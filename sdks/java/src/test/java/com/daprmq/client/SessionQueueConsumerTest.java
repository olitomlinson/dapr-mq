package com.daprmq.client;

import com.daprmq.client.errors.NoSessionsAvailableException;
import com.daprmq.grpc.ConsumeSessionResponse;
import com.daprmq.grpc.SessionDelivered;
import io.grpc.stub.StreamObserver;
import org.junit.jupiter.api.Test;

import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

class SessionQueueConsumerTest {

    private SessionStream streamDeliveringOneItem(FakeRequestObserver requestObserver, String lockId) {
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
        FakeRequestObserver requestObserver = new FakeRequestObserver();
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
    void deadLettersOnHandlerExceptionByDefault() throws InterruptedException {
        FakeRequestObserver requestObserver = new FakeRequestObserver();
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
        } finally {
            consumer.stop();
        }

        assertEquals("L2", requestObserver.sent.get(1).getDeadLetter().getLockId());
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
}
