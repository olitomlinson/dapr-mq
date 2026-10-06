package com.daprmq.client;

import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.DaprMQUnavailableException;
import com.daprmq.client.errors.DeliveryUnknownException;
import com.daprmq.client.types.EnqueueItem;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.junit.jupiter.api.Test;

import java.net.ServerSocket;
import java.net.http.HttpClient;
import java.time.Duration;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.CancellationException;
import java.util.concurrent.atomic.AtomicReference;

import static com.daprmq.client.ScriptedHttpServer.NOT_DELIVERED;
import static com.daprmq.client.ScriptedHttpServer.UNKNOWN;
import static com.daprmq.client.ScriptedHttpServer.ok;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertInstanceOf;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * The shared retry contract (sdks/testing/RETRIES_AND_READINESS.md): retry what certainly wasn't
 * delivered, retry an unknown outcome only for a fully keyed enqueue, within the retry timeout.
 */
class DaprMQClientRetryTest {
    private static final String OK = "{\"success\":true,\"message\":\"ok\",\"itemsEnqueued\":1,\"itemsDeduplicated\":0}";
    private static final RetryOptions FAST = RetryOptions.defaults()
            .withTimeout(Duration.ofSeconds(5))
            .withMinAttemptWindow(Duration.ofMillis(10))
            .withBackoff(Duration.ofMillis(1), Duration.ofMillis(5));

    private static DaprMQClient client(String baseUrl, RetryOptions retry) {
        return new DaprMQClient(baseUrl, HttpClient.newHttpClient(), null, null, retry);
    }

