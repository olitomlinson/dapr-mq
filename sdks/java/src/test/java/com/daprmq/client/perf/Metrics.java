package com.daprmq.client.perf;

import com.daprmq.client.perf.Profiles.QueueDrainParams;
import com.daprmq.client.perf.Profiles.SessionDrainParams;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.function.DoubleBinaryOperator;
import java.util.stream.Collectors;

/**
 * Turns raw operation, stream and handler records into the numbers of
 * sdks/testing/PERFORMANCE_TESTS.md. Pure, so it is unit tested; a port of LoadMetrics.cs,
 * SessionDrainMetrics.cs and QueueDrainMetrics.cs in sdks/dotnet/perf/DaprMQ.Client.Perf. Every map
 * is already in the result schema's shape.
 */
public final class Metrics {
    public static final int BUCKET_MS = 1000;

    private Metrics() {
    }

    /** count, mean and nearest-rank percentiles. */
    public static Map<String, Object> distribution(List<Double> values) {
        double[] sorted = values.stream().mapToDouble(Double::doubleValue).sorted().toArray();
        Map<String, Object> d = new LinkedHashMap<>();
        if (sorted.length == 0) {
            for (String name : List.of("count", "mean", "p50", "p95", "p99", "max")) {
                d.put(name, 0);
            }
            return d;
        }
        double sum = 0;
        for (double v : sorted) {
            sum += v;
        }
        d.put("count", sorted.length);
        d.put("mean", sum / sorted.length);
        d.put("p50", percentile(sorted, 0.50));
        d.put("p95", percentile(sorted, 0.95));
        d.put("p99", percentile(sorted, 0.99));
        d.put("max", sorted[sorted.length - 1]);
        return d;
    }

    private static double percentile(double[] sorted, double p) {
        return sorted[Math.min(Math.max((int) Math.ceil(p * sorted.length) - 1, 0), sorted.length - 1)];
    }

    private static double round3(double value) {
        return Math.round(value * 1000) / 1000.0;
    }

    // --- P-01..P-03 closed-loop load ---

    /** One operation; times are ms from the start of the step (warmup included). */
    public record OpRecord(double endMs, double latencyMs, int messages, boolean error) {
    }

    public record StepResult(Map<String, Object> step, Map<String, List<Object>> timeline) {
    }

    /** Only operations finishing inside [warmup, warmup + duration) count; errors are counted but left out of latency. */
    public static StepResult computeStep(int concurrency, int queues, List<OpRecord> ops, double warmupMs, double durationMs) {
        List<OpRecord> window = ops.stream().filter(o -> o.endMs() >= warmupMs && o.endMs() < warmupMs + durationMs).toList();
        double seconds = durationMs / 1000.0;
        int messages = window.stream().mapToInt(OpRecord::messages).sum();

        Map<String, Object> step = new LinkedHashMap<>();
        step.put("concurrency", concurrency);
        step.put("queues", queues);
        step.put("durationSeconds", seconds);
        step.put("ops", window.size());
        step.put("messages", messages);
        step.put("errors", (int) window.stream().filter(OpRecord::error).count());
        step.put("opsPerSecond", window.size() / seconds);
        step.put("messagesPerSecond", messages / seconds);
        step.put("latencyMs", distribution(window.stream().filter(o -> !o.error()).map(OpRecord::latencyMs).toList()));

        int bucketCount = (int) Math.ceil(durationMs / BUCKET_MS);
        List<List<OpRecord>> buckets = new ArrayList<>();
        for (int i = 0; i < bucketCount; i++) {
            buckets.add(new ArrayList<>());
        }
        for (OpRecord op : window) {
            buckets.get((int) ((op.endMs() - warmupMs) / BUCKET_MS)).add(op);
        }

        Map<String, List<Object>> timeline = new LinkedHashMap<>();
        timeline.put("opsPerSecond", buckets.stream().map(b -> (Object) b.size()).toList());
        timeline.put("messagesPerSecond", buckets.stream().map(b -> (Object) b.stream().mapToInt(OpRecord::messages).sum()).toList());
        for (String p : List.of("p50", "p95", "p99")) {
            timeline.put("latency" + p.toUpperCase() + "Ms", buckets.stream().map(b -> bucketPercentile(b, p)).collect(Collectors.toList()));
        }
        timeline.put("errors", buckets.stream().map(b -> (Object) (int) b.stream().filter(OpRecord::error).count()).toList());
        timeline.put("concurrency", buckets.stream().map(b -> (Object) concurrency).toList());
        return new StepResult(step, timeline);
    }

