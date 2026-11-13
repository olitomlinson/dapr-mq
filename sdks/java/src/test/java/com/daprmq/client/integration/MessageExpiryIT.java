package com.daprmq.client.integration;

import com.daprmq.client.DaprMQClient;
import com.daprmq.client.QueueStream;
import com.daprmq.client.types.DequeueLockedItem;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.QueueDelivery;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.testcontainers.DockerClientFactory;

import java.util.List;
import java.util.Map;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

/**
 * E-01..E-03 from sdks/testing/INTEGRATION_TESTS.md: message expiry. The stack sets
 * DAPRMQ_MIN_EXPIRY_TTL_SECONDS to 1 so an item can expire within a test.
 */
class MessageExpiryIT {
    // Server default, DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS unset.
    private static final int DEAD_LETTER_EXPIRY_TTL_SECONDS = 2_419_200;

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

    /** Allows for the API server stamping the expiry slightly after the test read its clock. */
    private static void assertNear(Double expiryAt, double want) {
        assertTrue(expiryAt != null && expiryAt >= want - 5 && expiryAt <= want + 30, "expiryAt " + expiryAt + " vs " + want);
    }

    @Test
    void e01ExpiredItemIsNotDelivered() throws InterruptedException {
        String queueId = newQueueId();
        client.enqueue(queueId, List.of(new EnqueueItem(Map.of("seq", 1)).withExpiryTtlSeconds(1)));

        Thread.sleep(2500);

        assertNull(client.dequeueLocked(queueId)); // null is an empty queue
    }

    @Test
    void e02DequeuedAndConsumedItemsCarryTheirExpiry() {
        String queueId = newQueueId();
        double before = System.currentTimeMillis() / 1000.0;
        client.enqueue(queueId, List.of(
                new EnqueueItem(Map.of("seq", 1)).withExpiryTtlSeconds(3600),
                new EnqueueItem(Map.of("seq", 2)).withExpiryTtlSeconds(7200)));

        DequeueLockedItem locked = client.dequeueLocked(queueId).items().get(0);
        assertEquals(3600, locked.expiry().expiryTtlSeconds());
        assertNear(locked.expiry().expiryAt(), before + 3600);
        assertNull(locked.expiry().originalExpiryTtlSeconds());
        client.acknowledge(queueId, locked.lockId());

        try (QueueStream stream = client.consume(queueId)) {
            QueueDelivery delivery = stream.iterator().next();
            assertEquals(7200, delivery.expiry().expiryTtlSeconds());
            assertNear(delivery.expiry().expiryAt(), before + 7200);
            delivery.ack().run();
        }
    }

    @Test
    void e03DeadLetteredItemCarriesTheOriginalTimeToLive() {
        String queueId = newQueueId();
        client.enqueue(queueId, List.of(new EnqueueItem(Map.of("seq", 1)).withExpiryTtlSeconds(3600)));
        DequeueLockedItem locked = client.dequeueLocked(queueId).items().get(0);
        client.deadLetter(queueId, locked.lockId());

        DequeueLockedItem dead = client.dequeueLocked(queueId + "-deadletter").items().get(0);

        assertEquals(DEAD_LETTER_EXPIRY_TTL_SECONDS, dead.expiry().expiryTtlSeconds());
        assertEquals(3600, dead.expiry().originalExpiryTtlSeconds());
    }
}
