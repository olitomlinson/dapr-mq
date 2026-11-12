package com.daprmq.client;

import com.daprmq.client.types.MessageExpiry;
import com.fasterxml.jackson.databind.JsonNode;

/**
 * @param deliveryCount 1 on a first delivery, 2 on the first redelivery, and so on.
 * @param expiry        The message's expiry.
 */
public record QueueMessageContext(String queueId, String lockId, JsonNode item, int priority, int deliveryCount, MessageExpiry expiry) {
}
