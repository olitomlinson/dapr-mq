package com.daprmq.examples.producer;

import com.daprmq.client.DaprMQClient;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.EnqueueResult;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Producer-side implementation of the 4 demo scenarios from SCENARIOS.md. */
final class ProducerScenarios {
    private ProducerScenarios() {
    }

    record ScenarioRun(String queueId, List<Map<String, Object>> steps) {
    }

    /** Returns null if {@code name} isn't one of the 4 known scenarios. */
    static ScenarioRun run(String name, DaprMQClient client, String queuePrefix) {
        return switch (name) {
            case "basic" -> basic(client, queuePrefix);
            case "ack-deadletter" -> ackDeadletter(client, queuePrefix);
            case "priority" -> priority(client, queuePrefix);
            case "sessions" -> sessions(client, queuePrefix);
            case "idempotency" -> idempotency(client, queuePrefix);
            default -> null;
        };
    }

    private static ScenarioRun basic(DaprMQClient client, String prefix) {
        String queueId = prefix + "-basic";
        List<EnqueueItem> items = new ArrayList<>();
        for (int n = 1; n <= 3; n++) {
            Map<String, Object> payload = new LinkedHashMap<>();
            payload.put("n", n);
            payload.put("message", "hello from producer");
            items.add(new EnqueueItem(payload));
        }
        client.enqueue(queueId, items);
        for (int n = 1; n <= 3; n++) {
            Log.info("Enqueued item " + n + "/3 to queue " + queueId + " (priority=1)");
        }
        List<Map<String, Object>> steps = new ArrayList<>();
        steps.add(step("enqueue", "enqueued 3 items to " + queueId));
        return new ScenarioRun(queueId, steps);
    }

    private static ScenarioRun ackDeadletter(DaprMQClient client, String prefix) {
        String queueId = prefix + "-ackdlq";
        String[] outcomes = {"ack", "deadletter", "expire"};
        List<EnqueueItem> items = new ArrayList<>();
        for (int i = 0; i < outcomes.length; i++) {
            Map<String, Object> payload = new LinkedHashMap<>();
            payload.put("outcome", outcomes[i]);
            payload.put("n", i + 1);
            items.add(new EnqueueItem(payload));
        }
        client.enqueue(queueId, items);
        for (int i = 0; i < outcomes.length; i++) {
            Log.info("Enqueued item " + (i + 1) + "/3 (outcome=" + outcomes[i] + ") to queue " + queueId + " (priority=1)");
        }
        List<Map<String, Object>> steps = new ArrayList<>();
        steps.add(step("enqueue", "enqueued 3 items (outcome=ack/deadletter/expire) to " + queueId));
        return new ScenarioRun(queueId, steps);
    }

    private static ScenarioRun priority(DaprMQClient client, String prefix) {
        String queueId = prefix + "-priority";

        List<EnqueueItem> normalItems = new ArrayList<>();
        for (int n = 1; n <= 3; n++) {
            Map<String, Object> payload = new LinkedHashMap<>();
            payload.put("priority", 1);
            payload.put("n", n);
            normalItems.add(new EnqueueItem(payload, 1));
        }
        client.enqueue(queueId, normalItems);
        for (int n = 1; n <= 3; n++) {
            Log.info("Enqueued item n=" + n + " to queue " + queueId + " (priority=1)");
        }

        List<EnqueueItem> fastItems = new ArrayList<>();
        for (int n = 4; n <= 6; n++) {
            Map<String, Object> payload = new LinkedHashMap<>();
            payload.put("priority", 0);
            payload.put("n", n);
            fastItems.add(new EnqueueItem(payload, 0));
        }
        client.enqueue(queueId, fastItems);
        for (int n = 4; n <= 6; n++) {
            Log.info("Enqueued item n=" + n + " to queue " + queueId + " (priority=0)");
        }

        List<Map<String, Object>> steps = new ArrayList<>();
        steps.add(step("enqueue", "enqueued 3 normal-priority items (n=1,2,3) to " + queueId));
        steps.add(step("enqueue", "enqueued 3 fast-lane items (n=4,5,6) to " + queueId));
        return new ScenarioRun(queueId, steps);
    }

