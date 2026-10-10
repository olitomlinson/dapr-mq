package com.daprmq.client;

import com.daprmq.grpc.ConsumeSessionRequest;

import com.daprmq.client.errors.NoSessionsAvailableException;
import com.daprmq.grpc.ConsumeSessionResponse;
import com.daprmq.grpc.SessionDelivered;
import io.grpc.stub.StreamObserver;
import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class SessionQueueConsumerTest {

    private void awaitFrames(FakeRequestObserver<ConsumeSessionRequest> requestObserver, int count) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(2);
        while (requestObserver.sent.size() < count && System.nanoTime() < deadline) {
            Thread.sleep(10);
        }
        assertEquals(count, requestObserver.sent.size());
    }

    private SessionStream streamDeliveringOneItem(FakeRequestObserver<ConsumeSessionRequest> requestObserver, String lockId) {
        AtomicReference<StreamObserver<ConsumeSessionResponse>> captured = new AtomicReference<>();
        SessionStream.StreamFactory factory = observer -> {
            captured.set(observer);
            return requestObserver;
        };
        SessionStream stream = new SessionStream(factory, "q", ConsumeSessionOptions.defaults());

        captured.get().onNext(ConsumeSessionResponse.newBuilder()
                .setDelivered(SessionDelivered.newBuilder()
                        .setLockId(lockId)
                        .setItemJson("{}")
                        .setPriority(0)
                        .setLockExpiresAt(1)
                        .build())
                .build());
        captured.get().onCompleted();

        return stream;
    }

    @Test
    void processesADeliveryAndAcksItThenDrainsCleanly() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        SessionStream stream = streamDeliveringOneItem(requestObserver, "L1");

        CountDownLatch handled = new CountDownLatch(1);
        List<String> handledLockIds = new CopyOnWriteArrayList<>();
        SessionCapableClient fakeClient = (queueId, options) -> stream;

        SessionQueueConsumerOptions options = new SessionQueueConsumerOptions().maxConcurrentSessions(1).drainTimeoutMillis(2000);
        SessionQueueConsumer consumer = new SessionQueueConsumer(fakeClient, "q", options, ctx -> {
            handledLockIds.add(ctx.lockId());
            handled.countDown();
        });

        consumer.start();
        try {
            assertTrue(handled.await(2, TimeUnit.SECONDS));
        } finally {
            consumer.stop();
        }

        assertEquals(List.of("L1"), handledLockIds);
        // sent[0] is the Start frame; sent[1] is the Ack the consumer issues after the handler returns.
        assertEquals("L1", requestObserver.sent.get(1).getAck().getLockId());
    }

    @Test
    void passesTheSessionIdleTimeoutToEachStream() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        SessionStream stream = streamDeliveringOneItem(requestObserver, "L1");
        List<ConsumeSessionOptions> requested = new CopyOnWriteArrayList<>();
        CountDownLatch handled = new CountDownLatch(1);
        SessionCapableClient fakeClient = (queueId, options) -> {
            requested.add(options);
            return stream;
        };

        SessionQueueConsumerOptions options = new SessionQueueConsumerOptions()
                .maxConcurrentSessions(1).sessionIdleTimeoutSeconds(2).drainTimeoutMillis(2000);
        SessionQueueConsumer consumer = new SessionQueueConsumer(fakeClient, "q", options, ctx -> handled.countDown());

        consumer.start();
        try {
            assertTrue(handled.await(2, TimeUnit.SECONDS));
        } finally {
            consumer.stop();
        }

        assertEquals(2, requested.get(0).sessionIdleTimeoutSeconds());
    }

    @Test
    void deadLettersOnHandlerExceptionByDefault() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        SessionStream stream = streamDeliveringOneItem(requestObserver, "L2");

        CountDownLatch handled = new CountDownLatch(1);
        SessionCapableClient fakeClient = (queueId, options) -> stream;
        SessionQueueConsumerOptions options = new SessionQueueConsumerOptions().maxConcurrentSessions(1).drainTimeoutMillis(2000);

        SessionQueueConsumer consumer = new SessionQueueConsumer(fakeClient, "q", options, ctx -> {
            handled.countDown();
            throw new RuntimeException("boom");
        });

        consumer.start();
        try {
            assertTrue(handled.await(2, TimeUnit.SECONDS));
            // Wait for the dead-letter frame itself: stop() sets `stopping`, and handleDelivery
            // rethrows instead of dead-lettering once that is set.
            awaitFrames(requestObserver, 2);
        } finally {
            consumer.stop();
        }

        assertEquals("L2", requestObserver.sent.get(1).getDeadLetter().getLockId());
    }

    @Test
    void nacksOnHandlerExceptionWhenConfigured() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        SessionStream stream = streamDeliveringOneItem(requestObserver, "L3");

        CountDownLatch handled = new CountDownLatch(1);
        SessionCapableClient fakeClient = (queueId, options) -> stream;
        SessionQueueConsumerOptions options = new SessionQueueConsumerOptions()
                .maxConcurrentSessions(1)
                .drainTimeoutMillis(2000)
                .onHandlerException(SessionHandlerFailureAction.NACK_MESSAGE);

        SessionQueueConsumer consumer = new SessionQueueConsumer(fakeClient, "q", options, ctx -> {
            handled.countDown();
            throw new RuntimeException("boom");
        });

        consumer.start();
        try {
            assertTrue(handled.await(2, TimeUnit.SECONDS));
            awaitFrames(requestObserver, 2);
        } finally {
            consumer.stop();
        }

        assertEquals("L3", requestObserver.sent.get(1).getNack().getLockId());
    }

    @Test
    void backsOffWithDoublingDelaysOnRepeatedClaimFailures() throws InterruptedException {
        SessionCapableClient fakeClient = (queueId, options) -> {
            throw new NoSessionsAvailableException("none available");
        };

        List<Long> delays = new CopyOnWriteArrayList<>();
        CountDownLatch threeDelays = new CountDownLatch(3);
        SessionQueueConsumerOptions options = new SessionQueueConsumerOptions()
                .maxConcurrentSessions(1)
                .minBackoffSeconds(1)
                .maxBackoffSeconds(60)
                .drainTimeoutMillis(2000);

        SessionQueueConsumer consumer = new SessionQueueConsumer(fakeClient, "q", options, ctx -> {
        });
        consumer.delay = millis -> {
            delays.add(millis);
            threeDelays.countDown();
        };

        consumer.start();
        try {
            assertTrue(threeDelays.await(2, TimeUnit.SECONDS));
        } finally {
            consumer.stop();
        }

        assertEquals(1000L, delays.get(0));
        assertEquals(2000L, delays.get(1));
        assertEquals(4000L, delays.get(2));
    }

    /** A stream that delivers the given items, then - like the real server - ends only once the client half-closes. */
    private SessionStream streamEndingOnHalfClose(FakeRequestObserver<ConsumeSessionRequest> requestObserver, String... lockIds) {
        AtomicReference<StreamObserver<ConsumeSessionResponse>> captured = new AtomicReference<>();
        SessionStream.StreamFactory factory = observer -> {
            captured.set(observer);
            return requestObserver;
        };
        requestObserver.onHalfClose = () -> captured.get().onCompleted();
        SessionStream stream = new SessionStream(factory, "q", ConsumeSessionOptions.defaults());
        for (String lockId : lockIds) {
            captured.get().onNext(ConsumeSessionResponse.newBuilder()
                    .setDelivered(SessionDelivered.newBuilder().setLockId(lockId).setItemJson("{}").setPriority(0).setLockExpiresAt(1).build())
                    .build());
        }
        return stream;
    }

    @Test
    void stopDuringAHandlerLetsItFinishAndAckBeforeClosingTheStream() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        SessionStream stream = streamEndingOnHalfClose(requestObserver, "L1");
        CountDownLatch entered = new CountDownLatch(1);
        AtomicBoolean interrupted = new AtomicBoolean();
        AtomicBoolean closedUnderHandler = new AtomicBoolean();
        SessionQueueConsumer consumer = new SessionQueueConsumer((queueId, options) -> stream, "q",
                new SessionQueueConsumerOptions().maxConcurrentSessions(1), ctx -> {
                    entered.countDown();
                    try {
                        Thread.sleep(200);
                    } catch (InterruptedException e) {
                        interrupted.set(true);
                    }
                    closedUnderHandler.set(requestObserver.completed || requestObserver.cancelled);
                });

        consumer.start();
        assertTrue(entered.await(2, TimeUnit.SECONDS));
        consumer.stop();

        assertFalse(interrupted.get());
        assertFalse(closedUnderHandler.get());
        assertEquals("L1", requestObserver.sent.get(1).getAck().getLockId(), "stop() returned before the in-flight message was acked");
        assertTrue(requestObserver.completed);
        assertFalse(requestObserver.cancelled);
    }

    @Test
    void stopPastTheDrainTimeoutInterruptsTheHandler() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        SessionStream stream = streamEndingOnHalfClose(requestObserver, "L1");
        CountDownLatch entered = new CountDownLatch(1);
        AtomicBoolean interrupted = new AtomicBoolean();
        SessionQueueConsumer consumer = new SessionQueueConsumer((queueId, options) -> stream, "q",
                new SessionQueueConsumerOptions().maxConcurrentSessions(1).drainTimeoutMillis(100), ctx -> {
                    entered.countDown();
                    try {
                        Thread.sleep(3_600_000);
                    } catch (InterruptedException e) {
                        interrupted.set(true);
                        throw e;
                    }
                });

        consumer.start();
        assertTrue(entered.await(2, TimeUnit.SECONDS));
        long started = System.nanoTime();
        consumer.stop();

        assertTrue(System.nanoTime() - started < TimeUnit.SECONDS.toNanos(2));
        assertTrue(interrupted.get());
        assertEquals(1, requestObserver.sent.size()); // unsettled: it returns with the session
    }

    @Test
    void idleStopHalfClosesTheStreamPromptly() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        CountDownLatch opened = new CountDownLatch(1);
        SessionQueueConsumer consumer = new SessionQueueConsumer((queueId, options) -> {
            opened.countDown();
            return streamEndingOnHalfClose(requestObserver);
        }, "q", new SessionQueueConsumerOptions().maxConcurrentSessions(1), ctx -> {
        });

        consumer.start();
        assertTrue(opened.await(2, TimeUnit.SECONDS));
        Thread.sleep(50); // let the slot block waiting for a delivery
        long started = System.nanoTime();
        consumer.stop();

        assertTrue(System.nanoTime() - started < TimeUnit.SECONDS.toNanos(1));
        assertTrue(requestObserver.completed);
        assertFalse(requestObserver.cancelled);
    }

    @Test
    void stopDuringAHandlerDoesNotStartAPrefetchedMessage() throws InterruptedException {
        FakeRequestObserver<ConsumeSessionRequest> requestObserver = new FakeRequestObserver<>();
        SessionStream stream = streamEndingOnHalfClose(requestObserver, "L1", "L2");
        CountDownLatch entered = new CountDownLatch(1);
        List<String> handled = new CopyOnWriteArrayList<>();
        SessionQueueConsumer consumer = new SessionQueueConsumer((queueId, options) -> stream, "q",
                new SessionQueueConsumerOptions().maxConcurrentSessions(1).prefetchCount(2), ctx -> {
                    handled.add(ctx.lockId());
                    entered.countDown();
                    Thread.sleep(100);
                });

        consumer.start();
        assertTrue(entered.await(2, TimeUnit.SECONDS));
        consumer.stop();

        assertEquals(List.of("L1"), handled);
        List<String> acked = new ArrayList<>();
        requestObserver.sent.stream().filter(r -> r.hasAck()).forEach(r -> acked.add(r.getAck().getLockId()));
        assertEquals(List.of("L1"), acked);
    }
}
