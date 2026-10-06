package com.daprmq.client;

import java.time.Duration;

/**
 * How calls ride out a DaprMQ that can't serve them yet (sdks/testing/RETRIES_AND_READINESS.md).
 * Immutable; start from {@link #defaults()}.
 */
public final class RetryOptions {
    private final Duration timeout;
    private final boolean autoIdempotencyKeys;
    private final Duration minAttemptWindow;
    private final Duration initialBackoff;
    private final Duration maxBackoff;

    private RetryOptions(Duration timeout, boolean autoIdempotencyKeys, Duration minAttemptWindow, Duration initialBackoff, Duration maxBackoff) {
        this.timeout = timeout;
        this.autoIdempotencyKeys = autoIdempotencyKeys;
        this.minAttemptWindow = minAttemptWindow;
        this.initialBackoff = initialBackoff;
        this.maxBackoff = maxBackoff;
    }

    /** 30 s timeout, no automatic idempotency keys. */
    public static RetryOptions defaults() {
        return new RetryOptions(Duration.ofSeconds(30), false, Duration.ofSeconds(6), Duration.ofMillis(100), Duration.ofSeconds(2));
    }

    /** How long one call may keep retrying; also the deadline sent to the server. Zero turns client retries off and sends no deadline. */
    public RetryOptions withTimeout(Duration timeout) {
        return new RetryOptions(timeout, autoIdempotencyKeys, minAttemptWindow, initialBackoff, maxBackoff);
    }

    /**
     * Give each enqueued item without an idempotency key a fresh one, so an enqueue whose outcome is
     * unknown is retried safely. Costs the server one extra state write per item.
     */
    public RetryOptions withAutoIdempotencyKeys(boolean autoIdempotencyKeys) {
        return new RetryOptions(timeout, autoIdempotencyKeys, minAttemptWindow, initialBackoff, maxBackoff);
    }

    public Duration timeout() {
        return timeout;
    }

    public boolean autoIdempotencyKeys() {
        return autoIdempotencyKeys;
    }

    /** No attempt starts with less left: the server takes ~5 s to report it can't serve. */
    RetryOptions withMinAttemptWindow(Duration minAttemptWindow) {
        return new RetryOptions(timeout, autoIdempotencyKeys, minAttemptWindow, initialBackoff, maxBackoff);
    }

    RetryOptions withBackoff(Duration initialBackoff, Duration maxBackoff) {
        return new RetryOptions(timeout, autoIdempotencyKeys, minAttemptWindow, initialBackoff, maxBackoff);
    }

    Duration minAttemptWindow() {
        return minAttemptWindow;
    }

    Duration initialBackoff() {
        return initialBackoff;
    }

    Duration maxBackoff() {
        return maxBackoff;
    }
}