    private static Object bucketPercentile(List<OpRecord> bucket, String name) {
        List<Double> ok = bucket.stream().filter(o -> !o.error()).map(OpRecord::latencyMs).toList();
        return ok.isEmpty() ? null : round3(((Number) distribution(ok).get(name)).doubleValue());
    }

    // --- P-04 session drain ---

    /**
     * One consumeSession stream as a consumer slot saw it, ms from the start of the consume phase.
     * firstDeliveryMs/sessionId are null when the claim itself failed.
     */
    public record StreamRecord(double openMs, Double firstDeliveryMs, double endMs, String sessionId, String endReason) {
    }

    /** One handler invocation, ms from the start of the consume phase; delivery latency is publish -> handler start. */
    public record HandlerRecord(String sessionId, int seq, double startMs, double endMs, Double deliveryLatencyMs) {
        public HandlerRecord(String sessionId, int seq, double startMs, double endMs) {
            this(sessionId, seq, startMs, endMs, null);
        }
    }

    private record Delivered(StreamRecord stream, List<HandlerRecord> handlers) {
    }

    /** Where every slot-second in [from, to) went: handling + claim + in-session wait + drain wait + between streams = capacity. */
    private static Map<String, Object> breakdown(int slots, List<StreamRecord> streams, List<Delivered> delivered, double from, double to) {
        DoubleBinaryOperator clip = (a, b) -> Math.max(0, Math.min(b, to) - Math.max(a, from));
        double handling = 0, claim = 0, inSession = 0, drain = 0;

        for (StreamRecord s : streams) {
            if (s.firstDeliveryMs() == null) {
                claim += clip.applyAsDouble(s.openMs(), s.endMs());
            }
        }
        for (Delivered d : delivered) {
            StreamRecord s = d.stream();
            List<HandlerRecord> hs = d.handlers();
            double first = s.firstDeliveryMs();
            double lastHandlerEnd = hs.isEmpty() ? first : Math.min(hs.get(hs.size() - 1).endMs(), s.endMs());
            double streamHandling = hs.stream().mapToDouble(h -> clip.applyAsDouble(h.startMs(), h.endMs())).sum();
            claim += clip.applyAsDouble(s.openMs(), first);
            handling += streamHandling;
            inSession += Math.max(0, clip.applyAsDouble(first, lastHandlerEnd) - streamHandling);
            drain += clip.applyAsDouble(lastHandlerEnd, s.endMs());
        }

        double capacity = slots * Math.max(0, to - from);
        double inStreams = streams.stream().mapToDouble(s -> clip.applyAsDouble(s.openMs(), s.endMs())).sum();
        double capacitySeconds = capacity / 1000.0;
        double handlingSeconds = handling / 1000.0;
        Map<String, Object> b = new LinkedHashMap<>();
        b.put("startSeconds", from / 1000.0);
        b.put("endSeconds", to / 1000.0);
        b.put("capacitySlotSeconds", capacitySeconds);
        b.put("handlingSeconds", handlingSeconds);
        b.put("claimSeconds", claim / 1000.0);
        b.put("inSessionWaitSeconds", inSession / 1000.0);
        b.put("drainWaitSeconds", drain / 1000.0);
        b.put("betweenStreamsSeconds", Math.max(0, capacity - inStreams) / 1000.0);
        b.put("idleSlotSeconds", capacitySeconds - handlingSeconds);
        b.put("utilization", capacitySeconds > 0 ? handlingSeconds / capacitySeconds : 0.0);
        return b;
    }

