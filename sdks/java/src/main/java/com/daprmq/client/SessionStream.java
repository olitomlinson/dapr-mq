package com.daprmq.client;

import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.NoSessionsAvailableException;
import com.daprmq.client.errors.SessionActorUnavailableException;
import com.daprmq.client.errors.SessionLockedException;
import com.daprmq.client.errors.SessionLostException;
import com.daprmq.client.errors.SessionNotFoundException;
import com.daprmq.client.internal.Json;
import com.daprmq.client.types.SessionDelivery;
import com.daprmq.grpc.ConsumeSessionAck;
import com.daprmq.grpc.ConsumeSessionDeadLetter;
import com.daprmq.grpc.ConsumeSessionRequest;
import com.daprmq.grpc.ConsumeSessionResponse;
import com.daprmq.grpc.SessionDelivered;
import com.daprmq.grpc.SessionError;
import com.fasterxml.jackson.databind.JsonNode;
import io.grpc.stub.ClientCallStreamObserver;
import io.grpc.stub.StreamObserver;

import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.LinkedBlockingQueue;

/**
 * Bridges the async, callback-based {@code ConsumeSession} bidi-streaming RPC into a blocking
 * {@link Iterable} of {@link SessionDelivery} - the managed consume loop for exactly one session:
 * claims a session (any-available or targeted), streams delivered items back, and lets the caller
 * ack/deadLetter each one via the delivery itself. No leaseId is exposed here (unlike the unary
 * session API) - the server tracks the lease internally and the gRPC call itself renews it for as
 * long as the stream stays open.
 *
 * <p>Single-use: call {@link #iterator()} at most once. Always {@link #close()} (or
 * {@link #cancel()}) when done - closing lets the session drain, cancelling ends the stream
 * (and releases the session) immediately.
 */
public final class SessionStream implements Iterable<SessionDelivery>, AutoCloseable {
    private static final Object DONE = new Object();

    private final BlockingQueue<Object> queue = new LinkedBlockingQueue<>();
    private final Object lock = new Object();
    private final StreamObserver<ConsumeSessionRequest> requestObserver;
    private volatile String assignedSessionId;
    private boolean iteratorCreated;

    /** Decouples this class from the generated stub type - lets tests substitute a fake stream. */
    @FunctionalInterface
    interface StreamFactory {
        StreamObserver<ConsumeSessionRequest> start(StreamObserver<ConsumeSessionResponse> responseObserver);
    }

    SessionStream(StreamFactory factory, String queueId, ConsumeSessionOptions options) {
        this.assignedSessionId = options.sessionId() == null ? "" : options.sessionId();

        StreamObserver<ConsumeSessionResponse> responseObserver = new StreamObserver<>() {
            @Override
            public void onNext(ConsumeSessionResponse value) {
                queue.add(value);
            }

            @Override
            public void onError(Throwable t) {
                queue.add(t);
            }

            @Override
            public void onCompleted() {
                queue.add(DONE);
            }
        };

        this.requestObserver = factory.start(responseObserver);

        com.daprmq.grpc.ConsumeSessionStart.Builder start = com.daprmq.grpc.ConsumeSessionStart.newBuilder()
                .setQueueId(queueId)
                .setLeaseSeconds(options.leaseSeconds())
                .setPrefetchCount(options.prefetchCount());
        if (options.sessionId() != null) {
            start.setSessionId(options.sessionId());
        }
        send(ConsumeSessionRequest.newBuilder().setStart(start).build());
    }

    @Override
    public Iterator<SessionDelivery> iterator() {
        if (iteratorCreated) {
            throw new IllegalStateException("SessionStream.iterator() may only be called once.");
        }
        iteratorCreated = true;
        return new StreamIterator();
    }

    /** Ends the stream gracefully - this is itself what releases the session server-side. */
    @Override
    public void close() {
        synchronized (lock) {
            try {
                requestObserver.onCompleted();
            } catch (Exception e) {
                // best-effort - the stream may already be broken/completed
            }
        }
    }

