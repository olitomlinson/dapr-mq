package com.daprmq.client.perf;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

/**
 * The profiles of sdks/testing/PERFORMANCE_TESTS.md, exactly as tabled there and in the .NET
 * reference (sdks/dotnet/perf/DaprMQ.Client.Perf/PerfOptions.cs). The {@code key()} strings group
 * runs across SDKs on the report, so they must match the .NET ones character for character
 * (sdks/testing/perf/profiles.json, checked by ProfilesTest).
 */
public final class Profiles {
    public static final String ENQUEUE = "enqueue";
    public static final String ENQUEUE_BATCH = "enqueue-batch";
    public static final String DEQUEUE_ACK = "dequeue-ack";

    private Profiles() {
    }

    /** P-01..P-03 closed-loop load. More than one concurrency value makes it a ramp; queues null = one per worker. */
    public record LoadParams(String scenario, int[] concurrency, Integer queues, int batchSize, int dequeueCount,
                             int payloadBytes, int seedPerQueue, int warmupSeconds, int durationSeconds) {
        public String id() {
            return switch (scenario) {
                case ENQUEUE -> "P-01";
                case ENQUEUE_BATCH -> "P-02";
                case DEQUEUE_ACK -> "P-03";
                default -> throw new IllegalStateException("Unknown load scenario " + scenario);
            };
        }

        public int queuesAt(int concurrency) {
            return queues != null ? queues : concurrency;
        }

        public String key() {
            String shape = DEQUEUE_ACK.equals(scenario) ? "/k" + dequeueCount + "/seed" + seedPerQueue : "/b" + batchSize;
            return scenario + ":c" + Arrays.stream(concurrency).mapToObj(String::valueOf).collect(Collectors.joining(","))
                    + "/q" + (queues != null ? queues.toString() : "=c") + shape
                    + "/" + payloadBytes + "B/" + warmupSeconds + "+" + durationSeconds + "s";
        }

        public Map<String, Object> params() {
            Map<String, Object> p = new LinkedHashMap<>();
            p.put("concurrency", Arrays.stream(concurrency).boxed().toList());
            p.put("queues", queues);
            p.put("batchSize", batchSize);
            p.put("dequeueCount", dequeueCount);
            p.put("payloadBytes", payloadBytes);
            p.put("seedPerQueue", seedPerQueue);
            p.put("warmupSeconds", warmupSeconds);
            p.put("durationSeconds", durationSeconds);
            return p;
        }
    }

    /** P-04: sessions x messages drained by one SessionQueueConsumer. */
    public record SessionDrainParams(int sessions, int messagesPerSession, int settleMs, int maxConcurrentSessions,
                                     int prefetchCount, int leaseSeconds, int sessionIdleTimeoutSeconds,
                                     String publishMode, int publishIntervalMs, int publishJitterMs) {
        /** The defaults: the extreme-scale "full" profile. */
        public static SessionDrainParams full() {
            return new SessionDrainParams(1000, 100, 1000, 20, 10, 30, 0, "before", 0, 0);
        }

        public SessionDrainParams with(int sessions, int messagesPerSession, int settleMs, int maxConcurrentSessions, int idleTimeoutSeconds) {
            return new SessionDrainParams(sessions, messagesPerSession, settleMs, maxConcurrentSessions, prefetchCount, leaseSeconds,
                    idleTimeoutSeconds, publishMode, publishIntervalMs, publishJitterMs);
        }

        public SessionDrainParams withPublish(String mode, int intervalMs, int jitterMs) {
            return new SessionDrainParams(sessions, messagesPerSession, settleMs, maxConcurrentSessions, prefetchCount, leaseSeconds,
                    sessionIdleTimeoutSeconds, mode, intervalMs, jitterMs);
        }

        public boolean rateLimited() {
            return publishIntervalMs > 0 || publishJitterMs > 0;
        }

        public String key() {
            return sessions + "x" + messagesPerSession + "@" + settleMs + "ms/" + maxConcurrentSessions + "slots"
                    + ("before".equals(publishMode) ? "" : "+" + publishMode)
                    + (rateLimited() ? "+pub" + publishIntervalMs + "ms~" + publishJitterMs + "ms" : "")
                    + (sessionIdleTimeoutSeconds != 0 ? "+idle" + sessionIdleTimeoutSeconds + "s" : "")
                    + (prefetchCount != 10 ? "+prefetch" + prefetchCount : "")
                    + (leaseSeconds != 30 ? "+lease" + leaseSeconds + "s" : "");
        }

        /**
         * Consumer-bound: ceil(sessions / slots) rounds of M x settle. Concurrent and rate limited, no
         * session can finish before its last message is published (~M x mean delay) and settled.
         */
        public double idealSeconds() {
            double consumeBound = Math.ceil(sessions / (double) maxConcurrentSessions) * messagesPerSession * settleMs / 1000.0;
            if (!"concurrent".equals(publishMode) || !rateLimited()) {
                return consumeBound;
            }
            double publishBound = (messagesPerSession * (publishIntervalMs + publishJitterMs / 2.0) + settleMs) / 1000.0;
            return Math.max(consumeBound, publishBound);
        }

        public Map<String, Object> params() {
            Map<String, Object> p = new LinkedHashMap<>();
            p.put("sessions", sessions);
            p.put("messagesPerSession", messagesPerSession);
            p.put("settleMs", settleMs);
            p.put("maxConcurrentSessions", maxConcurrentSessions);
            p.put("prefetchCount", prefetchCount);
            p.put("leaseSeconds", leaseSeconds);
            p.put("sessionIdleTimeoutSeconds", sessionIdleTimeoutSeconds);
            p.put("publishMode", publishMode);
            p.put("publishIntervalMs", publishIntervalMs);
            p.put("publishJitterMs", publishJitterMs);
            return p;
        }
    }

