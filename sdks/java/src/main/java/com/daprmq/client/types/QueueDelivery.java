package com.daprmq.client.types;

import com.fasterxml.jackson.databind.JsonNode;

/**
 * One delivered, locked item from {@code DaprMQClient.consume}. The server renews its lock until
 * it is settled. A rejected settlement arrives later through {@code ConsumeOptions.onSettleFailed};
 * the runnables here only send the frame.
 *
 * @param deliveryCount 1 on a first delivery, 2 on the first redelivery after a nack or a lapsed
 *                      lock, and so on.
 * @param ack           Acknowledges and permanently removes this item.
 * @param nack          Returns this item to its original position for redelivery (+1 delivery count).
 * @param deadLetter    Moves this item to the dead letter queue.
 */
public record QueueDelivery(
        String lockId,
        JsonNode item,
        int priority,
        double lockExpiresAt,
        int deliveryCount,
        Runnable ack,
        Runnable nack,
        Runnable deadLetter) {
}
