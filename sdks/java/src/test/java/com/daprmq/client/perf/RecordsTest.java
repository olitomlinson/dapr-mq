package com.daprmq.client.perf;

import com.daprmq.client.perf.Metrics.HandlerRecord;
import com.daprmq.client.perf.Records.LoadResult;
import com.daprmq.client.perf.Records.RunContext;
import com.daprmq.client.perf.Records.RunEnvironment;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Port of RunRecordsTests (.NET): the record shape the shared report reads, checked as written JSON. */
class RecordsTest {
    private static final ObjectMapper JSON = new ObjectMapper();
    private static final RunContext CONTEXT = new RunContext(Instant.parse("2026-10-05T10:00:00Z"),
            new RunEnvironment("ci", "abc123", "main", false, "Linux", 4, "OpenJDK 21", "testcontainers daprmq-api:test"), "pr", 2, 3, "0.1.0");

    private static JsonNode json(Object value) {
        return JSON.valueToTree(value);
    }

    private static Map<String, Object> step(int concurrency, double messagesPerSecond, int errors) {
        Map<String, Object> step = new LinkedHashMap<>();
        step.put("concurrency", concurrency);
        step.put("queues", concurrency);
        step.put("durationSeconds", 30.0);
        step.put("ops", 100);
        step.put("messages", (int) (messagesPerSecond * 30));
        step.put("errors", errors);
        step.put("opsPerSecond", messagesPerSecond);
        step.put("messagesPerSecond", messagesPerSecond);
        step.put("latencyMs", Map.of("count", 100, "mean", 2, "p50", 1, "p95", 3, "p99", 4, "max", 5));
        return step;
    }

    private static Map<String, List<Object>> timeline(int concurrency, Integer... ops) {
        Map<String, List<Object>> t = new LinkedHashMap<>();
        for (String name : List.of("opsPerSecond", "messagesPerSecond", "latencyP50Ms", "latencyP95Ms", "latencyP99Ms")) {
            t.put(name, List.of((Object[]) ops));
        }
        t.put("errors", List.of(ops).stream().map(o -> (Object) 0).toList());
        t.put("concurrency", List.of(ops).stream().map(o -> (Object) concurrency).toList());
        return t;
    }

    @Test
    void loadRecordsIdentityTopologyAndScenario() {
        JsonNode run = json(Records.load(CONTEXT, "enqueue", Profiles.LOAD.get("enqueue"),
                new LoadResult(List.of(step(8, 500, 0)), List.of(timeline(8, 500, 500)), false, List.of())));

        assertEquals(2, run.get("schemaVersion").asInt());
        assertEquals("20261005T100000Z_java_ci_enqueue", run.get("runId").asText());
        assertEquals("2026-10-05T10:00:00Z", run.get("timestampUtc").asText());
        assertEquals(json(Map.of("name", "java", "version", "0.1.0", "runtime", "OpenJDK 21")), run.get("sdk"));
        assertEquals(2, run.at("/topology/apiReplicas").asInt());
        assertEquals(3, run.at("/topology/schedulerReplicas").asInt());
        assertTrue(run.at("/topology/loadBalancer").asBoolean());
        assertEquals("pr", run.get("scale").asText());
        assertEquals("P-01", run.at("/scenario/id").asText());
        assertEquals("enqueue:c8/q8/b1/256B/3+15s", run.at("/scenario/key").asText());
        assertEquals(8, run.at("/scenario/params/queues").asInt());
        assertTrue(run.at("/checks/passed").asBoolean());
    }

    @Test
    void aRampHeadlinesItsBestStepAndConcatenatesTheTimelines() {
        JsonNode run = json(Records.load(CONTEXT, "enqueue-ramp", Profiles.LOAD.get("enqueue-ramp"), new LoadResult(
                List.of(step(1, 100, 0), step(2, 300, 0), step(4, 250, 0)), List.of(timeline(1, 1), timeline(2, 2), timeline(4, 3, 3)), false, List.of())));

        assertEquals(300, run.at("/metrics/messagesPerSecond").asDouble());
        assertEquals(3, run.get("steps").size());
        assertEquals("[1,2,3,3]", run.at("/timeline/series/opsPerSecond").toString());
        assertEquals("[1,2,4,4]", run.at("/timeline/series/concurrency").toString());
        assertTrue(run.at("/scenario/params/queues").isNull());
    }

    @Test
    void loadFailsItsChecksOnErrorsOrADrainedQueue() {
        JsonNode run = json(Records.load(CONTEXT, "dequeue-ack", Profiles.LOAD.get("dequeue-ack"),
                new LoadResult(List.of(step(8, 100, 2)), List.of(timeline(8, 1)), true, List.of("TimeoutException: boom"))));

        assertFalse(run.at("/checks/passed").asBoolean());
        assertEquals("[\"2 errors (first: TimeoutException: boom)\",\"a queue drained before the window ended: raise seedPerQueue\"]",
                run.at("/checks/failures").toString());
    }

