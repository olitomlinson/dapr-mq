package com.daprmq.examples.consumer;

import com.daprmq.client.DaprMQClient;
import com.daprmq.client.types.DequeueLockedItem;
import com.daprmq.client.types.DequeueLockedResult;
import com.daprmq.client.types.SessionLease;
import com.fasterxml.jackson.databind.JsonNode;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Consumer-side implementation of the 4 demo scenarios from SCENARIOS.md. */
final class ConsumerScenarios {
    private ConsumerScenarios() {
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
        List<Map<String, Object>> steps = new ArrayList<>();

        DequeueLockedResult result = client.dequeueLocked(queueId, 5, 30, null);
        if (result == null || result.items().isEmpty()) {
            Log.info("Queue " + queueId + " is empty - run the producer's scenario first");
            steps.add(step("dequeue", "queue empty — run the producer's scenario first"));
            return new ScenarioRun(queueId, steps);
        }

        List<String> lockIds = new ArrayList<>();
        for (DequeueLockedItem item : result.items()) {
            lockIds.add(item.lockId());
            Log.info("Dequeued item " + item.item() + " from queue " + queueId + " lockId=" + item.lockId());
        }
        steps.add(step("dequeue", "dequeued " + result.items().size() + " items, lockIds=" + lockIds));

        for (DequeueLockedItem item : result.items()) {
            client.acknowledge(queueId, item.lockId());
            Log.info("Acknowledged item lockId=" + item.lockId() + " from queue " + queueId);
        }
        steps.add(step("acknowledge", "acknowledged " + result.items().size() + " items"));

        return new ScenarioRun(queueId, steps);
    }

    private static ScenarioRun ackDeadletter(DaprMQClient client, String prefix) {
        String queueId = prefix + "-ackdlq";
        String dlqId = queueId + "-deadletter";
        List<Map<String, Object>> steps = new ArrayList<>();

        DequeueLockedResult first = client.dequeueLocked(queueId, 3, 10, null);
        if (first == null || first.items().isEmpty()) {
            Log.info("Queue " + queueId + " is empty - run the producer's scenario first");
            steps.add(step("dequeue", "queue empty — run the producer's scenario first"));
            return new ScenarioRun(queueId, steps);
        }

        List<String> lockIds = new ArrayList<>();
        for (DequeueLockedItem item : first.items()) {
            lockIds.add(item.lockId());
        }
        Log.info("Dequeued " + first.items().size() + " items from queue " + queueId + " lockIds=" + lockIds);
        steps.add(step("dequeue", "dequeued " + first.items().size() + " items, lockIds=" + lockIds));

        boolean expirePending = false;
        for (DequeueLockedItem item : first.items()) {
            String outcome = outcomeOf(item.item());
            switch (outcome) {
                case "deadletter" -> {
                    client.deadLetter(queueId, item.lockId());
                    Log.info("Dead-lettered item lockId=" + item.lockId() + " outcome=deadletter -> dlqId=" + dlqId);
                    steps.add(step("deadletter", "dead-lettered item outcome=deadletter -> dlqId=" + dlqId));
                }
                case "expire" -> {
                    expirePending = true;
                    Log.info("Leaving item lockId=" + item.lockId() + " outcome=expire locked (no action, awaiting expiry)");
                }
                default -> {
                    client.acknowledge(queueId, item.lockId());
                    Log.info("Acknowledged item lockId=" + item.lockId() + " outcome=" + outcome);
                    steps.add(step("acknowledge", "acked item outcome=" + outcome));
                }
            }
        }

        if (expirePending) {
            Log.info("Waiting 11s for outcome=expire item's lock to expire on queue " + queueId);
            steps.add(step("wait", "waiting 11s for outcome=expire item's lock to expire"));
            sleepSeconds(11);

            DequeueLockedResult redelivered = client.dequeueLocked(queueId, 1, 10, null);
            if (redelivered == null || redelivered.items().isEmpty()) {
                Log.info("No redelivered item found on queue " + queueId + " after expiry wait");
                steps.add(step("dequeue", "no redelivered item found after expiry wait"));
            } else {
                DequeueLockedItem redeliveredItem = redelivered.items().get(0);
                Log.info("Dequeued expired item again via redelivery lockId=" + redeliveredItem.lockId() + " from queue " + queueId);
                steps.add(step("dequeue", "dequeued expired item again via redelivery"));

                client.acknowledge(queueId, redeliveredItem.lockId());
                Log.info("Acknowledged redelivered item lockId=" + redeliveredItem.lockId() + " (drained)");
                steps.add(step("acknowledge", "acknowledged redelivered item (drained)"));
            }
        }

        DequeueLockedResult dlqDequeue = client.dequeueLocked(dlqId, 1, 10, null);
        if (dlqDequeue == null || dlqDequeue.items().isEmpty()) {
            Log.info("Dead-letter queue " + dlqId + " is empty");
            steps.add(step("dequeue", "dead-letter queue " + dlqId + " is empty"));
        } else {
            DequeueLockedItem dlqItem = dlqDequeue.items().get(0);
            Log.info("Dequeued dead-lettered item from " + dlqId + " content=" + dlqItem.item() + " lockId=" + dlqItem.lockId());
            steps.add(step("dequeue", "dequeued item from dead-letter queue " + dlqId));

            client.acknowledge(dlqId, dlqItem.lockId());
            Log.info("Acknowledged dead-lettered item lockId=" + dlqItem.lockId() + " on " + dlqId + " (drained)");
            steps.add(step("acknowledge", "acknowledged dead-letter queue item (drained)"));
        }

        return new ScenarioRun(queueId, steps);
    }

