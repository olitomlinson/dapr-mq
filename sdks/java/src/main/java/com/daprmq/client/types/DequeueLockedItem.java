package com.daprmq.client.types;

import com.fasterxml.jackson.databind.JsonNode;

public record DequeueLockedItem(JsonNode item, int priority, String lockId, double lockExpiresAt) {
}
