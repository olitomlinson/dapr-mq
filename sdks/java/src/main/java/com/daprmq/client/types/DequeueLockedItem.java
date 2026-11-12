package com.daprmq.client.types;

import com.fasterxml.jackson.databind.JsonNode;

/** A locked item. {@code expiry} is the message's expiry, not the lock's. */
public record DequeueLockedItem(JsonNode item, int priority, String lockId, double lockExpiresAt, MessageExpiry expiry) {
}
