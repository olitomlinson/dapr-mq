package com.daprmq.client.perf;

import com.daprmq.client.perf.Metrics.HandlerRecord;
import com.daprmq.client.perf.Metrics.OpRecord;
import com.daprmq.client.perf.Metrics.QueueHandlerRecord;
import com.daprmq.client.perf.Metrics.StepResult;
import com.daprmq.client.perf.Metrics.StreamRecord;
import com.daprmq.client.perf.Profiles.QueueDrainParams;
import com.daprmq.client.perf.Profiles.SessionDrainParams;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;

/** Ports of LoadMetricsTests, SessionDrainMetricsTests and QueueDrainMetricsTests (.NET). */
class MetricsTest {
    private static double d(Map<String, Object> map, String... path) {
        Object value = map;
        for (String key : path) {
            @SuppressWarnings("unchecked")
            Map<String, Object> m = (Map<String, Object>) value;
            value = m.get(key);
        }
        return ((Number) value).doubleValue();
    }

    // --- load ---

    @Test
    void onlyOperationsFinishingInsideTheRecordedWindowCount() {
        List<OpRecord> ops = List.of(new OpRecord(500, 99, 1, false), new OpRecord(1000, 10, 1, false), new OpRecord(1500, 20, 1, false),
                new OpRecord(2999, 30, 1, false), new OpRecord(3000, 99, 1, false));

        Map<String, Object> step = Metrics.computeStep(2, 2, ops, 1000, 2000).step();

        assertEquals(3, d(step, "ops"));
        assertEquals(1.5, d(step, "opsPerSecond"), 1e-9);
        assertEquals(20, d(step, "latencyMs", "p50"));
        assertEquals(30, d(step, "latencyMs", "max"));
        assertEquals(2, d(step, "durationSeconds"));
    }

    @Test
    void messagesCountBatchItemsAndErrorsAreCountedButLeftOutOfLatency() {
        Map<String, Object> step = Metrics.computeStep(1, 1,
                List.of(new OpRecord(100, 10, 100, false), new OpRecord(200, 5000, 0, true), new OpRecord(300, 20, 100, false)), 0, 1000).step();

        assertEquals(List.of(3.0, 200.0, 1.0), List.of(d(step, "ops"), d(step, "messages"), d(step, "errors")));
        assertEquals(200, d(step, "messagesPerSecond"), 1e-9);
        assertEquals(2, d(step, "latencyMs", "count"));
        assertEquals(20, d(step, "latencyMs", "max"));
    }

    @Test
    void timelineBucketsBySecondOfCompletionWithNullPercentilesForEmptySeconds() {
        StepResult result = Metrics.computeStep(4, 4, List.of(new OpRecord(1100, 10, 1, false), new OpRecord(1900, 30, 1, false),
                new OpRecord(3500, 40, 2, false), new OpRecord(3600, 50, 0, true)), 1000, 3000);

        assertEquals(List.of(2, 0, 2), result.timeline().get("opsPerSecond"));
        assertEquals(List.of(2, 0, 2), result.timeline().get("messagesPerSecond"));
        assertEquals(Arrays.asList(30.0, null, 40.0), result.timeline().get("latencyP95Ms"));
        assertEquals(List.of(0, 0, 1), result.timeline().get("errors"));
        assertEquals(List.of(4, 4, 4), result.timeline().get("concurrency"));
    }

    @Test
    void percentilesUseNearestRank() {
        List<Double> values = new ArrayList<>();
        for (int i = 1; i <= 100; i++) {
            values.add((double) i);
        }
        Map<String, Object> dist = Metrics.distribution(values);
        assertEquals(List.of(50.0, 95.0, 99.0, 100.0), List.of(d(dist, "p50"), d(dist, "p95"), d(dist, "p99"), d(dist, "max")));
    }

    // --- session drain ---

    private static SessionDrainParams scenario(int sessions, int messages, int slots) {
        return SessionDrainParams.full().with(sessions, messages, 1000, slots, 0);
    }

    private static StreamRecord stream(String sessionId, double open, Double first, double end) {
        return new StreamRecord(open, first, end, sessionId, "completed");
    }

    @Test
    void perfectlyPackedSlotsAreFullyUtilised() {
        Map<String, Object> r = Metrics.computeSessionDrain(scenario(2, 2, 2), List.of(stream("a", 0, 0.0, 2000), stream("b", 0, 0.0, 2000)),
                List.of(new HandlerRecord("a", 0, 0, 1000), new HandlerRecord("a", 1, 1000, 2000), new HandlerRecord("b", 0, 0, 1000), new HandlerRecord("b", 1, 1000, 2000)),
                2000, 0.5);

        assertEquals(2, d(r, "wallClockSeconds"), 1e-9);
        assertEquals(2, d(r, "idealSeconds"), 1e-9);
        assertEquals(1, d(r, "efficiency"), 1e-9);
        assertEquals(0.5, d(r, "seedSeconds"));
        assertEquals(1, d(r, "peak", "utilization"), 1e-9);
        assertEquals(0, d(r, "peak", "idleSlotSeconds"), 1e-9);
        assertEquals(4, d(r, "messagesHandled"));
    }

