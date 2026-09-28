package com.daprmq.client.integration;

import com.daprmq.client.DaprMQClient;
import com.daprmq.client.SessionQueueConsumer;
import com.daprmq.client.SessionQueueConsumerOptions;
import com.daprmq.client.types.EnqueueItem;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.testcontainers.DockerClientFactory;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

class SessionQueueConsumerIT {
    private static final int ITEMS_PER_SESSION = 5;
    private static final long SLOW_HANDLER_MILLIS = 300;

    private static DaprMQServer server;

    @BeforeAll
    static void startStack() {
        assumeTrue(DockerClientFactory.instance().isDockerAvailable(), "Docker daemon not available");
        server = DaprMQServer.start();
    }

    @AfterAll
    static void stopStack() {
        if (server != null) {
            server.close();
        }
    }

    @Test
    void K02_MultiSession_PreservesPerSessionOrder_AndIsolatesThroughput() throws Exception {
        String queueId = "java-it-" + UUID.randomUUID().toString().replace("-", "");

        try (DaprMQClient client = DaprMQClient.create(server.httpUrl(), server.grpcAddress())) {
            for (int seq = 1; seq <= ITEMS_PER_SESSION; seq++) {
                for (String sessionId : List.of("fast", "slow")) {
                    Map<String, Object> item = new LinkedHashMap<>();
                    item.put("sessionId", sessionId);
                    item.put("seq", seq);
                    client.enqueue(queueId, List.of(new EnqueueItem(item, 1, null, sessionId)));
                }
            }

            Map<String, List<Integer>> observed = new ConcurrentHashMap<>(Map.of(
                    "fast", java.util.Collections.synchronizedList(new ArrayList<>()),
                    "slow", java.util.Collections.synchronizedList(new ArrayList<>())));
            CountDownLatch allDone = new CountDownLatch(2);
            long started = System.nanoTime();
            AtomicLong fastCompletedMillis = new AtomicLong(-1);

            SessionQueueConsumerOptions options = new SessionQueueConsumerOptions()
                    .maxConcurrentSessions(2)
                    .leaseSeconds(30)
                    .prefetchCount(10)
                    .minBackoffSeconds(1)
                    .maxBackoffSeconds(2);

            SessionQueueConsumer consumer = new SessionQueueConsumer(client, queueId, options, ctx -> {
                if ("slow".equals(ctx.sessionId())) {
                    Thread.sleep(SLOW_HANDLER_MILLIS);
                }
                List<Integer> seen = observed.get(ctx.sessionId());
                seen.add(ctx.item().path("seq").asInt());
                if (seen.size() == ITEMS_PER_SESSION) {
                    if ("fast".equals(ctx.sessionId())) {
                        fastCompletedMillis.set((System.nanoTime() - started) / 1_000_000);
                    }
                    allDone.countDown();
                }
            });

            consumer.start();
            try {
                assertTrue(allDone.await(30, TimeUnit.SECONDS), "timed out waiting for both sessions to drain");
            } finally {
                consumer.stop();
            }

            List<Integer> expected = java.util.stream.IntStream.rangeClosed(1, ITEMS_PER_SESSION).boxed().toList();
            assertEquals(expected, List.copyOf(observed.get("fast")));
            assertEquals(expected, List.copyOf(observed.get("slow")));

            // "slow" needs >= ITEMS_PER_SESSION * 300ms (sequential within its own stream); "fast" must
            // finish well inside that, proving the sessions run on independent streams/slots.
            long slowFloorMillis = ITEMS_PER_SESSION * SLOW_HANDLER_MILLIS;
            assertTrue(fastCompletedMillis.get() >= 0, "fast session never completed");
            assertTrue(fastCompletedMillis.get() < slowFloorMillis,
                    "fast session took " + fastCompletedMillis.get() + "ms, expected well under slow's " + slowFloorMillis + "ms floor");
        }
    }
}
