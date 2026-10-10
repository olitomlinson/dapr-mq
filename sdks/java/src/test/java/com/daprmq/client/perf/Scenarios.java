package com.daprmq.client.perf;

import com.daprmq.client.DaprMQClient;
import com.daprmq.client.QueueConsumer;
import com.daprmq.client.QueueConsumerOptions;
import com.daprmq.client.RecordingSessionClient;
import com.daprmq.client.SessionQueueConsumer;
import com.daprmq.client.SessionQueueConsumerOptions;
import com.daprmq.client.perf.Metrics.HandlerRecord;
import com.daprmq.client.perf.Metrics.OpRecord;
import com.daprmq.client.perf.Metrics.QueueHandlerRecord;
import com.daprmq.client.perf.Metrics.StepResult;
import com.daprmq.client.perf.Profiles.LoadParams;
import com.daprmq.client.perf.Profiles.QueueDrainParams;
import com.daprmq.client.perf.Profiles.SessionDrainParams;
import com.daprmq.client.perf.Records.LoadResult;
import com.daprmq.client.types.DequeueLockedItem;
import com.daprmq.client.types.DequeueLockedResult;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.EnqueueResult;
import io.grpc.ManagedChannel;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.Random;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Supplier;

/**
 * The scenarios, driving the public SDK surface (DaprMQClient, SessionQueueConsumer, QueueConsumer).
 * Ports of LoadScenario.cs, SessionDrainScenario.cs and QueueDrainScenario.cs in
 * sdks/dotnet/perf/DaprMQ.Client.Perf. Workers and publishers are threads, since the client is blocking.
 */
public final class Scenarios {
    private static final int SEED_BATCH = 100;
    private static final int SEED_PARALLELISM = 16;
    private static final int MAX_SAMPLE_ERRORS = 5;

    private Scenarios() {
    }

    /** Milliseconds on a monotonic clock since start(). */
    static final class Clock {
        private volatile long startedAt = -1;

        void start() {
            startedAt = System.nanoTime();
        }

        double ms() {
            return startedAt < 0 ? 0 : (System.nanoTime() - startedAt) / 1e6;
        }
    }

    /**
     * Delay before each message: the first after a random 0..jitter start offset (so publishers don't
     * run in lockstep), each later one after interval + random 0..jitter.
     */
    public static int[] publishDelays(int messages, int intervalMs, int jitterMs, Random random) {
        int[] delays = new int[messages];
        for (int i = 0; i < messages; i++) {
            delays[i] = (i == 0 ? 0 : intervalMs) + (jitterMs > 0 ? random.nextInt(jitterMs + 1) : 0);
        }
        return delays;
    }

    interface Job {
        void run() throws Exception;
    }

    /** Runs the jobs on up to {@code limit} threads and rethrows the first failure. */
    static void runLimited(int limit, List<Job> jobs) {
        ExecutorService pool = Executors.newFixedThreadPool(Math.max(1, Math.min(limit, jobs.size())));
        try {
            List<Future<Object>> futures = new ArrayList<>();
            for (Job job : jobs) {
                futures.add(pool.submit(() -> {
                    job.run();
                    return null;
                }));
            }
            for (Future<Object> f : futures) {
                f.get();
            }
        } catch (Exception e) {
            throw new IllegalStateException(e.getCause() != null ? e.getCause() : e);
        } finally {
            pool.shutdownNow();
        }
    }

    private static Thread startThread(String name, Runnable body) {
        Thread thread = new Thread(body, name);
        thread.start();
        return thread;
    }

    static void sleep(long millis) {
        try {
            Thread.sleep(millis);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException(e);
        }
    }

    private static ScheduledExecutorService progress(Supplier<String> describe) {
        ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor(r -> {
            Thread t = new Thread(r, "perf-progress");
            t.setDaemon(true);
            return t;
        });
        timer.scheduleAtFixedRate(() -> System.out.println("  " + describe.get()), 30, 30, TimeUnit.SECONDS);
        return timer;
    }

