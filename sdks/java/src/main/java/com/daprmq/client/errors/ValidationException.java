package com.daprmq.client.errors;

public class ValidationException extends DaprMQException {
    public ValidationException(String message) {
        super(message, "VALIDATION_ERROR");
    }
}
