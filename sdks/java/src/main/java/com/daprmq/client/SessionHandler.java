package com.daprmq.client;

@FunctionalInterface
public interface SessionHandler {
    void handle(SessionMessageContext context) throws Exception;
}