    private static ScenarioRun sessions(DaprMQClient client, String prefix) {
        String queueId = prefix + "-sessions";
        record SessionItem(String sessionId, int seq) {
        }
        List<SessionItem> defs = List.of(
                new SessionItem("session-a", 1),
                new SessionItem("session-a", 2),
                new SessionItem("session-b", 1),
                new SessionItem("session-b", 2));

        List<EnqueueItem> items = new ArrayList<>();
        for (SessionItem d : defs) {
            Map<String, Object> payload = new LinkedHashMap<>();
            payload.put("sessionId", d.sessionId());
            payload.put("seq", d.seq());
            items.add(new EnqueueItem(payload, 1, null, d.sessionId()));
        }
        client.enqueue(queueId, items);
        for (SessionItem d : defs) {
            Log.info("Enqueued item sessionId=" + d.sessionId() + " seq=" + d.seq() + " to queue " + queueId);
        }

        List<Map<String, Object>> steps = new ArrayList<>();
        steps.add(step("enqueue", "enqueued 4 items across sessions session-a, session-b to " + queueId));
        return new ScenarioRun(queueId, steps);
    }

    /**
     * Enqueues an item, a duplicate reusing its idempotency key (expected to be
     * deduplicated), then a distinct item under a different key. The key is
     * suffixed with the current time so repeated runs don't collide with a
     * previous run's key still inside the dedup TTL window.
     */
    private static ScenarioRun idempotency(DaprMQClient client, String prefix) {
        String queueId = prefix + "-idempotency";
        String key = "idempotency-demo-" + System.currentTimeMillis();
        List<Map<String, Object>> steps = new ArrayList<>();

        Map<String, Object> payload1 = new LinkedHashMap<>();
        payload1.put("n", 1);
        EnqueueResult first = client.enqueue(queueId, List.of(new EnqueueItem(payload1, 1, key, null)));
        Log.info("Enqueued item n=1 with idempotencyKey=" + key
                + " (itemsEnqueued=" + first.itemsEnqueued() + ", itemsDeduplicated=" + first.itemsDeduplicated() + ")");
        steps.add(step("enqueue", "enqueued item n=1 with idempotencyKey=" + key
                + " (itemsEnqueued=" + first.itemsEnqueued() + ", itemsDeduplicated=" + first.itemsDeduplicated() + ")"));

        Map<String, Object> payload2 = new LinkedHashMap<>();
        payload2.put("n", 2);
        EnqueueResult duplicate = client.enqueue(queueId, List.of(new EnqueueItem(payload2, 1, key, null)));
        Log.info("Enqueued item n=2 reusing idempotencyKey=" + key
                + " (itemsEnqueued=" + duplicate.itemsEnqueued() + ", itemsDeduplicated=" + duplicate.itemsDeduplicated()
                + ") - expected to be deduplicated");
        steps.add(step("enqueue", "enqueued item n=2 reusing idempotencyKey=" + key
                + " (itemsEnqueued=" + duplicate.itemsEnqueued() + ", itemsDeduplicated=" + duplicate.itemsDeduplicated() + ")"));

        String distinctKey = key + "-b";
        Map<String, Object> payload3 = new LinkedHashMap<>();
        payload3.put("n", 3);
        EnqueueResult distinct = client.enqueue(queueId, List.of(new EnqueueItem(payload3, 1, distinctKey, null)));
        Log.info("Enqueued item n=3 with a different idempotencyKey=" + distinctKey
                + " (itemsEnqueued=" + distinct.itemsEnqueued() + ", itemsDeduplicated=" + distinct.itemsDeduplicated() + ")");
        steps.add(step("enqueue", "enqueued item n=3 with a different idempotencyKey=" + distinctKey
                + " (itemsEnqueued=" + distinct.itemsEnqueued() + ", itemsDeduplicated=" + distinct.itemsDeduplicated() + ")"));

        return new ScenarioRun(queueId, steps);
    }

    private static Map<String, Object> step(String action, String detail) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("action", action);
        m.put("detail", detail);
        return m;
    }
}
