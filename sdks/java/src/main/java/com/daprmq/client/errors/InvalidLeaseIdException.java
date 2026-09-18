package com.daprmq.client.errors;

public class InvalidLeaseIdException extends DaprMQException {
    public InvalidLeaseIdException(String message) {
        super(message, "INVALID_LEASE_ID");
    }
}
