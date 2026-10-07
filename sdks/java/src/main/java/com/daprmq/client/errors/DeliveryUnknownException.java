package com.daprmq.client.errors;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * The operation may or may not have been performed (e.g. the connection broke after it was sent),
 * and it isn't safe to repeat automatically. See sdks/testing/RETRIES_AND_READINESS.md for what to
 * do per operation; an enqueue whose items all carry an idempotency key is retried instead.
 */
public class DeliveryUnknownException extends DaprMQException {
    private final String operation;
    private final String queueId;
    private final List<String> idempotencyKeys;

    public DeliveryUnknownException(String message, String operation, String queueId, List<String> idempotencyKeys) {
        super(message, "DELIVERY_UNKNOWN");
        this.operation = operation;
        this.queueId = queueId;
        this.idempotencyKeys = idempotencyKeys == null ? List.of() : Collections.unmodifiableList(new ArrayList<>(idempotencyKeys));
    }

    public String getOperation() {
        return operation;
    }

    public String getQueueId() {
        return queueId;
    }

    /** For enqueue: each item's key, in order (null where the item had none). */
    public List<String> getIdempotencyKeys() {
        return idempotencyKeys;
    }
}