    /** Ends the stream immediately, releasing the session faster than a graceful close would. */
    public void cancel() {
        synchronized (lock) {
            try {
                if (requestObserver instanceof ClientCallStreamObserver<ConsumeSessionRequest> cco) {
                    cco.cancel("Consumer stopped", null);
                } else {
                    requestObserver.onCompleted();
                }
            } catch (Exception e) {
                // best-effort - the stream may already be broken/completed
            }
        }
    }

    private void send(ConsumeSessionRequest request) {
        synchronized (lock) {
            requestObserver.onNext(request);
        }
    }

    private static DaprMQException mapSessionError(String errorCode, String message) {
        return switch (errorCode) {
            case "SESSION_NOT_FOUND" -> new SessionNotFoundException(message);
            case "SESSION_LOCKED" -> new SessionLockedException(message);
            case "NO_SESSIONS_AVAILABLE" -> new NoSessionsAvailableException(message);
            case "SESSION_ACTOR_UNAVAILABLE" -> new SessionActorUnavailableException(message);
            default -> new DaprMQException(message, errorCode);
        };
    }

    private final class StreamIterator implements Iterator<SessionDelivery> {
        private SessionDelivery nextItem;
        private boolean fetched;
        private boolean done;

        private void fetchIfNeeded() {
            if (fetched || done) {
                return;
            }
            while (true) {
                Object message;
                try {
                    message = queue.take();
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    done = true;
                    fetched = true;
                    throw new DaprMQException("Interrupted while waiting for ConsumeSession stream");
                }

                if (message == DONE) {
                    done = true;
                    fetched = true;
                    return;
                }

                if (message instanceof RuntimeException re) {
                    done = true;
                    fetched = true;
                    throw re;
                }
                if (message instanceof Throwable t) {
                    done = true;
                    fetched = true;
                    DaprMQException wrapped = new DaprMQException(t.getMessage());
                    wrapped.initCause(t);
                    throw wrapped;
                }

                ConsumeSessionResponse response = (ConsumeSessionResponse) message;
                switch (response.getPayloadCase()) {
                    case SESSION_ASSIGNED -> {
                        assignedSessionId = response.getSessionAssigned().getSessionId();
                    }
                    case DELIVERED -> {
                        SessionDelivered delivered = response.getDelivered();
                        String lockId = delivered.getLockId();
                        JsonNode item = Json.tryParse(delivered.getItemJson());
                        nextItem = new SessionDelivery(
                                assignedSessionId,
                                lockId,
                                item,
                                delivered.getPriority(),
                                delivered.getLockExpiresAt(),
                                () -> send(ConsumeSessionRequest.newBuilder()
                                        .setAck(ConsumeSessionAck.newBuilder().setLockId(lockId))
                                        .build()),
                                () -> send(ConsumeSessionRequest.newBuilder()
                                        .setDeadLetter(ConsumeSessionDeadLetter.newBuilder().setLockId(lockId))
                                        .build()));
                        fetched = true;
                        return;
                    }
                    case ERROR -> {
                        SessionError error = response.getError();
                        done = true;
                        fetched = true;
                        throw mapSessionError(error.getErrorCode(), error.getMessage());
                    }
                    case SESSION_LOST -> {
                        done = true;
                        fetched = true;
                        throw new SessionLostException(response.getSessionLost().getMessage());
                    }
                    default -> {
                        // PAYLOAD_NOT_SET - ignore and keep reading
                    }
                }
            }
        }

        @Override
        public boolean hasNext() {
            fetchIfNeeded();
            return !done;
        }

        @Override
        public SessionDelivery next() {
            fetchIfNeeded();
            if (done) {
                throw new NoSuchElementException();
            }
            fetched = false;
            SessionDelivery item = nextItem;
            nextItem = null;
            return item;
        }
    }
}