    private static void await(CountDownLatch done, double timeoutSeconds, Supplier<String> describe) throws InterruptedException {
        if (!done.await((long) (timeoutSeconds * 1000), TimeUnit.MILLISECONDS)) {
            throw new IllegalStateException(describe.get());
        }
    }

    private static void requireEnqueued(EnqueueResult result, int expected, String what) {
        if (!result.success() || result.itemsEnqueued() != expected) {
            throw new IllegalStateException(what + " returned " + result.itemsEnqueued() + "/" + expected + ": " + result.message());
        }
    }

    // --- P-01..P-03 closed-loop load ---

    /**
     * Each step runs {@code concurrency} workers back to back on one shared client for warmup +
     * duration, worker w on queue w % queues. A ramp is a sequence of steps, each on fresh queues.
     */
    public static LoadResult load(DaprMQClient client, LoadParams load) throws InterruptedException {
        List<Map<String, Object>> steps = new ArrayList<>();
        List<Map<String, List<Object>>> timelines = new ArrayList<>();
        List<String> errors = Collections.synchronizedList(new ArrayList<>());
        boolean drained = false;
        String pad = "x".repeat(load.payloadBytes());

        for (int concurrency : load.concurrency()) {
            int queues = load.queuesAt(concurrency);
            String runId = UUID.randomUUID().toString().replace("-", "").substring(0, 12);
            // No "-session-" in the base id: QueueActor treats that marker as a per-session actor.
            List<String> queueIds = new ArrayList<>();
            for (int q = 0; q < queues; q++) {
                queueIds.add("perf-" + load.scenario() + "-" + runId + "-q" + q);
            }

            if (Profiles.DEQUEUE_ACK.equals(load.scenario())) {
                long seedStart = System.nanoTime();
                seed(client, load, queueIds, pad);
                System.out.printf("  seeded %d x %d in %.1fs%n", queues, load.seedPerQueue(), (System.nanoTime() - seedStart) / 1e9);
            }

            double warmupMs = load.warmupSeconds() * 1000.0;
            double durationMs = load.durationSeconds() * 1000.0;
            Clock clock = new Clock();
            clock.start();
            ConcurrentLinkedQueue<OpRecord> records = new ConcurrentLinkedQueue<>();
            AtomicBoolean stepDrained = new AtomicBoolean();

            List<Thread> workers = new ArrayList<>();
            for (int w = 0; w < concurrency; w++) {
                int worker = w;
                String queueId = queueIds.get(w % queues);
                workers.add(startThread("perf-worker-" + w, () -> {
                    List<OpRecord> mine = new ArrayList<>(4096);
                    int seq = 0;
                    while (clock.ms() < warmupMs + durationMs) {
                        double start = clock.ms();
                        int messages;
                        boolean error = false;
                        try {
                            messages = operation(client, load, queueId, worker, seq, pad);
                            seq += Math.max(messages, 1);
                        } catch (Exception e) {
                            messages = 0;
                            error = true;
                            if (errors.size() < MAX_SAMPLE_ERRORS) {
                                errors.add(e.getClass().getSimpleName() + ": " + e.getMessage());
                            }
                        }
                        if (messages < 0) {
                            stepDrained.set(true);
                            break;
                        }
                        double end = clock.ms();
                        mine.add(new OpRecord(end, end - start, messages, error));
                    }
                    records.addAll(mine);
                }));
            }
            for (Thread worker : workers) {
                worker.join();
            }

            StepResult result = Metrics.computeStep(concurrency, queues, new ArrayList<>(records), warmupMs, durationMs);
            steps.add(result.step());
            timelines.add(result.timeline());
            drained |= stepDrained.get();
            @SuppressWarnings("unchecked")
            Map<String, Object> latency = (Map<String, Object>) result.step().get("latencyMs");
            System.out.printf("  c=%-4d q=%-4d %10.0f msg/s  p50 %7.2f  p95 %7.2f  p99 %7.2f ms  errors %s%s%n",
                    concurrency, queues, ((Number) result.step().get("messagesPerSecond")).doubleValue(),
                    ((Number) latency.get("p50")).doubleValue(), ((Number) latency.get("p95")).doubleValue(), ((Number) latency.get("p99")).doubleValue(),
                    result.step().get("errors"), stepDrained.get() ? "  DRAINED" : "");
        }
        return new LoadResult(steps, timelines, drained, new ArrayList<>(errors));
    }

