package com.daprmq.client.errors;

public class SessionLockedException extends DaprMQException {
    public SessionLockedException(String message) {
        super(message, "SESSION_LOCKED");
    }
}
