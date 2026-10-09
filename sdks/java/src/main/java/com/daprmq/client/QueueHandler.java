package com.daprmq.client;

/** Handles one message from a plain queue. Returning acks it; throwing applies the consumer's onHandlerError. */
@FunctionalInterface
public interface QueueHandler {
    void handle(QueueMessageContext context) throws Exception;
}