    @Test
    void notDeliveredIsRetriedUntilItSucceedsForAnyOperation() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(NOT_DELIVERED, NOT_DELIVERED, ok("{}"))) {
            client(server.baseUrl(), FAST).acknowledge("q", "L1");

            assertEquals(3, server.requests().size());
        }
    }

    @Test
    void everyAttemptSendsTheRemainingTimeAsTheDeadline() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(ok(OK))) {
            client(server.baseUrl(), FAST).enqueue("q", List.of(new EnqueueItem("x")));

            long ms = Long.parseLong(server.requests().get(0).headers().get("daprmq-timeout"));
            assertTrue(ms >= 4_000 && ms <= 5_000, "daprmq-timeout=" + ms);
        }
    }

    @Test
    void notDeliveredUntilTimeRunsOutThrowsUnavailable() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(NOT_DELIVERED)) {
            DaprMQUnavailableException e = assertThrows(DaprMQUnavailableException.class,
                    () -> client(server.baseUrl(), FAST.withTimeout(Duration.ofMillis(200))).acknowledge("q", "L1"));

            assertTrue(server.requests().size() > 1);
            assertEquals("UNAVAILABLE", e.getErrorCode());
            assertEquals("acknowledge", e.getOperation());
            assertEquals("q", e.getQueueId());
        }
    }

    @Test
    void noAttemptStartsWithLessThanTheMinimumWindowLeft() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(NOT_DELIVERED)) {
            RetryOptions retry = FAST.withTimeout(Duration.ofSeconds(1)).withMinAttemptWindow(Duration.ofSeconds(5));

            assertThrows(DaprMQUnavailableException.class, () -> client(server.baseUrl(), retry).acknowledge("q", "L1"));

            assertEquals(1, server.requests().size());
        }
    }

    @Test
    void refusedConnectionIsNotDeliveredAndEndsInUnavailable() throws Exception {
        int port;
        try (ServerSocket socket = new ServerSocket(0)) {
            port = socket.getLocalPort(); // closed again: nothing listens, connections are refused
        }

        DaprMQUnavailableException e = assertThrows(DaprMQUnavailableException.class,
                () -> client("http://localhost:" + port, FAST.withTimeout(Duration.ofMillis(300))).enqueue("q", List.of(new EnqueueItem("x"))));

        assertEquals("enqueue", e.getOperation());
    }

    @Test
    void unknownIsNotRetriedForADequeue() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(UNKNOWN)) {
            DeliveryUnknownException e = assertThrows(DeliveryUnknownException.class,
                    () -> client(server.baseUrl(), FAST).dequeueLocked("q"));

            assertEquals(1, server.requests().size());
            assertEquals("DELIVERY_UNKNOWN", e.getErrorCode());
            assertEquals("dequeueLocked", e.getOperation());
        }
    }

    @Test
    void unknownIsNotRetriedForAnEnqueueWithUnkeyedItemsAndReportsTheKeys() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(UNKNOWN)) {
            DeliveryUnknownException e = assertThrows(DeliveryUnknownException.class,
                    () -> client(server.baseUrl(), FAST).enqueue("q", List.of(new EnqueueItem("a", 1, "k1", null), new EnqueueItem("b"))));

            assertEquals(1, server.requests().size());
            assertEquals(Arrays.asList("k1", null), e.getIdempotencyKeys());
        }
    }

    @Test
    void unknownIsRetriedForAFullyKeyedEnqueue() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(UNKNOWN, ok(OK))) {
            client(server.baseUrl(), FAST).enqueue("q", List.of(new EnqueueItem("a", 1, "k1", null)));

            assertEquals(2, server.requests().size());
        }
    }

    @Test
    void autoIdempotencyKeysFillMissingKeysKeepGivenOnesAndMakeUnknownRetryable() throws Exception {
        try (ScriptedHttpServer server = new ScriptedHttpServer(UNKNOWN, ok(OK))) {
            client(server.baseUrl(), FAST.withAutoIdempotencyKeys(true))
                    .enqueue("q", List.of(new EnqueueItem("a", 1, "mine", null), new EnqueueItem("b")));

            assertEquals(2, server.requests().size());
            assertEquals(server.requests().get(0).body(), server.requests().get(1).body()); // same generated key
            JsonNode items = new ObjectMapper().readTree(server.requests().get(0).body()).get("items");
            assertEquals("mine", items.get(0).get("idempotencyKey").asText());
            assertTrue(items.get(1).get("idempotencyKey").asText().matches("[0-9a-f]{32}"));
        }
    }

    @Test
    void a503WithoutTheMarkerIsNotADeliveryFailure() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(
                new ScriptedHttpServer.Answer(503, "{\"message\":\"proxy says no\"}", java.util.Map.of()))) {
            DaprMQException e = assertThrows(DaprMQException.class, () -> client(server.baseUrl(), FAST).acknowledge("q", "L1"));

            assertFalse(e instanceof DaprMQUnavailableException);
            assertEquals(1, server.requests().size());
        }
    }

    @Test
    void retriesOffSendNoDeadlineAndMakeOneAttempt() {
        try (ScriptedHttpServer server = new ScriptedHttpServer(NOT_DELIVERED)) {
            assertThrows(DaprMQUnavailableException.class,
                    () -> client(server.baseUrl(), RetryOptions.defaults().withTimeout(Duration.ZERO)).acknowledge("q", "L1"));

            assertEquals(1, server.requests().size());
            assertFalse(server.requests().get(0).headers().containsKey("daprmq-timeout"));
        }
    }

    @Test
    void interruptingTheCallerStopsRetryingAsCancellation() throws Exception {
        try (ScriptedHttpServer server = new ScriptedHttpServer(NOT_DELIVERED)) {
            DaprMQClient client = client(server.baseUrl(), FAST.withTimeout(Duration.ofSeconds(30)).withBackoff(Duration.ofMillis(20), Duration.ofMillis(20)));
            AtomicReference<Throwable> thrown = new AtomicReference<>();
            Thread caller = new Thread(() -> {
                try {
                    client.acknowledge("q", "L1");
                } catch (Throwable t) {
                    thrown.set(t);
                }
            });

            caller.start();
            Thread.sleep(100);
            caller.interrupt();
            caller.join(5_000);

            assertFalse(caller.isAlive());
            assertInstanceOf(CancellationException.class, thrown.get());
            assertNotEquals(0, server.requests().size());
        }
    }
}
