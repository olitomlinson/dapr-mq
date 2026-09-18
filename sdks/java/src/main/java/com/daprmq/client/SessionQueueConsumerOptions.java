package com.daprmq.client;

public final class SessionQueueConsumerOptions {
    private int maxConcurrentSessions = 4;
    private String targetSessionId;
    private int leaseSeconds = 30;
    private int prefetchCount = 10;
    private int minBackoffSeconds = 1;
    private int maxBackoffSeconds = 60;
    private SessionHandlerFailureAction onHandlerException = SessionHandlerFailureAction.DEAD_LETTER_MESSAGE;
    private long drainTimeoutMillis = 30_000;

    public SessionQueueConsumerOptions maxConcurrentSessions(int value) {
        this.maxConcurrentSessions = value;
        return this;
    }

    /** Sticky routing to one specific session. Must pair with maxConcurrentSessions == 1. */
    public SessionQueueConsumerOptions targetSessionId(String value) {
        this.targetSessionId = value;
        return this;
    }

    public SessionQueueConsumerOptions leaseSeconds(int value) {
        this.leaseSeconds = value;
        return this;
    }

    public SessionQueueConsumerOptions prefetchCount(int value) {
        this.prefetchCount = value;
        return this;
    }

    public SessionQueueConsumerOptions minBackoffSeconds(int value) {
        this.minBackoffSeconds = value;
        return this;
    }

    public SessionQueueConsumerOptions maxBackoffSeconds(int value) {
        this.maxBackoffSeconds = value;
        return this;
    }

    public SessionQueueConsumerOptions onHandlerException(SessionHandlerFailureAction value) {
        this.onHandlerException = value;
        return this;
    }

    /** How long stop() waits for in-flight handlers to finish before giving up on the drain. */
    public SessionQueueConsumerOptions drainTimeoutMillis(long value) {
        this.drainTimeoutMillis = value;
        return this;
    }

    public int getMaxConcurrentSessions() {
        return maxConcurrentSessions;
    }

    public String getTargetSessionId() {
        return targetSessionId;
    }

    public int getLeaseSeconds() {
        return leaseSeconds;
    }

    public int getPrefetchCount() {
        return prefetchCount;
    }

    public int getMinBackoffSeconds() {
        return minBackoffSeconds;
    }

    public int getMaxBackoffSeconds() {
        return maxBackoffSeconds;
    }

    public SessionHandlerFailureAction getOnHandlerException() {
        return onHandlerException;
    }

    public long getDrainTimeoutMillis() {
        return drainTimeoutMillis;
    }
}
