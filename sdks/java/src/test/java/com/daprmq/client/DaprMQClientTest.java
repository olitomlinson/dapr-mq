package com.daprmq.client;

import com.daprmq.client.errors.InvalidLeaseIdException;
import com.daprmq.client.errors.LockExpiredException;
import com.daprmq.client.errors.LockNotFoundException;
import com.daprmq.client.errors.SessionActorUnavailableException;
import com.daprmq.client.errors.SessionLeaseExpiredException;
import com.daprmq.client.errors.SessionLockedException;
import com.daprmq.client.errors.SessionNotFoundException;
import com.daprmq.client.errors.ValidationException;
import com.daprmq.client.types.DequeueLockedResult;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.EnqueueResult;
import com.daprmq.client.types.SessionLease;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

import java.net.http.HttpClient;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

class DaprMQClientTest {
    private FakeHttpServer server;
    private DaprMQClient client;

    @BeforeEach
    void setUp() {
        server = new FakeHttpServer();
        client = new DaprMQClient(server.baseUrl(), HttpClient.newHttpClient(), null);
    }

    @AfterEach
    void tearDown() {
        server.close();
    }

    @Test
    void enqueueMapsASuccessfulResponse() {
        server.respondWith(200, "{\"success\":true,\"message\":\"Enqueued 1 items\",\"itemsEnqueued\":1,\"itemsDeduplicated\":0}");

        EnqueueResult result = client.enqueue("my-queue", List.of(new EnqueueItem(Map.of("task", "send_email"), 0, null, "order-42")));

        assertTrue(result.success());
        assertEquals(1, result.itemsEnqueued());
        assertEquals("POST", server.lastRequest().method());
        assertEquals("/queue/my-queue/enqueue", server.lastRequest().path());
        assertTrue(server.lastRequest().body().contains("\"sessionId\":\"order-42\""));
        assertTrue(server.lastRequest().body().contains("\"priority\":0"));
    }

    @Test
    void enqueueThrowsValidationExceptionOn400() {
        server.respondWith(400, "{\"message\":\"bad item\",\"success\":false}");

        assertThrows(ValidationException.class, () -> client.enqueue("q", List.of(new EnqueueItem(Map.of()))));
    }

    @Test
    void dequeueLockedSendsHeadersAndMapsItems() {
        server.respondWith(200, "{\"items\":[{\"item\":{\"task\":\"x\"},\"priority\":0,\"lockId\":\"L1\",\"lockExpiresAt\":123.0}],\"locked\":false}");

        DequeueLockedResult result = client.dequeueLocked("q", 5, 60, "lease-1");

        assertEquals(1, result.items().size());
        assertEquals("L1", result.items().get(0).lockId());
        assertEquals("true", server.lastRequest().headers().get("require-ack"));
        assertEquals("5", server.lastRequest().headers().get("count"));
        assertEquals("60", server.lastRequest().headers().get("ttl-seconds"));
        assertEquals("lease-1", server.lastRequest().headers().get("lease-id"));
    }

    @Test
    void dequeueLockedReturnsNullOn204() {
        server.respondWith(204, null);

        assertNull(client.dequeueLocked("q"));
    }

    @Test
    void dequeueLockedReturnsLockedResultOn423() {
        server.respondWith(423, "{\"message\":\"locked\",\"lockExpiresAt\":999.0}");

        DequeueLockedResult result = client.dequeueLocked("q");

        assertTrue(result.locked());
        assertTrue(result.items().isEmpty());
    }

    @Test
    void dequeueLockedThrowsSessionLeaseExpiredOn410() {
        server.respondWith(410, "{\"message\":\"lease expired\"}");

        assertThrows(SessionLeaseExpiredException.class, () -> client.dequeueLocked("q"));
    }

    @Test
    void dequeueLockedThrowsValidationOn400() {
        server.respondWith(400, "{\"message\":\"bad count\"}");

        assertThrows(ValidationException.class, () -> client.dequeueLocked("q"));
    }

    @Test
    void acknowledgeMapsLockNotFound() {
        server.respondWith(409, "{\"success\":false,\"message\":\"no such lock\",\"errorCode\":\"LOCK_NOT_FOUND\"}");

        assertThrows(LockNotFoundException.class, () -> client.acknowledge("q", "L1"));
    }

    @Test
    void extendLockMapsLockExpiredOn410() {
        server.respondWith(410, "{\"success\":false,\"errorMessage\":\"expired\"}");

        assertThrows(LockExpiredException.class, () -> client.extendLock("q", "L1", 30));
    }

    @Test
    void extendLockMapsLockNotFoundOn404() {
        server.respondWith(404, "{\"message\":\"not found\"}");

        assertThrows(LockNotFoundException.class, () -> client.extendLock("q", "L1", 30));
    }

    @Test
    void deadLetterMapsSessionLeaseExpired() {
        server.respondWith(410, "{\"success\":false,\"message\":\"expired\",\"errorCode\":\"SESSION_LEASE_EXPIRED\"}");

        assertThrows(SessionLeaseExpiredException.class, () -> client.deadLetter("q", "L1"));
    }

    @Test
    void acceptSessionReturnsNullOn204() {
        server.respondWith(204, null);

        assertNull(client.acceptSession("q"));
    }

    @Test
    void acceptSessionParsesLeaseOnSuccess() {
        server.respondWith(200, "{\"sessionId\":\"s1\",\"leaseId\":\"lease-1\",\"leaseExpiresAt\":100.5}");

        SessionLease lease = client.acceptSession("q");

        assertEquals("s1", lease.sessionId());
        assertEquals("lease-1", lease.leaseId());
    }

    @Test
    void acceptSessionMapsSessionLockedOn423() {
        server.respondWith(423, "{\"message\":\"locked\"}");

        assertThrows(SessionLockedException.class, () -> client.acceptSession("q"));
    }

    @Test
    void acceptSessionMapsSessionNotFoundOn404() {
        server.respondWith(404, "{\"message\":\"not found\"}");

        assertThrows(SessionNotFoundException.class, () -> client.acceptSession("q"));
    }

    @Test
    void acceptSessionMapsActorUnavailableOn502() {
        server.respondWith(502, "{\"message\":\"unreachable\"}");

        assertThrows(SessionActorUnavailableException.class, () -> client.acceptSession("q"));
    }

    @Test
    void renewSessionLeaseMapsInvalidLeaseId() {
        server.respondWith(400, "{\"message\":\"bad lease\"}");

        assertThrows(InvalidLeaseIdException.class, () -> client.renewSessionLease("q", "s1", "bad-lease"));
    }

    @Test
    void releaseSessionThrowsOnFailure() {
        server.respondWith(400, "{\"message\":\"bad lease\"}");

        assertThrows(InvalidLeaseIdException.class, () -> client.releaseSession("q", "s1", "bad-lease"));
    }

    @Test
    void releaseSessionSucceedsSilently() {
        server.respondWith(200, "{\"success\":true}");

        client.releaseSession("q", "s1", "lease-1");

        assertFalse(server.lastRequest().body().isEmpty());
    }
}