    /** One operation: the messages it moved, or -1 when a dequeue found the queue empty. */
    private static int operation(DaprMQClient client, LoadParams load, String queueId, int worker, int seq, String pad) {
        if (Profiles.DEQUEUE_ACK.equals(load.scenario())) {
            DequeueLockedResult result = client.dequeueLocked(queueId, load.dequeueCount(), 30, null);
            if (result == null || result.items().isEmpty()) {
                return -1;
            }
            for (DequeueLockedItem item : result.items()) {
                client.acknowledge(queueId, item.lockId());
            }
            return result.items().size();
        }

        int batch = Profiles.ENQUEUE_BATCH.equals(load.scenario()) ? load.batchSize() : 1;
        requireEnqueued(client.enqueue(queueId, items(worker, seq, batch, pad)), batch, "Enqueue");
        return batch;
    }

    private static List<EnqueueItem> items(int worker, int seq, int count, String pad) {
        long publishedAt = System.currentTimeMillis();
        List<EnqueueItem> items = new ArrayList<>(count);
        for (int n = seq; n < seq + count; n++) {
            items.add(new EnqueueItem(Map.of("worker", worker, "seq", n, "publishedAt", publishedAt, "pad", pad)));
        }
        return items;
    }

    private static void seed(DaprMQClient client, LoadParams load, List<String> queueIds, String pad) {
        int perQueue = load.seedPerQueue();
        List<Job> jobs = new ArrayList<>();
        // Batches for one queue go in order (one actor serialises them anyway); queues in parallel.
        for (int q = 0; q < queueIds.size(); q++) {
            int queue = q;
            String queueId = queueIds.get(q);
            jobs.add(() -> {
                for (int start = 0; start < perQueue; start += SEED_BATCH) {
                    int count = Math.min(SEED_BATCH, perQueue - start);
                    requireEnqueued(client.enqueue(queueId, items(queue, start, count, pad)), count, "Seeding " + queueId);
                }
            });
        }
        runLimited(SEED_PARALLELISM, jobs);
    }

    // --- P-04 session drain ---

    private static String sessionId(int session) {
        return String.format("s%05d", session);
    }

