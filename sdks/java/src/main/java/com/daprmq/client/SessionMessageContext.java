package com.daprmq.client;

import com.fasterxml.jackson.databind.JsonNode;

/**
 * No leaseId here (unlike the low-level unary session API) - the ConsumeSession wire protocol
 * never exposes one to the client, since the server tracks the lease internally. See
 * {@link com.daprmq.client.types.SessionDelivery}.
 */
public record SessionMessageContext(String queueId, String sessionId, String lockId, JsonNode item, int priority) {
}