    /** P-05: messages drained by one QueueConsumer; a publish interval publishes alongside it. */
    public record QueueDrainParams(int messages, int settleMs, int maxActiveMessages, int maxConcurrentHandlers, boolean strictOrder,
                                   int publishIntervalMs, int publishJitterMs, int tailEvery, int tailMs) {
        public QueueDrainParams(int messages, int settleMs, int maxActiveMessages, int maxConcurrentHandlers) {
            this(messages, settleMs, maxActiveMessages, maxConcurrentHandlers, false, 0, 0, 0, 0);
        }

        public boolean livePublish() {
            return publishIntervalMs > 0 || publishJitterMs > 0;
        }

        public String key() {
            return "queue:" + messages + "@" + settleMs + "ms/active" + maxActiveMessages
                    + (maxConcurrentHandlers > 0 ? "/handlers" + maxConcurrentHandlers : "")
                    + (strictOrder ? "+strict" : "")
                    + (livePublish() ? "+pub" + publishIntervalMs + "ms~" + publishJitterMs + "ms" : "")
                    + (tailEvery > 0 ? "+tail" + tailMs + "ms/" + tailEvery : "");
        }

        /** Handlers that can run at once: strict order runs one, otherwise the window bounds them. */
        public int concurrency() {
            if (strictOrder) {
                return 1;
            }
            return maxConcurrentHandlers > 0 ? Math.min(maxConcurrentHandlers, maxActiveMessages) : maxActiveMessages;
        }

        public int tailMessages() {
            return tailEvery > 0 ? messages / tailEvery : 0;
        }

        public int handlerMs(int seq) {
            return tailEvery > 0 && (seq + 1) % tailEvery == 0 ? tailMs : settleMs;
        }

        /**
         * The handler work spread over concurrency(), but no less than the slowest single handler, nor
         * than the last live-published message plus its handler. Null for an instant handler.
         */
        public Double idealSeconds() {
            if (settleMs == 0 && tailMs == 0 && !livePublish()) {
                return null;
            }
            double workMs = (double) (messages - tailMessages()) * settleMs + (double) tailMessages() * tailMs;
            double slowestMs = tailMessages() > 0 ? Math.max(settleMs, tailMs) : settleMs;
            double lastPublishedMs = livePublish() ? (messages - 1) * (double) publishIntervalMs + handlerMs(messages - 1) : 0;
            return Math.max(Math.max(workMs / concurrency(), slowestMs), lastPublishedMs) / 1000;
        }

        public Map<String, Object> params() {
            Map<String, Object> p = new LinkedHashMap<>();
            p.put("messages", messages);
            p.put("settleMs", settleMs);
            p.put("maxActiveMessages", maxActiveMessages);
            p.put("maxConcurrentHandlers", maxConcurrentHandlers);
            p.put("strictOrder", strictOrder);
            p.put("publishIntervalMs", publishIntervalMs);
            p.put("publishJitterMs", publishJitterMs);
            p.put("tailEvery", tailEvery);
            p.put("tailMs", tailMs);
            return p;
        }
    }

