package com.daprmq.client.errors;

public class LockNotFoundException extends DaprMQException {
    public LockNotFoundException(String message) {
        super(message, "LOCK_NOT_FOUND");
    }
}
