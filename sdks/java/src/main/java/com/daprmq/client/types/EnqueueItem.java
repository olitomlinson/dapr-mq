package com.daprmq.client.types;

/**
 * @param item             Item payload - any Jackson-serializable value.
 * @param priority         0 = fast lane, 1 (default) = normal. Lower is processed first.
 * @param idempotencyKey   Optional client-supplied dedup key.
 * @param sessionId        Optional session id to route this item to a per-session queue actor.
 */
public record EnqueueItem(Object item, int priority, String idempotencyKey, String sessionId) {
    public EnqueueItem(Object item) {
        this(item, 1, null, null);
    }

    public EnqueueItem(Object item, int priority) {
        this(item, priority, null, null);
    }
}
