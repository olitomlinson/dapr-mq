package com.daprmq.client.errors;

public class SessionLeaseExpiredException extends DaprMQException {
    public SessionLeaseExpiredException(String message) {
        super(message, "SESSION_LEASE_EXPIRED");
    }
}