    private static ScenarioRun priority(DaprMQClient client, String prefix) {
        String queueId = prefix + "-priority";
        List<Map<String, Object>> steps = new ArrayList<>();

        DequeueLockedResult result = client.dequeueLocked(queueId, 10, 30, null);
        if (result == null || result.items().isEmpty()) {
            Log.info("Queue " + queueId + " is empty - run the producer's scenario first");
            steps.add(step("dequeue", "queue empty — run the producer's scenario first"));
            return new ScenarioRun(queueId, steps);
        }

        List<Integer> order = new ArrayList<>();
        for (DequeueLockedItem item : result.items()) {
            int n = fieldAsInt(item.item(), "n");
            order.add(n);
            Log.info("Dequeued item n=" + n + " priority=" + item.priority() + " from queue " + queueId);
        }
        steps.add(step("dequeue", "dequeued " + result.items().size() + " items in retrieval order n=" + order));

        for (DequeueLockedItem item : result.items()) {
            client.acknowledge(queueId, item.lockId());
        }
        Log.info("Acknowledged " + result.items().size() + " items from queue " + queueId);
        steps.add(step("acknowledge", "acknowledged " + result.items().size() + " items"));

        return new ScenarioRun(queueId, steps);
    }

    private static final List<String> KNOWN_SESSION_IDS = List.of("session-a", "session-b");

    private static ScenarioRun sessions(DaprMQClient client, String prefix) {
        String queueId = prefix + "-sessions";
        List<Map<String, Object>> steps = new ArrayList<>();

        // Round 1: "any available" mode (sessionId=null) - the server picks whichever
        // of the known sessions is currently unclaimed.
        String claimedFirst = null;
        SessionLease anyLease = client.acceptSession(queueId, null, 30);
        if (anyLease == null) {
            Log.warn("No session currently available (any-available mode) on queue " + queueId);
            steps.add(step("accept-session", "no session currently available (any-available mode)"));
        } else {
            claimedFirst = anyLease.sessionId();
            Log.info("Accepted session " + claimedFirst + " via any-available mode leaseId=" + anyLease.leaseId());
            steps.add(step("accept-session", "accepted session " + claimedFirst + " via any-available mode leaseId=" + anyLease.leaseId()));
            drainSession(client, queueId, claimedFirst, anyLease.leaseId(), steps);
        }

        // Round 2: targeted mode - accept whichever known session round 1 didn't return
        // (both, if round 1 found nothing available).
        for (String sessionId : KNOWN_SESSION_IDS) {
            if (sessionId.equals(claimedFirst)) {
                continue;
            }

            SessionLease lease = client.acceptSession(queueId, sessionId, 30);
            if (lease == null) {
                Log.warn("Session " + sessionId + " unavailable (targeted mode) on queue " + queueId);
                steps.add(step("accept-session", "session " + sessionId + " unavailable (targeted mode)"));
                continue;
            }
            Log.info("Accepted session " + sessionId + " via targeted mode leaseId=" + lease.leaseId());
            steps.add(step("accept-session", "accepted session " + sessionId + " via targeted mode leaseId=" + lease.leaseId()));
            drainSession(client, queueId, sessionId, lease.leaseId(), steps);
        }

        return new ScenarioRun(queueId, steps);
    }

