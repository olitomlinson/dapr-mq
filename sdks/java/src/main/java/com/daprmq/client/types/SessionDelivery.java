package com.daprmq.client.types;

import com.fasterxml.jackson.databind.JsonNode;

/**
 * One delivered, locked item from {@code DaprMQClient.consumeSession}.
 *
 * <p>There is no leaseId here - the ConsumeSession wire protocol never exposes one to the client
 * (the server tracks the lease internally and applies it when it calls Acknowledge/DeadLetter on
 * the caller's behalf), so {@link #ack()}/{@link #deadLetter()} are the only way to resolve this
 * item.
 *
 * @param ack        Acknowledges and permanently removes this item.
 * @param deadLetter Moves this item to the dead letter queue.
 */
public record SessionDelivery(
        String sessionId,
        String lockId,
        JsonNode item,
        int priority,
        double lockExpiresAt,
        Runnable ack,
        Runnable deadLetter) {
}
