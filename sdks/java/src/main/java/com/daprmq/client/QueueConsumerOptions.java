package com.daprmq.client;

import java.time.Duration;

public final class QueueConsumerOptions {
    private int maxActiveMessages = 100;
    private int maxConcurrentHandlers = 0;
    private Duration lockTtl = Duration.ofSeconds(30);
    private boolean allowCompetingConsumers = true;
    private boolean strictOrder = false;
    private QueueHandlerFailureAction onHandlerError = QueueHandlerFailureAction.NACK;
    private double maxRetriableErrorsPerSec = 10;
    private int minBackoffSeconds = 1;
    private int maxBackoffSeconds = 60;
    private long drainTimeoutMillis = 30_000;

    /**
     * Delivered but unsettled messages the server keeps in flight to this consumer: the stream's
     * prefetchCount (1-1000). Messages over maxConcurrentHandlers wait locked, and the server keeps
     * their locks alive.
     */
    public QueueConsumerOptions maxActiveMessages(int value) {
        this.maxActiveMessages = value;
        return this;
    }

    /** Handlers running at once. 0 = unlimited, which maxActiveMessages bounds. */
    public QueueConsumerOptions maxConcurrentHandlers(int value) {
        this.maxConcurrentHandlers = value;
        return this;
    }

    /** Passed to the stream; the server renews each lock until its message is settled. */
    public QueueConsumerOptions lockTtl(Duration value) {
        this.lockTtl = value;
        return this;
    }

    /** Lets replicas share the queue, each holding its own locks. Default true. */
    public QueueConsumerOptions allowCompetingConsumers(boolean value) {
        this.allowCompetingConsumers = value;
        return this;
    }

    /**
     * Handles messages one at a time in queue order, including after a nack: forces a window of 1,
     * one handler, and no competing consumers.
     */
    public QueueConsumerOptions strictOrder(boolean value) {
        this.strictOrder = value;
        return this;
    }

    public QueueConsumerOptions onHandlerError(QueueHandlerFailureAction value) {
        this.onHandlerError = value;
        return this;
    }

    /** Paces nacks after handler errors, so a failing handler doesn't spin. 0 = unpaced. */
    public QueueConsumerOptions maxRetriableErrorsPerSec(double value) {
        this.maxRetriableErrorsPerSec = value;
        return this;
    }

    /** Reconnect backoff after the stream breaks, doubling to the max; resets after a delivery. */
    public QueueConsumerOptions minBackoffSeconds(int value) {
        this.minBackoffSeconds = value;
        return this;
    }

    public QueueConsumerOptions maxBackoffSeconds(int value) {
        this.maxBackoffSeconds = value;
        return this;
    }

    /** How long stop() waits for running handlers before interrupting them. */
    public QueueConsumerOptions drainTimeoutMillis(long value) {
        this.drainTimeoutMillis = value;
        return this;
    }

    public int getMaxActiveMessages() {
        return maxActiveMessages;
    }

    public int getMaxConcurrentHandlers() {
        return maxConcurrentHandlers;
    }

    public Duration getLockTtl() {
        return lockTtl;
    }

    public boolean getAllowCompetingConsumers() {
        return allowCompetingConsumers;
    }

    public boolean getStrictOrder() {
        return strictOrder;
    }

    public QueueHandlerFailureAction getOnHandlerError() {
        return onHandlerError;
    }

    public double getMaxRetriableErrorsPerSec() {
        return maxRetriableErrorsPerSec;
    }

    public int getMinBackoffSeconds() {
        return minBackoffSeconds;
    }

    public int getMaxBackoffSeconds() {
        return maxBackoffSeconds;
    }

    public long getDrainTimeoutMillis() {
        return drainTimeoutMillis;
    }
}
