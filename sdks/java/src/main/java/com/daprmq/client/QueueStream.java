package com.daprmq.client;

import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.StreamClosedException;
import com.daprmq.client.internal.Json;
import com.daprmq.client.types.QueueDelivery;
import com.daprmq.grpc.ConsumeAck;
import com.daprmq.grpc.ConsumeDeadLetter;
import com.daprmq.grpc.ConsumeDelivered;
import com.daprmq.grpc.ConsumeNack;
import com.daprmq.grpc.ConsumeRequest;
import com.daprmq.grpc.ConsumeResponse;
import com.daprmq.grpc.ConsumeSettleFailed;
import com.daprmq.grpc.ConsumeStart;
import com.fasterxml.jackson.databind.JsonNode;
import io.grpc.Status;
import io.grpc.stub.ClientCallStreamObserver;
import io.grpc.stub.StreamObserver;

import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * Bridges the async {@code Consume} bidi-streaming RPC into a blocking {@link Iterable} of
 * {@link QueueDelivery}: the server keeps up to {@code prefetchCount} locked items delivered,
 * refills as they are settled, and renews their locks, so the client never extends them.
 *
 * <p>Single-use: call {@link #iterator()} at most once. Always {@link #close()} (or
 * {@link #cancel()}) when done. Closing half-closes: the server applies every settlement already
 * sent, then returns the unsettled items to the queue and ends the stream. After it, the iterator
 * hands out nothing more but keeps reading until the server ends the stream (iterate on to wait
 * for that), settling throws {@link StreamClosedException}, and the call is cancelled if the server
 * hasn't ended it within 5 s.
 */
public final class QueueStream implements Iterable<QueueDelivery>, AutoCloseable {
    private static final Object DONE = new Object();

    /** How long a closed stream waits for the server to end it before cancelling the call. Overridable for tests. */
    static volatile long drainTimeoutMillis = 5_000;

    private final BlockingQueue<Object> queue = new LinkedBlockingQueue<>();
    private final Object lock = new Object(); // serialises writes: a close can race an ack
    private final StreamObserver<ConsumeRequest> requestObserver;
    private final ConsumeOptions options;
    private volatile boolean closed;
    private volatile boolean ended;
    private volatile boolean drainExpired;
    private boolean iteratorCreated;

    /** Decouples this class from the generated stub type - lets tests substitute a fake stream. */
    @FunctionalInterface
    interface StreamFactory {
        StreamObserver<ConsumeRequest> start(StreamObserver<ConsumeResponse> responseObserver);
    }

    QueueStream(StreamFactory factory, String queueId, ConsumeOptions options) {
        this.options = options;
        StreamObserver<ConsumeResponse> responseObserver = new StreamObserver<>() {
            @Override
            public void onNext(ConsumeResponse value) {
                queue.add(value);
            }

            @Override
            public void onError(Throwable t) {
                ended = true;
                queue.add(t);
            }

            @Override
            public void onCompleted() {
                ended = true;
                queue.add(DONE);
            }
        };

        this.requestObserver = factory.start(responseObserver);

        long lockTtlMillis = options.lockTtl() == null ? 0 : options.lockTtl().toMillis();
        send(ConsumeRequest.newBuilder().setStart(ConsumeStart.newBuilder()
                .setQueueId(queueId)
                .setPrefetchCount(Math.max(options.prefetchCount(), 1))
                .setLockTtlSeconds(lockTtlMillis > 0 ? (int) Math.ceil(lockTtlMillis / 1000.0) : 30)
                .setAllowCompetingConsumers(options.allowCompetingConsumers())).build());
    }

    @Override
    public Iterator<QueueDelivery> iterator() {
        if (iteratorCreated) {
            throw new IllegalStateException("QueueStream.iterator() may only be called once.");
        }
        iteratorCreated = true;
        return new StreamIterator();
    }

    /** Ends the stream gracefully: the server returns the unsettled items straight away. */
    @Override
    public void close() {
        synchronized (lock) {
            if (closed) {
                return;
            }
            closed = true;
            try {
                requestObserver.onCompleted();
            } catch (Exception e) {
                // best-effort - the stream may already be broken/completed
            }
        }
        CompletableFuture.delayedExecutor(drainTimeoutMillis, TimeUnit.MILLISECONDS).execute(() -> {
            if (!ended) {
                drainExpired = true;
                cancel();
            }
        });
    }

    /** Ends the stream immediately, dropping settlements the server hasn't read yet. */
    public void cancel() {
        synchronized (lock) {
            closed = true;
            try {
                if (requestObserver instanceof ClientCallStreamObserver<ConsumeRequest> cco) {
                    cco.cancel("Consumer stopped", null);
                } else {
                    requestObserver.onCompleted();
                }
            } catch (Exception e) {
                // best-effort - the stream may already be broken/completed
            }
        }
        // Ends the iterator whether or not the transport reports the cancellation.
        queue.add(Status.CANCELLED.withDescription("Consumer stopped").asRuntimeException());
    }

    private void send(ConsumeRequest request) {
        synchronized (lock) {
            if (closed || ended) {
                throw new StreamClosedException("The Consume stream is closed; this item can no longer be settled on it.");
            }
            requestObserver.onNext(request);
        }
    }

    private final class StreamIterator implements Iterator<QueueDelivery> {
        private QueueDelivery nextItem;
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
                    throw new DaprMQException("Interrupted while waiting for Consume stream");
                }

                if (message == DONE) {
                    done = true;
                    fetched = true;
                    return;
                }

                if (message instanceof Throwable && drainExpired) {
                    done = true; // the call was cancelled because the server didn't end it in time
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

                ConsumeResponse response = (ConsumeResponse) message;
                if (closed) {
                    continue; // closing: let the server finish, but hand out nothing more
                }
                switch (response.getPayloadCase()) {
                    case DELIVERED -> {
                        ConsumeDelivered delivered = response.getDelivered();
                        String lockId = delivered.getLockId();
                        JsonNode item = Json.tryParse(delivered.getItemJson());
                        nextItem = new QueueDelivery(
                                lockId,
                                item,
                                delivered.getPriority(),
                                delivered.getLockExpiresAt(),
                                delivered.getDeliveryCount(),
                                () -> send(ConsumeRequest.newBuilder().setAck(ConsumeAck.newBuilder().setLockId(lockId)).build()),
                                () -> send(ConsumeRequest.newBuilder().setNack(ConsumeNack.newBuilder().setLockId(lockId)).build()),
                                () -> send(ConsumeRequest.newBuilder().setDeadLetter(ConsumeDeadLetter.newBuilder().setLockId(lockId)).build()));
                        fetched = true;
                        return;
                    }
                    case SETTLE_FAILED -> {
                        if (options.onSettleFailed() != null) {
                            ConsumeSettleFailed failed = response.getSettleFailed();
                            options.onSettleFailed().accept(failed.getLockId(), DaprMQClient.mapLockError(failed.getErrorCode(), failed.getMessage()));
                        }
                    }
                    case ERROR -> {
                        done = true;
                        fetched = true;
                        throw DaprMQClient.mapLockError(response.getError().getErrorCode(), response.getError().getMessage());
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
        public QueueDelivery next() {
            fetchIfNeeded();
            if (done) {
                throw new NoSuchElementException();
            }
            fetched = false;
            QueueDelivery item = nextItem;
            nextItem = null;
            return item;
        }
    }
}
