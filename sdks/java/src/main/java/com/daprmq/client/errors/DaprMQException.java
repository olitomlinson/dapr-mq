package com.daprmq.client.errors;

public class DaprMQException extends RuntimeException {
    private final String errorCode;

    public DaprMQException(String message) {
        this(message, null);
    }

    public DaprMQException(String message, String errorCode) {
        super(message);
        this.errorCode = errorCode;
    }

    public String getErrorCode() {
        return errorCode;
    }
}
