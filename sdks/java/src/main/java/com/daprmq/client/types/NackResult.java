package com.daprmq.client.types;

/**
 * Result of {@code DaprMQClient.nack}.
 *
 * @param deadLettered  True if the nack exceeded the server's max delivery count, so the item was
 *                      moved to the dead letter queue instead of being returned to this one.
 * @param deliveryCount The item's delivery count after this nack.
 * @param dlqId         Dead letter queue id, when {@code deadLettered}.
 */
public record NackResult(boolean deadLettered, int deliveryCount, String dlqId) {
}