    /** Average busy handlers per bucket, from [start, end) intervals. */
    private static List<Double> busyTimeline(List<double[]> intervals, int buckets) {
        double[] busy = new double[buckets];
        for (double[] interval : intervals) {
            for (int b = (int) (interval[0] / BUCKET_MS); b < buckets && b * (double) BUCKET_MS < interval[1]; b++) {
                busy[b] += Math.max(0, Math.min(interval[1], (b + 1) * (double) BUCKET_MS) - Math.max(interval[0], b * (double) BUCKET_MS));
            }
        }
        List<Double> timeline = new ArrayList<>();
        for (double ms : busy) {
            timeline.add(round3(ms / BUCKET_MS));
        }
        return timeline;
    }

    /** The P-04 metrics, plus timelineBucketMs and busySlotsTimeline (moved into the timeline when recorded). */
    public static Map<String, Object> computeSessionDrain(SessionDrainParams scenario, List<StreamRecord> streams, List<HandlerRecord> handlers,
                                                          double completedMs, double seedSeconds) {
        double ideal = scenario.idealSeconds();
        double wall = completedMs / 1000.0;

        Map<String, List<HandlerRecord>> bySession = new HashMap<>();
        for (HandlerRecord h : handlers) {
            bySession.computeIfAbsent(h.sessionId(), k -> new ArrayList<>()).add(h);
        }
        bySession.values().forEach(hs -> hs.sort(Comparator.comparingDouble(HandlerRecord::startMs)));

        List<Delivered> delivered = streams.stream()
                .filter(s -> s.firstDeliveryMs() != null && s.sessionId() != null)
                .map(s -> new Delivered(s, bySession.getOrDefault(s.sessionId(), List.of()).stream()
                        .filter(h -> h.startMs() >= s.firstDeliveryMs() && h.startMs() <= s.endMs()).toList()))
                .toList();
        double firstHandlerStart = handlers.stream().mapToDouble(HandlerRecord::startMs).min().orElse(0);

        // Peak = first delivery until the last session gets its first delivery; after that the slots
        // going idle is the unavoidable tail, not a claim/drain cost.
        double lastSessionStart = bySession.values().stream().mapToDouble(hs -> hs.get(0).startMs()).max().orElse(0);
        double peakEnd = lastSessionStart > firstHandlerStart ? lastSessionStart : completedMs;

        List<Double> gaps = new ArrayList<>();
        for (Delivered d : delivered) {
            for (int i = 1; i < d.handlers().size(); i++) {
                gaps.add(d.handlers().get(i).startMs() - d.handlers().get(i - 1).endMs());
            }
        }
        Set<String> unique = handlers.stream().map(h -> h.sessionId() + "\u0000" + h.seq()).collect(Collectors.toSet());
        Map<String, Integer> claims = new HashMap<>();
        delivered.forEach(d -> claims.merge(d.stream().sessionId(), 1, Integer::sum));
        int fifoViolations = 0;
        for (List<HandlerRecord> hs : bySession.values()) {
            for (int i = 1; i < hs.size(); i++) {
                if (hs.get(i).seq() < hs.get(i - 1).seq()) {
                    fifoViolations++;
                }
            }
        }

        Map<String, Object> r = new LinkedHashMap<>();
        r.put("seedSeconds", seedSeconds);
        r.put("wallClockSeconds", wall);
        r.put("idealSeconds", ideal);
        r.put("efficiency", wall > 0 ? ideal / wall : 0.0);
        r.put("timeToFirstMessageSeconds", firstHandlerStart / 1000.0);
        r.put("tailSeconds", (completedMs - peakEnd) / 1000.0);
        r.put("peak", breakdown(scenario.maxConcurrentSessions(), streams, delivered, firstHandlerStart, peakEnd));
        r.put("overall", breakdown(scenario.maxConcurrentSessions(), streams, delivered, 0, completedMs));
        r.put("claimLatencyMs", distribution(delivered.stream().map(d -> d.stream().firstDeliveryMs() - d.stream().openMs()).toList()));
        r.put("drainWaitMs", distribution(delivered.stream().filter(d -> !d.handlers().isEmpty())
                .map(d -> d.stream().endMs() - d.handlers().get(d.handlers().size() - 1).endMs()).toList()));
        r.put("interMessageGapMs", distribution(gaps));
        r.put("deliveryLatencyMs", distribution(handlers.stream().map(HandlerRecord::deliveryLatencyMs).filter(v -> v != null).toList()));
        r.put("streams", streams.size());
        r.put("failedClaims", (int) streams.stream().filter(s -> s.firstDeliveryMs() == null).count());
        r.put("sessionsClaimedMoreThanOnce", (int) claims.values().stream().filter(n -> n > 1).count());
        r.put("messagesHandled", handlers.size());
        r.put("duplicates", handlers.size() - unique.size());
        r.put("missing", Math.max(0, scenario.sessions() * scenario.messagesPerSession() - unique.size()));
        r.put("fifoViolations", fifoViolations);
        r.put("timelineBucketMs", BUCKET_MS);
        r.put("busySlotsTimeline", busyTimeline(handlers.stream().map(h -> new double[] {h.startMs(), h.endMs()}).toList(),
                (int) Math.ceil(completedMs / BUCKET_MS)));
        return r;
    }