    private static final int[] RAMP = {1, 2, 4, 8, 16, 32, 64};
    private static final int[] WIDE_RAMP = {1, 2, 4, 8, 16, 32, 64, 128, 256};

    public static final Map<String, LoadParams> LOAD = new LinkedHashMap<>();
    public static final Map<String, SessionDrainParams> SESSION_DRAIN = new LinkedHashMap<>();
    public static final Map<String, QueueDrainParams> QUEUE_DRAIN = new LinkedHashMap<>();
    public static final Map<String, List<String>> SUITES = new LinkedHashMap<>();

    static {
        LOAD.put("enqueue", new LoadParams(ENQUEUE, new int[] {8}, 8, 1, 1, 256, 0, 3, 15));
        LOAD.put("enqueue-hot", new LoadParams(ENQUEUE, new int[] {8}, 1, 1, 1, 256, 0, 3, 15));
        LOAD.put("enqueue-batch", new LoadParams(ENQUEUE_BATCH, new int[] {4}, 4, 100, 1, 256, 0, 3, 15));
        LOAD.put("dequeue-ack", new LoadParams(DEQUEUE_ACK, new int[] {8}, 8, 1, 1, 256, 4000, 3, 15));
        LOAD.put("enqueue-ramp", new LoadParams(ENQUEUE, WIDE_RAMP, null, 1, 1, 256, 0, 5, 30));
        LOAD.put("enqueue-hot-ramp", new LoadParams(ENQUEUE, RAMP, 1, 1, 1, 256, 0, 5, 30));
        LOAD.put("enqueue-batch-ramp", new LoadParams(ENQUEUE_BATCH, RAMP, null, 100, 1, 256, 0, 5, 30));
        LOAD.put("dequeue-ack-ramp", new LoadParams(DEQUEUE_ACK, WIDE_RAMP, null, 1, 1, 256, 12000, 5, 30));

        SessionDrainParams full = SessionDrainParams.full();
        SESSION_DRAIN.put("steady-drain", full.with(200, 20, 100, 20, 1));
        SESSION_DRAIN.put("session-churn", full.with(300, 2, 50, 20, 1));
        SESSION_DRAIN.put("deep-session", full.with(4, 1000, 10, 4, 1));
        SESSION_DRAIN.put("live-publish", full.with(20, 50, 100, 20, 1).withPublish("concurrent", 200, 100));
        SESSION_DRAIN.put("sdk-defaults", full.with(40, 5, 1000, 20, 0));
        SESSION_DRAIN.put("full", full);
        SESSION_DRAIN.put("wide-drain", full.with(2000, 10, 50, 200, 1));

        QUEUE_DRAIN.put("queue-drain-instant", new QueueDrainParams(4000, 0, 100, 0));
        QUEUE_DRAIN.put("queue-drain", new QueueDrainParams(4000, 10, 100, 0));
        QUEUE_DRAIN.put("queue-drain-slow", new QueueDrainParams(3000, 100, 100, 0));
        QUEUE_DRAIN.put("queue-strict-order", new QueueDrainParams(300, 0, 100, 0, true, 0, 0, 0, 0));
        QUEUE_DRAIN.put("queue-live-publish", new QueueDrainParams(50, 10, 100, 0, false, 200, 100, 0, 0));
        QUEUE_DRAIN.put("queue-drain-large", new QueueDrainParams(50000, 10, 500, 0));
        QUEUE_DRAIN.put("queue-drain-tail", new QueueDrainParams(2000, 100, 100, 0, false, 0, 0, 20, 5000));

        SUITES.put("pr", List.of(
                "enqueue", "enqueue-hot", "enqueue-batch", "dequeue-ack",
                "steady-drain", "session-churn", "deep-session", "live-publish", "sdk-defaults",
                "queue-drain-instant", "queue-drain", "queue-drain-slow", "queue-strict-order", "queue-live-publish"));
        SUITES.put("extreme", List.of(
                "enqueue-ramp", "enqueue-hot-ramp", "enqueue-batch-ramp", "dequeue-ack-ramp",
                "full", "wide-drain", "queue-drain-large", "queue-drain-tail"));
    }

    public static List<String> all() {
        List<String> all = new ArrayList<>(LOAD.keySet());
        all.addAll(SESSION_DRAIN.keySet());
        all.addAll(QUEUE_DRAIN.keySet());
        return all;
    }

    public static String scaleOf(String profile) {
        return SUITES.entrySet().stream().filter(e -> e.getValue().contains(profile)).map(Map.Entry::getKey).findFirst().orElse("adhoc");
    }
}
