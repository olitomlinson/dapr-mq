package com.daprmq.client.errors;

public class LockExpiredException extends DaprMQException {
    public LockExpiredException(String message) {
        super(message, "LOCK_EXPIRED");
    }
}
