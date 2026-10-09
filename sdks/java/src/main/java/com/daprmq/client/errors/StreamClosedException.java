package com.daprmq.client.errors;

/**
 * A {@code QueueDelivery} was settled after its {@code DaprMQClient.consume} stream closed. The
 * server has already returned the item to the queue (or will once its lock lapses), so it will be
 * redelivered.
 */
public class StreamClosedException extends DaprMQException {
    public StreamClosedException(String message) {
        super(message, "STREAM_CLOSED");
    }
}
