package com.daprmq.client.types;

/**
 * @param item             Item payload - any Jackson-serializable value.
 * @param priority         0 = fast lane, 1 (default) = normal. Lower is processed first.
 * @param idempotencyKey   Optional client-supplied dedup key.
 * @param sessionId        Optional session id to route this item to a per-session queue actor. Every item in one
 *                         enqueue call must have the same session id (or none); the server rejects a mixed call.
 * @param expiryTtlSeconds The message's time to live. Null uses the server's default (one year unless configured);
 *                         the server rejects values below its minimum (10 minutes by default) or over 10 years.
 *                         An item still queued past its expiry is never delivered.
 */
public record EnqueueItem(Object item, int priority, String idempotencyKey, String sessionId, Integer expiryTtlSeconds) {
    public EnqueueItem(Object item) {
        this(item, 1, null, null, null);
    }

    public EnqueueItem(Object item, int priority) {
        this(item, priority, null, null, null);
    }

    public EnqueueItem(Object item, int priority, String idempotencyKey, String sessionId) {
        this(item, priority, idempotencyKey, sessionId, null);
    }

    /** A copy of this item with the given time to live. */
    public EnqueueItem withExpiryTtlSeconds(int expiryTtlSeconds) {
        return new EnqueueItem(item, priority, idempotencyKey, sessionId, expiryTtlSeconds);
    }
}
