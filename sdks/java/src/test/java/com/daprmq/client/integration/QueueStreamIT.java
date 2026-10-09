package com.daprmq.client.integration;

import com.daprmq.client.ConsumeOptions;
import com.daprmq.client.DaprMQClient;
import com.daprmq.client.QueueConsumer;
import com.daprmq.client.QueueConsumerOptions;
import com.daprmq.client.QueueHandlerFailureAction;
import com.daprmq.client.QueueStream;
import com.daprmq.client.types.DequeueLockedResult;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.QueueDelivery;
import com.fasterxml.jackson.databind.JsonNode;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.testcontainers.DockerClientFactory;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.BooleanSupplier;
import java.util.stream.IntStream;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

/** QS-01..QS-04 (the Consume stream) and QC-01..QC-06 (QueueConsumer) from sdks/testing/INTEGRATION_TESTS.md. */
class QueueStreamIT {
    private static DaprMQServer server;
    private static DaprMQClient client;

    @BeforeAll
    static void startStack() {
        assumeTrue(DockerClientFactory.instance().isDockerAvailable(), "Docker daemon not available");
        server = DaprMQServer.start();
        client = DaprMQClient.create(server.httpUrl(), server.grpcAddress());
    }

    @AfterAll
    static void stopStack() {
        if (client != null) {
            client.close();
        }
        if (server != null) {
            server.close();
        }
    }

    private static String newQueueId() {
        return "java-it-" + UUID.randomUUID().toString().replace("-", "");
    }

    private static void enqueueSeqs(DaprMQClient c, String queueId, int from, int to) {
        List<EnqueueItem> items = new ArrayList<>();
        for (int seq = from; seq <= to; seq++) {
            items.add(new EnqueueItem(Map.of("seq", seq)));
        }
        c.enqueue(queueId, items);
    }

    private static int seq(JsonNode item) {
        return item.get("seq").asInt();
    }

