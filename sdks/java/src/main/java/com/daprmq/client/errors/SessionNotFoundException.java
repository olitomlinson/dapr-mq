package com.daprmq.client.errors;

public class SessionNotFoundException extends DaprMQException {
    public SessionNotFoundException(String message) {
        super(message, "SESSION_NOT_FOUND");
    }
}