    @Test
    void idleSlotTimeIsAttributedToClaimDrainAndBetweenStreams() {
        Map<String, Object> r = Metrics.computeSessionDrain(scenario(2, 1, 1), List.of(stream("a", 0, 100.0, 4100), stream("b", 4300, 4400.0, 8400)),
                List.of(new HandlerRecord("a", 0, 100, 1100), new HandlerRecord("b", 0, 4400, 5400)), 5400, 0);

        assertEquals(0.1, d(r, "peak", "startSeconds"), 1e-9);
        assertEquals(4.4, d(r, "peak", "endSeconds"), 1e-9);
        assertEquals(4.3, d(r, "peak", "capacitySlotSeconds"), 1e-9);
        assertEquals(1.0, d(r, "peak", "handlingSeconds"), 1e-9);
        assertEquals(0.1, d(r, "peak", "claimSeconds"), 1e-9);
        assertEquals(3.0, d(r, "peak", "drainWaitSeconds"), 1e-9);
        assertEquals(0.2, d(r, "peak", "betweenStreamsSeconds"), 1e-9);
        assertEquals(0.0, d(r, "peak", "inSessionWaitSeconds"), 1e-9);
        assertEquals(3.3, d(r, "peak", "idleSlotSeconds"), 1e-9);
        assertEquals(1.0 / 4.3, d(r, "peak", "utilization"), 1e-9);
        assertEquals(5.4, d(r, "overall", "capacitySlotSeconds"), 1e-9);
        assertEquals(2.0, d(r, "overall", "handlingSeconds"), 1e-9);
        assertEquals(0.2, d(r, "overall", "claimSeconds"), 1e-9);
        assertEquals(3.0, d(r, "overall", "drainWaitSeconds"), 1e-9);
        assertEquals(0.2, d(r, "overall", "betweenStreamsSeconds"), 1e-9);
        assertEquals(1.0, d(r, "tailSeconds"), 1e-9);
        assertEquals(0.1, d(r, "timeToFirstMessageSeconds"), 1e-9);
        assertEquals(2, d(r, "claimLatencyMs", "count"));
        assertEquals(100, d(r, "claimLatencyMs", "p50"), 1e-9);
        assertEquals(3000, d(r, "drainWaitMs", "max"), 1e-9);
    }

    @Test
    void gapsBetweenHandlersInOneStreamAreInSessionWait() {
        Map<String, Object> r = Metrics.computeSessionDrain(scenario(1, 2, 1), List.of(stream("a", 0, 0.0, 2500)),
                List.of(new HandlerRecord("a", 0, 0, 1000), new HandlerRecord("a", 1, 1500, 2500)), 2500, 0);

        assertEquals(0.5, d(r, "overall", "inSessionWaitSeconds"), 1e-9);
        assertEquals(1, d(r, "interMessageGapMs", "count"));
        assertEquals(500, d(r, "interMessageGapMs", "p50"), 1e-9);
    }

    @Test
    void aStreamThatNeverDeliveredIsAFailedClaimAndReclaimsAreCounted() {
        Map<String, Object> r = Metrics.computeSessionDrain(scenario(1, 2, 1),
                List.of(new StreamRecord(0, null, 300, null, "NoSessionsAvailableException"), stream("a", 300, 400.0, 1400), stream("a", 1400, 1500.0, 2500)),
                List.of(new HandlerRecord("a", 0, 400, 1400), new HandlerRecord("a", 1, 1500, 2500)), 2500, 0);

        assertEquals(1, d(r, "failedClaims"));
        assertEquals(3, d(r, "streams"));
        assertEquals(0.5, d(r, "overall", "claimSeconds"), 1e-9);
        assertEquals(1, d(r, "sessionsClaimedMoreThanOnce"));
    }

    @Test
    void orderingDuplicatesAndMissingMessagesAreDetected() {
        Map<String, Object> r = Metrics.computeSessionDrain(scenario(2, 3, 2), List.of(), List.of(
                new HandlerRecord("a", 0, 0, 10), new HandlerRecord("a", 2, 10, 20), new HandlerRecord("a", 1, 20, 30),
                new HandlerRecord("b", 0, 0, 10), new HandlerRecord("b", 0, 10, 20)), 30, 0);

        assertEquals(List.of(1.0, 1.0, 2.0, 5.0), List.of(d(r, "fifoViolations"), d(r, "duplicates"), d(r, "missing"), d(r, "messagesHandled")));
    }

    @Test
    void deliveryLatencyIsDistributedOverHandlersThatRecordedIt() {
        Map<String, Object> r = Metrics.computeSessionDrain(scenario(1, 3, 1), List.of(stream("a", 0, 0.0, 30)), List.of(
                new HandlerRecord("a", 0, 0, 10, 100.0), new HandlerRecord("a", 1, 10, 20, 300.0), new HandlerRecord("a", 2, 20, 30)), 30, 0);

        assertEquals(List.of(2.0, 100.0, 300.0), List.of(d(r, "deliveryLatencyMs", "count"), d(r, "deliveryLatencyMs", "p50"), d(r, "deliveryLatencyMs", "max")));
    }

