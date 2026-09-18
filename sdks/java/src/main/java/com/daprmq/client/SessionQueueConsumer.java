package com.daprmq.client;

import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.NoSessionsAvailableException;
import com.daprmq.client.errors.SessionActorUnavailableException;
import com.daprmq.client.errors.SessionLockedException;
import com.daprmq.client.errors.SessionLostException;
import com.daprmq.client.errors.SessionNotFoundException;
import com.daprmq.client.types.SessionDelivery;
import io.grpc.Status;
import io.grpc.StatusRuntimeException;

import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Manages a pool of {@code maxConcurrentSessions} independent slots, each looping over a
 * {@link DaprMQClient#consumeSession} stream: claim a session, hand each delivered item to the
 * caller's handler, ack/deadLetter it, and repeat until the session drains - then claim another.
 * Backoff on a failed claim doubles on a miss, resets on a successful claim, and is capped.
 */
public final class SessionQueueConsumer implements AutoCloseable {
    private final SessionCapableClient client;
    private final String queueId;
    private final SessionQueueConsumerOptions options;
    private final SessionHandler handler;
    private final AtomicBoolean stopping = new AtomicBoolean(false);
    private final Set<SessionStream> activeStreams = ConcurrentHashMap.newKeySet();

    /** Test seam - substituted by tests to assert on requested backoff durations without waiting real time. */
    volatile Delay delay = Thread::sleep;

    private ExecutorService executor;

    public SessionQueueConsumer(SessionCapableClient client, String queueId, SessionQueueConsumerOptions options, SessionHandler handler) {
        if (options.getTargetSessionId() != null && options.getMaxConcurrentSessions() != 1) {
            throw new IllegalArgumentException("targetSessionId requires maxConcurrentSessions == 1.");
        }
        this.client = client;
        this.queueId = queueId;
        this.options = options;
        this.handler = handler;
    }

    public synchronized void start() {
        if (executor != null) {
            throw new IllegalStateException("SessionQueueConsumer already started.");
        }
        stopping.set(false);
        AtomicInteger slotNumber = new AtomicInteger();
        executor = Executors.newFixedThreadPool(options.getMaxConcurrentSessions(), runnable -> {
            Thread t = new Thread(runnable, "daprmq-session-consumer-" + queueId + "-" + slotNumber.getAndIncrement());
            t.setDaemon(true);
            return t;
        });
        for (int i = 0; i < options.getMaxConcurrentSessions(); i++) {
            executor.submit(this::runSlot);
        }
    }

    /**
     * Stops claiming new sessions, cancels any streams currently blocked waiting for a delivery
     * (releasing their sessions immediately, faster than the lease TTL would), then gives in-flight
     * handlers up to drainTimeoutMillis to finish.
     */
    public synchronized void stop() {
        stopping.set(true);
        for (SessionStream stream : activeStreams) {
            stream.cancel();
        }

        if (executor != null) {
            executor.shutdown();
            try {
                executor.awaitTermination(options.getDrainTimeoutMillis(), TimeUnit.MILLISECONDS);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
            executor = null;
        }
    }

    @Override
    public void close() {
        stop();
    }

    private void runSlot() {
        int backoffSeconds = options.getMinBackoffSeconds();

        while (!stopping.get()) {
            boolean sessionWasClaimed = false;
            SessionStream stream = null;
            try {
                stream = client.consumeSession(queueId, new ConsumeSessionOptions(options.getTargetSessionId(), options.getLeaseSeconds(), options.getPrefetchCount()));
                activeStreams.add(stream);

                for (SessionDelivery delivery : stream) {
                    sessionWasClaimed = true;
                    handleDelivery(delivery);
                }
                sessionWasClaimed = true; // stream ended cleanly after a successful claim (drained)
            } catch (StatusRuntimeException e) {
                if (e.getStatus().getCode() == Status.Code.CANCELLED && stopping.get()) {
                    break;
                }
                // Any other transport failure - not specially handled, mirrors falling through to
                // the generic "unexpected mid-stream failure" case below (sessionWasClaimed keeps
                // whatever value it already had).
            } catch (NoSessionsAvailableException | SessionNotFoundException | SessionLockedException | SessionActorUnavailableException e) {
                // claim itself failed - fall through to backoff below
            } catch (SessionLostException e) {
                sessionWasClaimed = true; // claim succeeded; the lease was lost afterward
            } catch (RuntimeException e) {
                if (stopping.get()) {
                    break;
                }
                // Any other exception surfacing mid-stream - including a handler exception
                // re-thrown by handleDelivery under ABANDON_SESSION/BOTH - ends this slot's current
                // stream early. sessionWasClaimed is already true by the time the loop body can
                // throw, so the outer loop retries immediately rather than backing off as if the
                // claim itself had failed.
            } finally {
                if (stream != null) {
                    activeStreams.remove(stream);
                    stream.close();
                }
            }

            if (stopping.get()) {
                break;
            }

            if (sessionWasClaimed) {
                backoffSeconds = options.getMinBackoffSeconds();
                continue;
            }

            try {
                delay.sleep(backoffSeconds * 1000L);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                break;
            }

            backoffSeconds = Math.min(backoffSeconds * 2, options.getMaxBackoffSeconds());
        }
    }

    private void handleDelivery(SessionDelivery delivery) {
        SessionMessageContext context = new SessionMessageContext(queueId, delivery.sessionId(), delivery.lockId(), delivery.item(), delivery.priority());
        try {
            handler.handle(context);
            delivery.ack().run();
        } catch (Exception e) {
            if (stopping.get()) {
                throw asRuntimeException(e);
            }
            switch (options.getOnHandlerException()) {
                case DEAD_LETTER_MESSAGE -> delivery.deadLetter().run();
                case ABANDON_SESSION -> throw asRuntimeException(e); // unwinds the loop, ending this slot's stream early
                case BOTH -> {
                    delivery.deadLetter().run();
                    throw asRuntimeException(e);
                }
            }
        }
    }

    private static RuntimeException asRuntimeException(Exception e) {
        return e instanceof RuntimeException re ? re : new DaprMQException(e.getMessage());
    }

    @FunctionalInterface
    interface Delay {
        void sleep(long millis) throws InterruptedException;
    }
}