    private static void waitFor(BooleanSupplier condition, String because) throws InterruptedException {
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(30);
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                throw new AssertionError("timed out waiting for " + because);
            }
            TimeUnit.MILLISECONDS.sleep(50);
        }
    }

    /** Leaves the stream: half-close, then wait for the server to apply what was sent and end it. */
    private static void drain(QueueStream stream, java.util.Iterator<QueueDelivery> it) {
        stream.close();
        while (it.hasNext()) {
            it.next();
        }
    }

    @Test
    void QS01_Consume_DeliversInOrder_AndAckRemovesItems() {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 3);

        QueueStream stream = client.consume(queueId, new ConsumeOptions(2, Duration.ofSeconds(30), true, null));
        var it = stream.iterator();
        List<QueueDelivery> delivered = new ArrayList<>();
        while (delivered.size() < 3) {
            QueueDelivery d = it.next();
            delivered.add(d);
            d.ack().run();
        }
        drain(stream, it);

        assertEquals(List.of(1, 2, 3), delivered.stream().map(d -> seq(d.item())).toList());
        assertEquals(List.of(1, 1, 1), delivered.stream().map(QueueDelivery::deliveryCount).toList());
        assertNull(client.dequeueLocked(queueId));
    }

    @Test
    void QS02_Consume_NackRedeliversWithTheNextDeliveryCount() {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 1);

        QueueStream stream = client.consume(queueId);
        var it = stream.iterator();
        QueueDelivery first = it.next();
        first.nack().run();
        QueueDelivery second = it.next();
        second.ack().run();
        drain(stream, it);

        assertEquals(1, seq(second.item()));
        assertEquals(1, first.deliveryCount());
        assertEquals(2, second.deliveryCount());
    }

    @Test
    void QS03_Consume_KeepsADeliveredItemLockedPastItsTtl() throws InterruptedException {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 1);
        List<String> settleFailures = new CopyOnWriteArrayList<>();

        QueueStream stream = client.consume(queueId, new ConsumeOptions(1, Duration.ofSeconds(2), true, (lockId, err) -> settleFailures.add(lockId)));
        var it = stream.iterator();
        QueueDelivery d = it.next();
        TimeUnit.SECONDS.sleep(6);
        assertNull(client.dequeueLocked(queueId, 1, 30, null, true));
        d.ack().run();
        drain(stream, it);

        assertEquals(List.of(), settleFailures);
    }

    @Test
    void QS04_ClosingTheStream_ReturnsUnsettledItemsStraightAway() {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 1);

        QueueStream stream = client.consume(queueId, new ConsumeOptions(1, Duration.ofSeconds(300), false, null));
        var it = stream.iterator();
        it.next();
        drain(stream, it);

        DequeueLockedResult back = client.dequeueLocked(queueId);
        assertNotNull(back);
        assertEquals(List.of(1), back.items().stream().map(i -> seq(i.item())).toList());
    }

    @Test
    void QC01_HandlerSuccess_Acks_AndTheQueueEndsEmpty() throws InterruptedException {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 20);
        List<Integer> handled = new CopyOnWriteArrayList<>();

        try (QueueConsumer consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions(), ctx -> handled.add(seq(ctx.item())))) {
            consumer.start();
            waitFor(() -> handled.size() == 20, "every message to be handled");
        }

        assertEquals(IntStream.rangeClosed(1, 20).boxed().toList(), handled.stream().sorted().toList());
        assertNull(client.dequeueLocked(queueId, 1, 30, null, true));
    }

    @Test
    void QC02_HandlerError_NackRedeliversWithDeliveryCount2() throws InterruptedException {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 1);
        List<Integer> counts = new CopyOnWriteArrayList<>();

        try (QueueConsumer consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions(), ctx -> {
            counts.add(ctx.deliveryCount());
            if (ctx.deliveryCount() == 1) {
                throw new IllegalStateException("first attempt fails");
            }
        })) {
            consumer.start();
            waitFor(() -> counts.size() == 2, "the nacked message to be redelivered");
        }

        assertEquals(List.of(1, 2), counts);
        assertNull(client.dequeueLocked(queueId, 1, 30, null, true));
    }

    @Test
    void QC02_HandlerError_DeadLetterMovesItToTheDeadLetterQueue() throws InterruptedException {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 7, 7);
        DequeueLockedResult[] dead = new DequeueLockedResult[1];

        try (QueueConsumer consumer = new QueueConsumer(client, queueId,
                new QueueConsumerOptions().onHandlerError(QueueHandlerFailureAction.DEAD_LETTER), ctx -> {
                    throw new IllegalStateException("poison");
                })) {
            consumer.start();
            waitFor(() -> (dead[0] = client.dequeueLocked(queueId + "-deadletter")) != null, "the message to reach the dead-letter queue");
        }

        assertEquals(List.of(7), dead[0].items().stream().map(i -> seq(i.item())).toList());
    }

    @Test
    void QC03_MaxConcurrentHandlers_IsNeverExceeded() throws InterruptedException {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 10);
        AtomicInteger running = new AtomicInteger();
        AtomicInteger peak = new AtomicInteger();
        AtomicInteger handled = new AtomicInteger();

        try (QueueConsumer consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions().maxConcurrentHandlers(2), ctx -> {
            peak.accumulateAndGet(running.incrementAndGet(), Math::max);
            Thread.sleep(100);
            running.decrementAndGet();
            handled.incrementAndGet();
        })) {
            consumer.start();
            waitFor(() -> handled.get() == 10, "every message to be handled");
        }

        assertEquals(2, peak.get());
    }

    @Test
    void QC04_StrictOrder_HandlesInQueueOrder_IncludingAfterANack() throws InterruptedException {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 5);
        List<Integer> succeeded = new CopyOnWriteArrayList<>();
        AtomicBoolean failedOnce = new AtomicBoolean();

        try (QueueConsumer consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions().strictOrder(true), ctx -> {
            int seq = seq(ctx.item());
            if (seq == 2 && failedOnce.compareAndSet(false, true)) {
                throw new IllegalStateException("nack 2 once");
            }
            succeeded.add(seq);
        })) {
            consumer.start();
            waitFor(() -> succeeded.size() == 5, "every message to be handled");
        }

        assertEquals(List.of(1, 2, 3, 4, 5), succeeded);
    }

    @Test
    void QC05_Stop_DrainsRunningHandlers_AndReturnsUnstartedMessagesStraightAway() throws InterruptedException {
        String queueId = newQueueId();
        enqueueSeqs(client, queueId, 1, 3);
        CountDownLatch started = new CountDownLatch(1);
        List<Integer> handled = new CopyOnWriteArrayList<>();

        QueueConsumer consumer = new QueueConsumer(client, queueId,
                new QueueConsumerOptions().maxConcurrentHandlers(1).maxActiveMessages(10).lockTtl(Duration.ofSeconds(300)), ctx -> {
                    started.countDown();
                    Thread.sleep(500);
                    handled.add(seq(ctx.item()));
                });
        consumer.start();
        assertTrue(started.await(30, TimeUnit.SECONDS));
        consumer.stop();

        assertEquals(List.of(1), handled);
        // Well inside the 300 s lock, so only the stream's close can have returned them.
        DequeueLockedResult back = client.dequeueLocked(queueId, 10, 30, null, true);
        assertNotNull(back);
        assertEquals(List.of(2, 3), back.items().stream().map(i -> seq(i.item())).toList());
    }

    @Test
    void QC06_BrokenStream_Reconnects_AndEveryMessageIsHandledAtLeastOnce() throws Exception {
        String queueId = newQueueId();
        Map<Integer, Integer> handled = new ConcurrentHashMap<>();

        try (TcpProxy proxy = new TcpProxy(server.grpcAddress());
             DaprMQClient proxied = DaprMQClient.create(server.httpUrl(), proxy.address())) {
            enqueueSeqs(proxied, queueId, 1, 20);
            try (QueueConsumer consumer = new QueueConsumer(proxied, queueId,
                    new QueueConsumerOptions().maxActiveMessages(5).maxConcurrentHandlers(2), ctx -> {
                        Thread.sleep(50);
                        handled.merge(seq(ctx.item()), 1, Integer::sum);
                    })) {
                consumer.start();
                waitFor(() -> handled.size() >= 5, "some messages to be handled before the break");
                proxy.breakConnections();
                waitFor(() -> handled.size() == 20, "every message to be handled after reconnecting");
            }
        }

        assertEquals(IntStream.rangeClosed(1, 20).boxed().toList(), handled.keySet().stream().sorted().toList());
        assertNull(client.dequeueLocked(queueId, 1, 30, null, true));
    }

    /**
     * Forwards a local port to the server so QC-06 can break every open connection without
     * restarting a container, which would re-map its host ports.
     */
    private static final class TcpProxy implements AutoCloseable {
        private final ServerSocket listener = new ServerSocket(0, 50, InetAddress.getLoopbackAddress());
        private final Set<Socket> open = ConcurrentHashMap.newKeySet();

        TcpProxy(String target) throws IOException {
            String host = target.substring(0, target.lastIndexOf(':'));
            int port = Integer.parseInt(target.substring(target.lastIndexOf(':') + 1));
            Thread accept = new Thread(() -> {
                while (!listener.isClosed()) {
                    try {
                        Socket in = listener.accept();
                        Socket out = new Socket(host, port);
                        open.add(in);
                        open.add(out);
                        pipe(in, out);
                        pipe(out, in);
                    } catch (IOException e) {
                        return;
                    }
                }
            });
            accept.setDaemon(true);
            accept.start();
        }

        String address() {
            return "127.0.0.1:" + listener.getLocalPort();
        }

        /** Drops every connection open now; new ones are still accepted. */
        void breakConnections() {
            for (Socket s : open) {
                try {
                    s.setSoLinger(true, 0); // reset, not a clean close
                    s.close();
                } catch (IOException e) {
                    // already closed
                }
            }
            open.clear();
        }

        private static void pipe(Socket from, Socket to) {
            Thread t = new Thread(() -> {
                try (InputStream in = from.getInputStream(); OutputStream out = to.getOutputStream()) {
                    in.transferTo(out);
                } catch (IOException e) {
                    // connection dropped
                } finally {
                    try {
                        from.close();
                        to.close();
                    } catch (IOException e) {
                        // already closed
                    }
                }
            });
            t.setDaemon(true);
            t.start();
        }

        @Override
        public void close() throws IOException {
            listener.close();
            breakConnections();
        }
    }
}