    @Test
    void sessionTimelineReportsAverageBusySlotsPerSecond() {
        Map<String, Object> r = Metrics.computeSessionDrain(scenario(1, 1, 1), List.of(stream("a", 0, 500.0, 2500)), List.of(new HandlerRecord("a", 0, 500, 2500)), 2500, 0);

        assertEquals(1000, d(r, "timelineBucketMs"));
        assertEquals(List.of(0.5, 1.0, 0.5), r.get("busySlotsTimeline"));
    }

    // --- queue drain ---

    private static QueueDrainParams queue(int messages, int settleMs, boolean strict) {
        return new QueueDrainParams(messages, settleMs, 100, 2, strict, 0, 0, 0, 0);
    }

    @Test
    void throughputWallClockAndFirstMessageComeFromTheHandlerRecords() {
        Map<String, Object> r = Metrics.computeQueueDrain(queue(4, 1000, false), List.of(new QueueHandlerRecord(0, 100, 1100, 50),
                new QueueHandlerRecord(1, 120, 1120, 70), new QueueHandlerRecord(2, 1100, 2100, 1050), new QueueHandlerRecord(3, 1120, 2000, 1070)), 2100, 0.5);

        assertEquals(2.1, d(r, "wallClockSeconds"), 1e-9);
        assertEquals(4 / 2.1, d(r, "messagesPerSecond"), 1e-9);
        assertEquals(0.1, d(r, "timeToFirstMessageSeconds"), 1e-9);
        assertEquals(0.5, d(r, "seedSeconds"));
        assertEquals(List.of(4.0, 1070.0), List.of(d(r, "deliveryLatencyMs", "count"), d(r, "deliveryLatencyMs", "max")));
        assertEquals(List.of(4.0, 0.0, 0.0), List.of(d(r, "messagesHandled"), d(r, "missing"), d(r, "duplicates")));
    }

    @Test
    void queueEfficiencyIsIdealOverWallClockAndUnsetForAnInstantHandler() {
        Map<String, Object> r = Metrics.computeQueueDrain(queue(4, 1000, false), List.of(new QueueHandlerRecord(0, 0, 1000, 0),
                new QueueHandlerRecord(1, 0, 1000, 0), new QueueHandlerRecord(2, 1000, 2000, 0), new QueueHandlerRecord(3, 1000, 2500, 0)), 2500, 0);
        assertEquals(2, d(r, "idealSeconds"), 1e-9);
        assertEquals(0.8, d(r, "efficiency"), 1e-9);

        Map<String, Object> instant = Metrics.computeQueueDrain(queue(4, 0, false), List.of(new QueueHandlerRecord(0, 0, 1, 0)), 1, 0);
        assertNull(instant.get("idealSeconds"));
        assertNull(instant.get("efficiency"));
    }

    @Test
    void peakConcurrentHandlersIsTheMostRunningAtOnce() {
        Map<String, Object> r = Metrics.computeQueueDrain(queue(4, 1000, false), List.of(new QueueHandlerRecord(0, 0, 1000, 0),
                new QueueHandlerRecord(1, 100, 900, 0), new QueueHandlerRecord(2, 200, 300, 0), new QueueHandlerRecord(3, 1000, 1500, 0)), 1500, 0);
        assertEquals(3, d(r, "peakConcurrentHandlers"));
    }

    @Test
    void queueMissingDuplicatesAndOrderViolationsAreCounted() {
        Map<String, Object> r = Metrics.computeQueueDrain(queue(4, 1000, false),
                List.of(new QueueHandlerRecord(0, 0, 10, 0), new QueueHandlerRecord(0, 20, 30, 0), new QueueHandlerRecord(2, 40, 50, 0)), 50, 0);
        assertEquals(List.of(2.0, 1.0, 2.0), List.of(d(r, "messagesHandled"), d(r, "duplicates"), d(r, "missing")));

        Map<String, Object> ordered = Metrics.computeQueueDrain(queue(4, 1000, true), List.of(new QueueHandlerRecord(0, 0, 10, 0),
                new QueueHandlerRecord(2, 10, 20, 0), new QueueHandlerRecord(1, 20, 30, 0), new QueueHandlerRecord(3, 30, 40, 0)), 40, 0);
        assertEquals(1, d(ordered, "orderViolations"));
    }

    @Test
    void queueTimelineCountsMessagesFinishedAndAverageBusyHandlersPerSecond() {
        Map<String, Object> r = Metrics.computeQueueDrain(queue(3, 1000, false), List.of(new QueueHandlerRecord(0, 0, 1000, 0),
                new QueueHandlerRecord(1, 0, 500, 0), new QueueHandlerRecord(2, 1000, 1500, 0)), 1500, 0);

        assertEquals(List.of(1, 2), r.get("messagesPerSecondTimeline"));
        assertEquals(List.of(1.5, 0.5), r.get("busyHandlersTimeline"));
    }
}
