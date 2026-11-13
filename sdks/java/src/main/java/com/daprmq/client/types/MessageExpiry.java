package com.daprmq.client.types;

/**
 * A delivered message's expiry. Each field is null when absent: an item with no expiry, or one that
 * was never dead-lettered.
 *
 * @param expiryAt                 When the message expires, in Unix seconds.
 * @param expiryTtlSeconds         The time to live {@code expiryAt} was computed from.
 * @param originalExpiryTtlSeconds On an item from a dead-letter queue, the producer's original time to live.
 */
public record MessageExpiry(Double expiryAt, Integer expiryTtlSeconds, Integer originalExpiryTtlSeconds) {
    public static final MessageExpiry NONE = new MessageExpiry(null, null, null);
}
