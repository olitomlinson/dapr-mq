package com.daprmq.client;

import com.fasterxml.jackson.databind.JsonNode;

/** @param deliveryCount 1 on a first delivery, 2 on the first redelivery, and so on. */
public record QueueMessageContext(String queueId, String lockId, JsonNode item, int priority, int deliveryCount) {
}
