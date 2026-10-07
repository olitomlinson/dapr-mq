package com.daprmq.client.types;

/**
 * Per-lock outcomes of {@code DaprMQClient.acknowledgeBatch}.
 */
public final class AcknowledgeOutcome {
    /** Settled by this call. */
    public static final String ACKNOWLEDGED = "ACKNOWLEDGED";

    /**
     * No such lock: never existed, or already settled. After the client retried a batch whose
     * outcome was unknown, this can mean the earlier attempt acknowledged it.
     */
    public static final String LOCK_NOT_FOUND = "LOCK_NOT_FOUND";

    /** Plain queue only: the lock's TTL passed, so the item is returning to the queue. */
    public static final String LOCK_EXPIRED = "LOCK_EXPIRED";

    /** Empty lock id. */
    public static final String INVALID_LOCK_ID = "INVALID_LOCK_ID";

    private AcknowledgeOutcome() {
    }
}