    /**
     * Seed sessions x messages, then drain them with one SessionQueueConsumer whose handler takes
     * settleMs per message. Completion is the moment every (session, seq) has been handled once.
     */
    public static Map<String, Object> sessionDrain(DaprMQClient client, ManagedChannel channel, SessionDrainParams s) throws InterruptedException {
        String queueId = "perf-drain-" + UUID.randomUUID().toString().replace("-", "");
        System.out.printf("Queue %s: %s, prefetch %d, lease %ds, idle-timeout %ds, publish %s%n",
                queueId, s.key(), s.prefetchCount(), s.leaseSeconds(), s.sessionIdleTimeoutSeconds(), s.publishMode());

        // The consume clock: it starts with the consumer, which in concurrent mode is also when
        // publishing starts - so there the wall clock includes the publish.
        Clock clock = new Clock();
        RecordingSessionClient recorder = new RecordingSessionClient(channel, clock::ms);
        ConcurrentLinkedQueue<HandlerRecord> handlers = new ConcurrentLinkedQueue<>();
        Set<String> seen = ConcurrentHashMap.newKeySet();
        int expected = s.sessions() * s.messagesPerSession();
        CountDownLatch allHandled = new CountDownLatch(1);
        AtomicReference<Double> completedMs = new AtomicReference<>(); // set once, by the handler that completes the set

        SessionQueueConsumer consumer = new SessionQueueConsumer(recorder, queueId, new SessionQueueConsumerOptions()
                .maxConcurrentSessions(s.maxConcurrentSessions())
                .prefetchCount(s.prefetchCount())
                .leaseSeconds(s.leaseSeconds())
                .sessionIdleTimeoutSeconds(s.sessionIdleTimeoutSeconds())
                .drainTimeoutMillis(5000), ctx -> {
            double start = clock.ms();
            double deliveryLatencyMs = System.currentTimeMillis() - ctx.item().get("publishedAt").asLong();
            Thread.sleep(s.settleMs());
            double end = clock.ms();
            int seq = ctx.item().get("seq").asInt();
            handlers.add(new HandlerRecord(ctx.sessionId(), seq, start, end, deliveryLatencyMs));
            if (seen.add(ctx.sessionId() + "/" + seq) && seen.size() == expected) {
                completedMs.compareAndSet(null, end);
                allHandled.countDown();
            }
        });

        double timeoutSeconds = s.idealSeconds() * 3 + 600;
        Supplier<String> timedOut = () -> "Only " + seen.size() + "/" + expected + " messages handled after " + (long) timeoutSeconds + "s.";
        ScheduledExecutorService timer = progress(() -> String.format("t=%.0fs  handled %d/%d  streams %d",
                clock.ms() / 1000, seen.size(), expected, recorder.streams().size()));
        double seedSeconds;
        try {
            if ("concurrent".equals(s.publishMode())) {
                clock.start();
                consumer.start();
                try {
                    seedSeconds = seedSessions(client, queueId, s);
                    await(allHandled, timeoutSeconds, timedOut);
                } finally {
                    consumer.stop();
                }
            } else {
                seedSeconds = seedSessions(client, queueId, s);
                clock.start();
                consumer.start();
                try {
                    await(allHandled, timeoutSeconds, timedOut);
                } finally {
                    consumer.stop();
                }
            }
        } finally {
            timer.shutdownNow();
        }

        return Metrics.computeSessionDrain(s, recorder.streams(), new ArrayList<>(handlers), completedMs.get(), seedSeconds);
    }

    private static double seedSessions(DaprMQClient client, String queueId, SessionDrainParams s) {
        long start = System.nanoTime();
        List<Job> jobs = new ArrayList<>();
        int parallelism = SEED_PARALLELISM;
        if (s.rateLimited()) {
            // Every session publishes at once, one message at a time on its own schedule.
            parallelism = s.sessions();
            for (int session = 0; session < s.sessions(); session++) {
                String id = sessionId(session);
                jobs.add(() -> {
                    int[] delays = publishDelays(s.messagesPerSession(), s.publishIntervalMs(), s.publishJitterMs(), ThreadLocalRandom.current());
                    for (int seq = 0; seq < delays.length; seq++) {
                        sleep(delays[seq]);
                        enqueueSession(client, queueId, id, seq, 1);
                    }
                });
            }
        } else {
            for (int session = 0; session < s.sessions(); session++) {
                String id = sessionId(session);
                jobs.add(() -> enqueueSession(client, queueId, id, 0, s.messagesPerSession()));
            }
        }
        runLimited(parallelism, jobs);
        double seconds = (System.nanoTime() - start) / 1e9;
        System.out.printf("Seeded %d messages in %.1fs%n", s.sessions() * s.messagesPerSession(), seconds);
        return seconds;
    }

    private static void enqueueSession(DaprMQClient client, String queueId, String session, int from, int count) {
        // Same process publishes and consumes, so wall-clock ms is a consistent publish -> handler clock.
        long publishedAt = System.currentTimeMillis();
        List<EnqueueItem> items = new ArrayList<>(count);
        for (int seq = from; seq < from + count; seq++) {
            items.add(new EnqueueItem(Map.of("session", session, "seq", seq, "publishedAt", publishedAt), 1, null, session));
        }
        requireEnqueued(client.enqueue(queueId, items), count, "Publishing to session " + session);
    }

    // --- P-05 queue drain ---

