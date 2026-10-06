package com.daprmq.client.integration;

import com.daprmq.client.DaprMQClient;
import com.daprmq.client.RetryOptions;
import com.daprmq.client.errors.DaprMQUnavailableException;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.EnqueueResult;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.testcontainers.DockerClientFactory;

import java.net.ServerSocket;
import java.time.Duration;
import java.util.List;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

/**
 * R-01, R-04 and R-05 from sdks/testing/RETRIES_AND_READINESS.md against a real server.
 * (R-02/R-03 need a split gateway/worker stack, which only the .NET fixture builds today.)
 */
class RetriesAndReadinessIT {
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

    private static String newQueueId() {
        return "java-it-" + UUID.randomUUID().toString().replace("-", "");
    }

    @Test
    void r01WaitForReadyReturnsAndAnEnqueueThenSucceeds() {
        try (DaprMQClient client = DaprMQClient.create(server.httpUrl(), server.grpcAddress())) {
            assertTrue(client.waitForReady(Duration.ofSeconds(30)));

            assertEquals(1, client.enqueue(newQueueId(), List.of(new EnqueueItem(java.util.Map.of("seq", 1)))).itemsEnqueued());
        }
    }

    @Test
    void r04ServerUnreachableThrowsUnavailableNotAHang() throws Exception {
        int port;
        try (ServerSocket socket = new ServerSocket(0)) {
            port = socket.getLocalPort(); // closed again: nothing listens, connections are refused
        }

        try (DaprMQClient client = DaprMQClient.create("http://127.0.0.1:" + port, "127.0.0.1:" + port,
                RetryOptions.defaults().withTimeout(Duration.ofSeconds(2)))) {
            long started = System.nanoTime();
            DaprMQUnavailableException e = assertThrows(DaprMQUnavailableException.class,
                    () -> client.enqueue(newQueueId(), List.of(new EnqueueItem(java.util.Map.of("seq", 1)))));

            assertEquals("enqueue", e.getOperation());
            assertTrue(Duration.ofNanos(System.nanoTime() - started).toSeconds() < 10);
        }
    }

    @Test
    void r05AutoIdempotencyKeysFillMissingKeysAndKeepGivenOnes() {
        try (DaprMQClient client = DaprMQClient.create(server.httpUrl(), server.grpcAddress(),
                RetryOptions.defaults().withAutoIdempotencyKeys(true))) {
            String queueId = newQueueId();
            List<EnqueueItem> items = List.of(
                    new EnqueueItem(java.util.Map.of("seq", 1), 1, "mine-" + UUID.randomUUID(), null),
                    new EnqueueItem(java.util.Map.of("seq", 2)));

            client.enqueue(queueId, items);
            EnqueueResult second = client.enqueue(queueId, items);

            // The caller's key is kept (the repeat is de-duplicated); the unkeyed item gets a fresh key per call.
            assertEquals(1, second.itemsDeduplicated());
            assertEquals(1, second.itemsEnqueued());
        }
    }
}
