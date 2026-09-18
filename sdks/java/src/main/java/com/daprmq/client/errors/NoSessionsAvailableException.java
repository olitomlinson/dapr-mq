package com.daprmq.client.errors;

public class NoSessionsAvailableException extends DaprMQException {
    public NoSessionsAvailableException(String message) {
        super(message, "NO_SESSIONS_AVAILABLE");
    }
}