    private static void drainSession(
            DaprMQClient client, String queueId, String sessionId, String leaseId, List<Map<String, Object>> steps) {
        String sessionQueueId = queueId + "-session-" + sessionId;
        DequeueLockedResult dequeued = client.dequeueLocked(sessionQueueId, 10, 30, leaseId);
        if (dequeued == null || dequeued.items().isEmpty()) {
            Log.info("Session queue " + sessionQueueId + " is empty");
            steps.add(step("dequeue", "session queue " + sessionQueueId + " is empty"));
        } else {
            List<Integer> seqs = new ArrayList<>();
            for (DequeueLockedItem item : dequeued.items()) {
                int seq = fieldAsInt(item.item(), "seq");
                seqs.add(seq);
                Log.info("Dequeued item sessionId=" + sessionId + " seq=" + seq + " from " + sessionQueueId);
            }
            steps.add(step("dequeue", "dequeued " + dequeued.items().size() + " items from session " + sessionId + " in order seq=" + seqs));

            for (DequeueLockedItem item : dequeued.items()) {
                client.acknowledge(sessionQueueId, item.lockId(), leaseId);
            }
            Log.info("Acknowledged " + dequeued.items().size() + " items for session " + sessionId);
            steps.add(step("acknowledge", "acknowledged " + dequeued.items().size() + " items for session " + sessionId));
        }

        client.releaseSession(queueId, sessionId, leaseId);
        Log.info("Released session " + sessionId + " leaseId=" + leaseId);
        steps.add(step("release-session", "released session " + sessionId));
    }

    /**
     * Dequeues from the queue the producer's idempotency scenario just filled. Expects
     * exactly 2 items (n=1 and n=3) since n=2, the duplicate, should never have been
     * enqueued at all.
     */
    private static ScenarioRun idempotency(DaprMQClient client, String prefix) {
        String queueId = prefix + "-idempotency";
        List<Map<String, Object>> steps = new ArrayList<>();

        DequeueLockedResult result = client.dequeueLocked(queueId, 5, 30, null);
        if (result == null || result.items().isEmpty()) {
            Log.info("Queue " + queueId + " is empty - run the producer's scenario first");
            steps.add(step("dequeue", "queue empty — run the producer's scenario first"));
            return new ScenarioRun(queueId, steps);
        }

        List<Integer> order = new ArrayList<>();
        for (DequeueLockedItem item : result.items()) {
            order.add(fieldAsInt(item.item(), "n"));
        }
        Log.info("Dequeued " + result.items().size() + " item(s) n=" + order + " from queue " + queueId
                + " - expecting 2 (n=1, n=3); the duplicate n=2 should have been silently dropped by idempotency dedup");
        steps.add(step("dequeue", "dequeued " + result.items().size() + " item(s) n=" + order + " - expecting 2 (n=1, n=3)"));

        for (DequeueLockedItem item : result.items()) {
            client.acknowledge(queueId, item.lockId());
            Log.info("Acknowledged item n=" + fieldAsInt(item.item(), "n") + " from queue " + queueId);
        }
        steps.add(step("acknowledge", "acknowledged " + result.items().size() + " items"));

        return new ScenarioRun(queueId, steps);
    }

    private static String outcomeOf(JsonNode item) {
        if (item != null && item.hasNonNull("outcome")) {
            return item.get("outcome").asText();
        }
        return "ack";
    }

    private static int fieldAsInt(JsonNode item, String field) {
        return (item != null && item.hasNonNull(field)) ? item.get(field).asInt() : -1;
    }

    private static void sleepSeconds(int seconds) {
        try {
            Thread.sleep(seconds * 1000L);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new RuntimeException("Interrupted while waiting for lock expiry", e);
        }
    }

    private static Map<String, Object> step(String action, String detail) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("action", action);
        m.put("detail", detail);
        return m;
    }
}
