package com.daprmq.client;

/** The slice of DaprMQClient that QueueConsumer needs - satisfied by DaprMQClient itself. */
public interface QueueCapableClient {
    QueueStream consume(String queueId, ConsumeOptions options);
}