    @Test
    void sessionDrainMovesTheBusySlotsTimelineOutOfTheMetrics() {
        Profiles.SessionDrainParams scenario = Profiles.SESSION_DRAIN.get("steady-drain");
        Map<String, Object> result = Metrics.computeSessionDrain(scenario, List.of(), List.of(new HandlerRecord("s0", 0, 0, 1500)), 2000, 1);
        result.put("missing", 0);

        JsonNode run = json(Records.sessionDrain(CONTEXT, "steady-drain", scenario, result));

        assertEquals("P-04", run.at("/scenario/id").asText());
        assertEquals("200x20@100ms/20slots+idle1s", run.at("/scenario/key").asText());
        assertEquals(1, run.at("/scenario/params/sessionIdleTimeoutSeconds").asInt());
        assertTrue(run.at("/metrics/busySlotsTimeline").isMissingNode());
        assertTrue(run.at("/metrics/timelineBucketMs").isMissingNode());
        assertEquals("{\"bucketMs\":1000,\"series\":{\"busySlots\":[1.0,0.5]}}", run.get("timeline").toString());
        assertTrue(run.at("/checks/passed").asBoolean());
    }

    private static Map<String, Object> queueResult(int missing, int orderViolations) {
        Map<String, Object> r = new LinkedHashMap<>();
        r.put("seedSeconds", 0.4);
        r.put("wallClockSeconds", 5.0);
        r.put("messagesPerSecond", 800.0);
        r.put("idealSeconds", 0.4);
        r.put("efficiency", 0.08);
        r.put("timeToFirstMessageSeconds", 0.02);
        r.put("peakConcurrentHandlers", 100);
        r.put("deliveryLatencyMs", Map.of("count", 4000, "mean", 2000, "p50", 2000, "p95", 4000, "p99", 4500, "max", 5000));
        r.put("messagesHandled", 4000 - missing);
        r.put("duplicates", 0);
        r.put("missing", missing);
        r.put("orderViolations", orderViolations);
        r.put("messagesPerSecondTimeline", List.of(700, 900));
        r.put("busyHandlersTimeline", List.of(95.5, 99.0));
        return r;
    }

    @Test
    void queueDrainRecordsTheScenarioAndMovesTheTimelinesOutOfTheMetrics() {
        JsonNode run = json(Records.queueDrain(CONTEXT, "queue-drain", Profiles.QUEUE_DRAIN.get("queue-drain"), queueResult(0, 0)));

        assertEquals("P-05", run.at("/scenario/id").asText());
        assertEquals("queue-drain", run.at("/scenario/name").asText());
        assertEquals("queue:4000@10ms/active100", run.at("/scenario/key").asText());
        assertEquals(4000, run.at("/scenario/params/messages").asInt());
        assertTrue(run.at("/scenario/params/key").isMissingNode());
        assertEquals(4000, run.at("/metrics/deliveryLatencyMs/p95").asInt());
        assertTrue(run.at("/metrics/messagesPerSecondTimeline").isMissingNode());
        assertEquals("[700,900]", run.at("/timeline/series/messagesPerSecond").toString());
        assertEquals("[95.5,99.0]", run.at("/timeline/series/busyHandlers").toString());
    }

    @Test
    void queueDrainFailsOnMissingMessagesOrOutOfOrderOnlyUnderStrictOrder() {
        assertEquals("[\"3 messages missing\"]",
                json(Records.queueDrain(CONTEXT, "queue-drain", Profiles.QUEUE_DRAIN.get("queue-drain"), queueResult(3, 0))).at("/checks/failures").toString());
        assertEquals("[\"2 order violations\"]",
                json(Records.queueDrain(CONTEXT, "queue-strict-order", Profiles.QUEUE_DRAIN.get("queue-strict-order"), queueResult(0, 2))).at("/checks/failures").toString());
        assertTrue(json(Records.queueDrain(CONTEXT, "queue-drain", Profiles.QUEUE_DRAIN.get("queue-drain"), queueResult(0, 2))).at("/checks/passed").asBoolean());
    }

    @Test
    void saveWritesTheRunInFullAndHistoryWithoutTheTimeline(@TempDir Path root) throws IOException {
        Path path = Records.save(root, Records.queueDrain(CONTEXT, "queue-drain", Profiles.QUEUE_DRAIN.get("queue-drain"), queueResult(0, 0)));

        assertEquals(root.resolve("sdk-java/runs/20261005T100000Z_java_ci_queue-drain.json"), path);
        assertEquals(1000, JSON.readTree(path.toFile()).at("/timeline/bucketMs").asInt());
        List<String> history = Files.readAllLines(root.resolve("sdk-java/history.jsonl"));
        assertEquals(1, history.size());
        assertNull(JSON.readTree(history.get(0)).get("timeline"));
    }
}
