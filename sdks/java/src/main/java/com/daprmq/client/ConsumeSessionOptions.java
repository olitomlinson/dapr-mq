package com.daprmq.client;

/**
 * @param sessionId                 Target a specific session id (sticky routing). {@code null} = any available.
 * @param leaseSeconds              Lease time-to-live in seconds (1-300, default: 30). The server renews this
 *                                  lease on its own schedule for as long as the stream stays open.
 * @param prefetchCount             How many delivered-but-not-yet-acked/dead-lettered items to keep in flight
 *                                  at once (default: 1). Above 1, a nack can reorder the session.
 * @param sessionIdleTimeoutSeconds Ends the stream (and releases the session) once no message has arrived
 *                                  for this long. 0 = the server default (the lease).
 */
public record ConsumeSessionOptions(String sessionId, int leaseSeconds, int prefetchCount, int sessionIdleTimeoutSeconds) {
    public ConsumeSessionOptions(String sessionId, int leaseSeconds, int prefetchCount) {
        this(sessionId, leaseSeconds, prefetchCount, 0);
    }

    public static ConsumeSessionOptions defaults() {
        return new ConsumeSessionOptions(null, 30, 1);
    }
}
