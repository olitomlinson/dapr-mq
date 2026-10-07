package com.daprmq.client.errors;

/**
 * The operation was certainly not performed - DaprMQ couldn't serve it - and retrying ran out of
 * {@code RetryOptions.timeout()}. Always safe to repeat later.
 */
public class DaprMQUnavailableException extends DaprMQException {
    private final String operation;
    private final String queueId;

    public DaprMQUnavailableException(String message, String operation, String queueId) {
        super(message, "UNAVAILABLE");
        this.operation = operation;
        this.queueId = queueId;
    }

    public String getOperation() {
        return operation;
    }

    public String getQueueId() {
        return queueId;
    }
}