    /**
     * Publish messages to a plain queue and drain them with one QueueConsumer whose handler takes
     * settleMs per message. Completion is the moment every seq has been handled once.
     */
    public static Map<String, Object> queueDrain(DaprMQClient client, QueueDrainParams s) throws InterruptedException {
        String queueId = "perf-queue-" + UUID.randomUUID().toString().replace("-", "");
        System.out.printf("Queue %s: %s%n", queueId, s.key());

        Clock clock = new Clock();
        ConcurrentLinkedQueue<QueueHandlerRecord> handled = new ConcurrentLinkedQueue<>();
        Set<Integer> seen = ConcurrentHashMap.newKeySet();
        CountDownLatch allHandled = new CountDownLatch(1);
        AtomicReference<Double> completedMs = new AtomicReference<>(); // set once, by the handler that completes the set

        QueueConsumer consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions()
                .maxActiveMessages(s.maxActiveMessages())
                .maxConcurrentHandlers(s.maxConcurrentHandlers())
                .strictOrder(s.strictOrder())
                .drainTimeoutMillis(5000), ctx -> {
            double start = clock.ms();
            double deliveryLatencyMs = System.currentTimeMillis() - ctx.item().get("publishedAt").asLong();
            int seq = ctx.item().get("seq").asInt();
            int settleMs = s.handlerMs(seq);
            if (settleMs > 0) {
                Thread.sleep(settleMs);
            }
            double end = clock.ms();
            handled.add(new QueueHandlerRecord(seq, start, end, deliveryLatencyMs));
            if (seen.add(seq) && seen.size() == s.messages()) {
                completedMs.compareAndSet(null, end);
                allHandled.countDown();
            }
        });

        Double ideal = s.idealSeconds();
        double timeoutSeconds = (ideal != null ? ideal : 0) * 3 + 600;
        ScheduledExecutorService timer = progress(() -> String.format("t=%.0fs  handled %d/%d", clock.ms() / 1000, seen.size(), s.messages()));
        double seedSeconds;
        try {
            // With a live publisher the clock starts with publishing, so the wall clock includes it.
            if (s.livePublish()) {
                clock.start();
                consumer.start();
                seedSeconds = publishLive(client, queueId, s);
            } else {
                seedSeconds = seedQueue(client, queueId, s);
                clock.start();
                consumer.start();
            }
            System.out.printf("Published %d messages in %.1fs%n", s.messages(), seedSeconds);
            try {
                await(allHandled, timeoutSeconds, () -> "Only " + seen.size() + "/" + s.messages() + " messages handled after " + (long) timeoutSeconds + "s.");
            } finally {
                consumer.stop();
            }
        } finally {
            timer.shutdownNow();
        }

        return Metrics.computeQueueDrain(s, new ArrayList<>(handled), completedMs.get(), seedSeconds);
    }

    private static double seedQueue(DaprMQClient client, String queueId, QueueDrainParams s) {
        long start = System.nanoTime();
        List<Job> jobs = new ArrayList<>();
        for (int from = 0; from < s.messages(); from += SEED_BATCH) {
            int batchStart = from;
            jobs.add(() -> enqueueQueue(client, queueId, batchStart, Math.min(SEED_BATCH, s.messages() - batchStart)));
        }
        // Parallel batches land out of seq order; strict order checks handler order against queue
        // order, so it seeds with one publisher.
        runLimited(s.strictOrder() ? 1 : SEED_PARALLELISM, jobs);
        return (System.nanoTime() - start) / 1e9;
    }

    private static double publishLive(DaprMQClient client, String queueId, QueueDrainParams s) {
        long start = System.nanoTime();
        int[] delays = publishDelays(s.messages(), s.publishIntervalMs(), s.publishJitterMs(), ThreadLocalRandom.current());
        for (int seq = 0; seq < delays.length; seq++) {
            sleep(delays[seq]);
            enqueueQueue(client, queueId, seq, 1);
        }
        return (System.nanoTime() - start) / 1e9;
    }

    private static void enqueueQueue(DaprMQClient client, String queueId, int from, int count) {
        long publishedAt = System.currentTimeMillis();
        List<EnqueueItem> items = new ArrayList<>(count);
        for (int seq = from; seq < from + count; seq++) {
            items.add(new EnqueueItem(Map.of("seq", seq, "publishedAt", publishedAt)));
        }
        requireEnqueued(client.enqueue(queueId, items), count, "Publishing to " + queueId);
    }
}
