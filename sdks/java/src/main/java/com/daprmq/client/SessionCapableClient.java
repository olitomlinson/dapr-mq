package com.daprmq.client;

/** The slice of {@link DaprMQClient} that {@link SessionQueueConsumer} needs - satisfied by DaprMQClient itself. */
public interface SessionCapableClient {
    SessionStream consumeSession(String queueId, ConsumeSessionOptions options);
}
