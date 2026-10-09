package com.daprmq.client;

import com.daprmq.grpc.ConsumeDelivered;
import com.daprmq.grpc.ConsumeRequest;
import com.daprmq.grpc.ConsumeResponse;
import io.grpc.Status;
import io.grpc.stub.StreamObserver;
import org.junit.jupiter.api.Test;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BooleanSupplier;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class QueueConsumerTest {

    /** One fake Consume stream: the test pushes deliveries; the server ends it when the client half-closes. */
    private static final class FakeServerStream {
        final FakeRequestObserver<ConsumeRequest> requests = new FakeRequestObserver<>();
        volatile StreamObserver<ConsumeResponse> responses;
        private final List<Runnable> pending = new ArrayList<>();

        FakeServerStream() {
            requests.onHalfClose = () -> responses.onCompleted();
        }

        synchronized QueueStream open(ConsumeOptions options) {
            QueueStream stream = new QueueStream(observer -> {
                responses = observer;
                return requests;
            }, "q", options);
            pending.forEach(Runnable::run);
            pending.clear();
            return stream;
        }

        synchronized void whenOpen(Runnable action) {
            if (responses != null) {
                action.run();
            } else {
                pending.add(action);
            }
        }

        void deliver(String lockId) {
            deliver(lockId, 1);
        }

        void deliver(String lockId, int deliveryCount) {
            whenOpen(() -> responses.onNext(ConsumeResponse.newBuilder().setDelivered(ConsumeDelivered.newBuilder()
                    .setLockId(lockId).setItemJson("{\"n\":1}").setPriority(1).setLockExpiresAt(0).setDeliveryCount(deliveryCount)).build()));
        }

        void breakStream() {
            whenOpen(() -> responses.onError(Status.UNAVAILABLE.withDescription("stream broke").asRuntimeException()));
        }

        /** Settlement frames sent after Start, as "action:lockId". */
        List<String> settled() {
            List<String> out = new ArrayList<>();
            for (ConsumeRequest r : requests.sent) {
                switch (r.getPayloadCase()) {
                    case ACK -> out.add("ack:" + r.getAck().getLockId());
                    case NACK -> out.add("nack:" + r.getNack().getLockId());
                    case DEAD_LETTER -> out.add("deadLetter:" + r.getDeadLetter().getLockId());
                    default -> { }
                }
            }
            return out;
        }
    }

    private static final class FakeClient implements QueueCapableClient {
        final List<FakeServerStream> streams;
        final List<ConsumeOptions> opened = new CopyOnWriteArrayList<>();

        FakeClient(FakeServerStream... streams) {
            this.streams = List.of(streams);
        }

        @Override
        public QueueStream consume(String queueId, ConsumeOptions options) {
            assertEquals("q", queueId);
            opened.add(options);
            return streams.get(Math.min(opened.size(), streams.size()) - 1).open(options);
        }
    }

    private static void waitUntil(BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("condition not met");
            }
            TimeUnit.MILLISECONDS.sleep(5);
        }
    }

    private static QueueConsumer consumer(FakeClient client, QueueConsumerOptions options, QueueHandler handler, List<Long> delays) {
        QueueConsumer consumer = new QueueConsumer(client, "q", options, handler);
        consumer.delay = millis -> delays.add(millis);
        return consumer;
    }

    @Test
    void opensTheStreamWithTheDefaults() throws Exception {
        FakeClient client = new FakeClient(new FakeServerStream());
        QueueConsumer consumer = consumer(client, new QueueConsumerOptions(), ctx -> { }, new CopyOnWriteArrayList<>());

        consumer.start();
        waitUntil(() -> client.opened.size() == 1);
        consumer.stop();

        ConsumeOptions opened = client.opened.get(0);
        assertEquals(100, opened.prefetchCount());
        assertEquals(Duration.ofSeconds(30), opened.lockTtl());
        assertTrue(opened.allowCompetingConsumers());
    }

    @Test
    void acksOnSuccessAndPassesTheDeliveryToTheHandler() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        List<QueueMessageContext> contexts = new CopyOnWriteArrayList<>();
        QueueConsumer consumer = consumer(new FakeClient(stream), new QueueConsumerOptions(), contexts::add, new CopyOnWriteArrayList<>());

        consumer.start();
        stream.deliver("L1", 3);
        waitUntil(() -> stream.settled().size() == 1);
        consumer.stop();

        assertEquals(List.of("ack:L1"), stream.settled());
        QueueMessageContext ctx = contexts.get(0);
        assertEquals("q", ctx.queueId());
        assertEquals("L1", ctx.lockId());
        assertEquals(1, ctx.item().get("n").asInt());
        assertEquals(1, ctx.priority());
        assertEquals(3, ctx.deliveryCount());
    }

    @Test
    void nacksAFailedMessageByDefault() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        QueueConsumer consumer = consumer(new FakeClient(stream), new QueueConsumerOptions(), ctx -> {
            throw new IllegalStateException("boom");
        }, new CopyOnWriteArrayList<>());

        consumer.start();
        stream.deliver("L1");
        waitUntil(() -> stream.settled().size() == 1);
        consumer.stop();

        assertEquals(List.of("nack:L1"), stream.settled());
    }

    @Test
    void deadLettersAFailedMessageWhenConfigured() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        QueueConsumer consumer = consumer(new FakeClient(stream),
                new QueueConsumerOptions().onHandlerError(QueueHandlerFailureAction.DEAD_LETTER),
                ctx -> {
                    throw new IllegalStateException("boom");
                }, new CopyOnWriteArrayList<>());

        consumer.start();
        stream.deliver("L1");
        waitUntil(() -> stream.settled().size() == 1);
        consumer.stop();

        assertEquals(List.of("deadLetter:L1"), stream.settled());
    }

    @Test
    void pacesNacksAtMaxRetriableErrorsPerSec() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        List<Long> delays = new CopyOnWriteArrayList<>();
        QueueConsumer consumer = consumer(new FakeClient(stream),
                new QueueConsumerOptions().maxRetriableErrorsPerSec(10).strictOrder(true),
                ctx -> {
                    throw new IllegalStateException("boom");
                }, delays);

        consumer.start();
        stream.deliver("L1");
        stream.deliver("L2");
        stream.deliver("L3");
        waitUntil(() -> stream.settled().size() == 3);
        consumer.stop();

        // The first nack goes straight away; each later one waits for its own slot, 100 ms after the
        // previous one (the fake delay doesn't pass that time).
        assertEquals(2, delays.size());
        assertTrue(delays.get(0) >= 50 && delays.get(0) <= 100, "first wait " + delays.get(0));
        assertTrue(delays.get(1) >= 150 && delays.get(1) <= 200, "second wait " + delays.get(1));
    }

    @Test
    void neverExceedsMaxConcurrentHandlers() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        AtomicInteger running = new AtomicInteger();
        AtomicInteger peak = new AtomicInteger();
        QueueConsumer consumer = consumer(new FakeClient(stream), new QueueConsumerOptions().maxConcurrentHandlers(2), ctx -> {
            peak.accumulateAndGet(running.incrementAndGet(), Math::max);
            Thread.sleep(30);
            running.decrementAndGet();
        }, new CopyOnWriteArrayList<>());

        consumer.start();
        for (int i = 0; i < 8; i++) {
            stream.deliver("L" + i);
        }
        waitUntil(() -> stream.settled().size() == 8);
        consumer.stop();

        assertEquals(2, peak.get());
    }

    @Test
    void strictOrderOpensAWindowOfOneWithoutCompetingConsumersAndHandlesOneAtATime() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        FakeClient client = new FakeClient(stream);
        AtomicInteger running = new AtomicInteger();
        AtomicInteger peak = new AtomicInteger();
        QueueConsumer consumer = consumer(client, new QueueConsumerOptions().strictOrder(true), ctx -> {
            peak.accumulateAndGet(running.incrementAndGet(), Math::max);
            Thread.sleep(10);
            running.decrementAndGet();
        }, new CopyOnWriteArrayList<>());

        consumer.start();
        for (int i = 0; i < 4; i++) {
            stream.deliver("L" + i);
        }
        waitUntil(() -> stream.settled().size() == 4);
        consumer.stop();

        assertEquals(1, client.opened.get(0).prefetchCount());
        assertFalse(client.opened.get(0).allowCompetingConsumers());
        assertEquals(1, peak.get());
        assertEquals(List.of("ack:L0", "ack:L1", "ack:L2", "ack:L3"), stream.settled());
    }

    @Test
    void stopLetsTheRunningHandlerAckBeforeClosingTheStreamAndStartsNoMore() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        List<String> started = new CopyOnWriteArrayList<>();
        CountDownLatch release = new CountDownLatch(1);
        QueueConsumer consumer = consumer(new FakeClient(stream), new QueueConsumerOptions().maxConcurrentHandlers(1), ctx -> {
            started.add(ctx.lockId());
            release.await();
        }, new CopyOnWriteArrayList<>());

        consumer.start();
        stream.deliver("L1");
        stream.deliver("L2"); // prefetched; waits for the one handler slot
        waitUntil(() -> started.size() == 1);

        Thread stopping = new Thread(consumer::stop);
        stopping.start();
        TimeUnit.MILLISECONDS.sleep(50);
        assertFalse(stream.requests.completed, "the stream must stay open while a handler runs");
        release.countDown();
        stopping.join(2000);

        assertFalse(stopping.isAlive());
        assertEquals(List.of("L1"), started);
        assertEquals(List.of("ack:L1"), stream.settled());
        assertTrue(stream.requests.completed);
    }

    @Test
    void stopInterruptsHandlersOnceTheDrainTimeoutRunsOut() throws Exception {
        FakeServerStream stream = new FakeServerStream();
        AtomicBoolean interrupted = new AtomicBoolean();
        QueueConsumer consumer = consumer(new FakeClient(stream), new QueueConsumerOptions().drainTimeoutMillis(100), ctx -> {
            try {
                Thread.sleep(60_000);
            } catch (InterruptedException e) {
                interrupted.set(true);
                throw e;
            }
        }, new CopyOnWriteArrayList<>());

        consumer.start();
        stream.deliver("L1");
        TimeUnit.MILLISECONDS.sleep(50);
        long began = System.nanoTime();
        consumer.stop();

        assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - began) < 2000);
        assertTrue(interrupted.get());
        assertEquals(List.of(), stream.settled()); // left unsettled: the server returns it when the stream closes
    }

    @Test
    void reopensABrokenStreamBackingOffUntilADeliveryResetsIt() throws Exception {
        FakeServerStream broken1 = new FakeServerStream();
        FakeServerStream broken2 = new FakeServerStream();
        FakeServerStream delivering = new FakeServerStream();
        FakeServerStream broken3 = new FakeServerStream();
        FakeServerStream last = new FakeServerStream();
        broken1.breakStream();
        broken2.breakStream();
        delivering.deliver("L1");
        FakeClient client = new FakeClient(broken1, broken2, delivering, broken3, last);
        List<Long> delays = new CopyOnWriteArrayList<>();
        QueueConsumer consumer = consumer(client, new QueueConsumerOptions().minBackoffSeconds(1).maxBackoffSeconds(60), ctx -> { }, delays);

        consumer.start();
        waitUntil(() -> delivering.settled().size() == 1);
        delivering.breakStream();
        waitUntil(() -> client.opened.size() == 4);
        broken3.breakStream();
        waitUntil(() -> client.opened.size() == 5);
        consumer.stop();

        assertEquals(List.of(1000L, 2000L, 1000L, 2000L), delays);
    }
}