    // --- P-05 queue drain ---

    public record QueueHandlerRecord(int seq, double startMs, double endMs, double deliveryLatencyMs) {
    }

    private static int peakOverlap(List<QueueHandlerRecord> handled) {
        // Ends sort before starts at the same instant: back-to-back handlers don't overlap.
        List<double[]> events = new ArrayList<>();
        for (QueueHandlerRecord h : handled) {
            events.add(new double[] {h.startMs(), 1});
            events.add(new double[] {h.endMs(), -1});
        }
        events.sort(Comparator.<double[]>comparingDouble(e -> e[0]).thenComparingDouble(e -> e[1]));
        int running = 0, peak = 0;
        for (double[] e : events) {
            running += (int) e[1];
            peak = Math.max(peak, running);
        }
        return peak;
    }

    /** The P-05 metrics, plus messagesPerSecondTimeline and busyHandlersTimeline (moved into the timeline when recorded). */
    public static Map<String, Object> computeQueueDrain(QueueDrainParams p, List<QueueHandlerRecord> handled, double completedMs, double seedSeconds) {
        double wall = completedMs / 1000;
        int unique = (int) handled.stream().map(QueueHandlerRecord::seq).distinct().count();
        Double ideal = p.idealSeconds();

        // Handler starts in time order; a message started after a later one is out of queue order.
        List<Integer> starts = handled.stream().sorted(Comparator.comparingDouble(QueueHandlerRecord::startMs)).map(QueueHandlerRecord::seq).toList();
        int orderViolations = 0;
        for (int i = 1; i < starts.size(); i++) {
            if (starts.get(i) < starts.get(i - 1)) {
                orderViolations++;
            }
        }

        int buckets = Math.max(1, (int) Math.ceil(completedMs / BUCKET_MS));
        int[] finished = new int[buckets];
        for (QueueHandlerRecord h : handled) {
            finished[Math.min((int) (h.endMs() / BUCKET_MS), buckets - 1)]++;
        }

        Map<String, Object> r = new LinkedHashMap<>();
        r.put("seedSeconds", seedSeconds);
        r.put("wallClockSeconds", wall);
        r.put("messagesPerSecond", wall > 0 ? unique / wall : 0.0);
        r.put("idealSeconds", ideal);
        r.put("efficiency", ideal != null && wall > 0 ? ideal / wall : null);
        r.put("timeToFirstMessageSeconds", handled.stream().mapToDouble(QueueHandlerRecord::startMs).min().orElse(0) / 1000);
        r.put("peakConcurrentHandlers", peakOverlap(handled));
        r.put("deliveryLatencyMs", distribution(handled.stream().map(QueueHandlerRecord::deliveryLatencyMs).toList()));
        r.put("messagesHandled", unique);
        r.put("duplicates", handled.size() - unique);
        r.put("missing", p.messages() - unique);
        r.put("orderViolations", orderViolations);
        r.put("messagesPerSecondTimeline", java.util.Arrays.stream(finished).boxed().toList());
        r.put("busyHandlersTimeline", busyTimeline(handled.stream().map(h -> new double[] {h.startMs(), h.endMs()}).toList(), buckets));
        return r;
    }
}
