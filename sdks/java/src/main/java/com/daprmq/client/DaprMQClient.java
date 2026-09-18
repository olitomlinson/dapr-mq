package com.daprmq.client;

import com.daprmq.client.errors.ActorNotFoundException;
import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.InvalidLeaseIdException;
import com.daprmq.client.errors.LockExpiredException;
import com.daprmq.client.errors.LockNotFoundException;
import com.daprmq.client.errors.SessionActorUnavailableException;
import com.daprmq.client.errors.SessionLeaseExpiredException;
import com.daprmq.client.errors.SessionLockedException;
import com.daprmq.client.errors.SessionNotFoundException;
import com.daprmq.client.errors.ValidationException;
import com.daprmq.client.internal.Json;
import com.daprmq.client.types.DequeueLockedItem;
import com.daprmq.client.types.DequeueLockedResult;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.EnqueueResult;
import com.daprmq.client.types.SessionLease;
import com.daprmq.grpc.DaprMQGrpc;
import com.fasterxml.jackson.databind.JsonNode;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;

import java.io.IOException;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

/**
 * REST-backed for every operation except {@link #consumeSession}, which is the one method built
 * on the {@code ConsumeSession} gRPC streaming RPC - everything else (enqueue, dequeueLocked,
 * acknowledge, extendLock, deadLetter, acceptSession, renewSessionLease, releaseSession) is a
 * plain HTTP call under the hood.
 */
public final class DaprMQClient implements AutoCloseable, SessionCapableClient {
    private final String httpBaseUrl;
    private final HttpClient httpClient;
    private final DaprMQGrpc.DaprMQStub asyncStub;
    private final ManagedChannel ownedChannel;

    /**
     * DI-friendly constructor - the caller already owns {@code grpcChannel}'s lifecycle; it is not
     * shut down by {@link #close()}.
     */
    public DaprMQClient(String httpBaseUrl, ManagedChannel grpcChannel) {
        this(httpBaseUrl, HttpClient.newHttpClient(), DaprMQGrpc.newStub(grpcChannel), null);
    }

    /** Test seam - lets tests substitute a mock/in-process stub directly. */
    DaprMQClient(String httpBaseUrl, HttpClient httpClient, DaprMQGrpc.DaprMQStub asyncStub) {
        this(httpBaseUrl, httpClient, asyncStub, null);
    }

    private DaprMQClient(String httpBaseUrl, HttpClient httpClient, DaprMQGrpc.DaprMQStub asyncStub, ManagedChannel ownedChannel) {
        this.httpBaseUrl = httpBaseUrl.replaceAll("/+$", "");
        this.httpClient = httpClient;
        this.asyncStub = asyncStub;
        this.ownedChannel = ownedChannel;
    }

    /** Convenience factory - builds and owns a plaintext gRPC channel, closed by {@link #close()}. */
    public static DaprMQClient create(String httpBaseUrl, String grpcTarget) {
        ManagedChannel channel = ManagedChannelBuilder.forTarget(grpcTarget).usePlaintext().build();
        return new DaprMQClient(httpBaseUrl, HttpClient.newHttpClient(), DaprMQGrpc.newStub(channel), channel);
    }

    public EnqueueResult enqueue(String queueId, List<EnqueueItem> items) {
        List<Map<String, Object>> wireItems = new ArrayList<>();
        for (EnqueueItem i : items) {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("item", i.item());
            m.put("priority", i.priority());
            m.put("idempotencyKey", i.idempotencyKey());
            m.put("sessionId", i.sessionId());
            wireItems.add(m);
        }
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("items", wireItems);

        HttpResponse<String> response = postJson(path(queueId, "enqueue"), body, null);
        if (!isSuccess(response.statusCode())) {
            throw mapGenericError(response.statusCode(), response.body());
        }

        JsonNode result = Json.requireParsed(response.body(), response.uri().toString());
        return new EnqueueResult(
                result.path("success").asBoolean(),
                result.path("message").asText(),
                result.path("itemsEnqueued").asInt(),
                result.path("itemsDeduplicated").asInt(0));
    }

    public DequeueLockedResult dequeueLocked(String queueId) {
        return dequeueLocked(queueId, 1, 30, null);
    }

    public DequeueLockedResult dequeueLocked(String queueId, int count, int ttlSeconds, String leaseId) {
        Map<String, String> headers = new LinkedHashMap<>();
        headers.put("require-ack", "true");
        headers.put("count", String.valueOf(count));
        headers.put("ttl-seconds", String.valueOf(ttlSeconds));
        if (leaseId != null) {
            headers.put("lease-id", leaseId);
        }

        HttpResponse<String> response = postNoBody(path(queueId, "dequeue"), headers);

        if (response.statusCode() == 204) {
            return null;
        }

        if (response.statusCode() == 423) {
            JsonNode locked = Json.tryParse(response.body());
            String message = locked == null ? null : Json.textOrNull(locked, "message");
            return new DequeueLockedResult(List.of(), true, message);
        }

        if (!isSuccess(response.statusCode())) {
            String message = Json.errorMessageFrom(response.body(), response.statusCode());
            // Dequeue's guard rejection carries no wire error code - 410 here is unambiguously the
            // session-lease guard (plain lock expiry doesn't apply to Dequeue), 400 covers both a
            // bad/missing lease-id and ordinary request validation (e.g. bad count).
            throw response.statusCode() == 410 ? new SessionLeaseExpiredException(message) : new ValidationException(message);
        }

        JsonNode result = Json.requireParsed(response.body(), response.uri().toString());
        List<DequeueLockedItem> items = new ArrayList<>();
        for (JsonNode i : result.path("items")) {
            items.add(new DequeueLockedItem(i.get("item"), i.path("priority").asInt(), i.path("lockId").asText(), i.path("lockExpiresAt").asDouble()));
        }
        return new DequeueLockedResult(items, result.path("locked").asBoolean(), Json.textOrNull(result, "message"));
    }

    public void acknowledge(String queueId, String lockId) {
        acknowledge(queueId, lockId, null);
    }

    public void acknowledge(String queueId, String lockId, String leaseId) {
        Map<String, Object> body = Map.of("lockId", lockId);
        HttpResponse<String> response = postJson(path(queueId, "acknowledge"), body, leaseId);
        if (isSuccess(response.statusCode())) {
            return;
        }

        JsonNode parsed = Json.tryParse(response.body());
        String errorCode = parsed == null ? null : Json.textOrNull(parsed, "errorCode");
        String message = (parsed != null && parsed.hasNonNull("message"))
                ? parsed.get("message").asText()
                : Json.errorMessageFrom(response.body(), response.statusCode());
        throw mapLockError(errorCode, message);
    }

    public void extendLock(String queueId, String lockId, int additionalTtlSeconds) {
        extendLock(queueId, lockId, additionalTtlSeconds, null);
    }

    public void extendLock(String queueId, String lockId, int additionalTtlSeconds, String leaseId) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("lockId", lockId);
        body.put("additionalTtlSeconds", additionalTtlSeconds);

        HttpResponse<String> response = postJson(path(queueId, "extend-lock"), body, leaseId);
        if (isSuccess(response.statusCode())) {
            return;
        }

        String message = Json.errorMessageFrom(response.body(), response.statusCode());
        // ExtendLock's error body carries no wire error code (unlike Acknowledge/DeadLetter), so
        // this is a best-effort status-based mapping only.
        throw switch (response.statusCode()) {
            case 410 -> new LockExpiredException(message);
            case 404 -> new LockNotFoundException(message);
            default -> new ValidationException(message);
        };
    }

    public void deadLetter(String queueId, String lockId) {
        deadLetter(queueId, lockId, null);
    }

    public void deadLetter(String queueId, String lockId, String leaseId) {
        Map<String, Object> body = Map.of("lockId", lockId);
        HttpResponse<String> response = postJson(path(queueId, "deadletter"), body, leaseId);
        if (isSuccess(response.statusCode())) {
            return;
        }

        JsonNode parsed = Json.tryParse(response.body());
        String errorCode = parsed == null ? null : Json.textOrNull(parsed, "errorCode");
        String message = (parsed != null && parsed.hasNonNull("message"))
                ? parsed.get("message").asText()
                : Json.errorMessageFrom(response.body(), response.statusCode());
        throw mapLockError(errorCode, message);
    }

    public SessionLease acceptSession(String queueId) {
        return acceptSession(queueId, null, 30);
    }

    public SessionLease acceptSession(String queueId, String sessionId, int leaseSeconds) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("sessionId", sessionId);
        body.put("leaseSeconds", leaseSeconds);

        HttpResponse<String> response = postJson(path(queueId, "sessions/accept"), body, null);

        if (response.statusCode() == 204) {
            return null;
        }

        if (!isSuccess(response.statusCode())) {
            String message = Json.errorMessageFrom(response.body(), response.statusCode());
            throw switch (response.statusCode()) {
                case 404 -> new SessionNotFoundException(message);
                case 423 -> new SessionLockedException(message);
                case 502 -> new SessionActorUnavailableException(message);
                default -> new ValidationException(message);
            };
        }

        JsonNode result = Json.requireParsed(response.body(), response.uri().toString());
        return new SessionLease(result.path("sessionId").asText(), result.path("leaseId").asText(), result.path("leaseExpiresAt").asDouble());
    }

    public SessionLease renewSessionLease(String queueId, String sessionId, String leaseId) {
        return renewSessionLease(queueId, sessionId, leaseId, 30);
    }

    public SessionLease renewSessionLease(String queueId, String sessionId, String leaseId, int additionalSeconds) {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("leaseId", leaseId);
        body.put("additionalSeconds", additionalSeconds);

        HttpResponse<String> response = postJson(path(queueId, "sessions/" + encode(sessionId) + "/renew"), body, null);

        if (!isSuccess(response.statusCode())) {
            String message = Json.errorMessageFrom(response.body(), response.statusCode());
            throw response.statusCode() == 410 ? new SessionLeaseExpiredException(message) : new InvalidLeaseIdException(message);
        }

        JsonNode result = Json.requireParsed(response.body(), response.uri().toString());
        return new SessionLease(sessionId, leaseId, result.path("newExpiresAt").asDouble());
    }

    public void releaseSession(String queueId, String sessionId, String leaseId) {
        Map<String, Object> body = Map.of("leaseId", leaseId);
        HttpResponse<String> response = postJson(path(queueId, "sessions/" + encode(sessionId) + "/release"), body, null);
        if (!isSuccess(response.statusCode())) {
            throw new InvalidLeaseIdException(Json.errorMessageFrom(response.body(), response.statusCode()));
        }
    }

    public SessionStream consumeSession(String queueId) {
        return consumeSession(queueId, ConsumeSessionOptions.defaults());
    }

    /**
     * Managed consume loop for exactly one session: claims a session (any-available or targeted),
     * streams delivered items back, and lets the caller ack/deadLetter each one via the returned
     * stream's deliveries. See {@link SessionStream} and {@link SessionQueueConsumer} (which runs a
     * pool of these).
     */
    @Override
    public SessionStream consumeSession(String queueId, ConsumeSessionOptions options) {
        return new SessionStream(asyncStub::consumeSession, queueId, options);
    }

    @Override
    public void close() {
        if (ownedChannel != null) {
            ownedChannel.shutdown();
            try {
                ownedChannel.awaitTermination(5, TimeUnit.SECONDS);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }
    }

    private String path(String queueId, String suffix) {
        return httpBaseUrl + "/queue/" + encode(queueId) + "/" + suffix;
    }

    private static String encode(String s) {
        return URLEncoder.encode(s, StandardCharsets.UTF_8).replace("+", "%20");
    }

    private static boolean isSuccess(int status) {
        return status >= 200 && status < 300;
    }

    private HttpResponse<String> postJson(String path, Object body, String leaseId) {
        HttpRequest.Builder builder = HttpRequest.newBuilder()
                .uri(URI.create(path))
                .header("content-type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(Json.write(body)));
        if (leaseId != null) {
            builder.header("lease-id", leaseId);
        }
        return send(builder);
    }

    private HttpResponse<String> postNoBody(String path, Map<String, String> headers) {
        HttpRequest.Builder builder = HttpRequest.newBuilder().uri(URI.create(path)).POST(HttpRequest.BodyPublishers.noBody());
        headers.forEach(builder::header);
        return send(builder);
    }

    private HttpResponse<String> send(HttpRequest.Builder builder) {
        try {
            return httpClient.send(builder.build(), HttpResponse.BodyHandlers.ofString());
        } catch (IOException e) {
            throw new DaprMQException("HTTP request failed: " + e.getMessage());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new DaprMQException("HTTP request interrupted");
        }
    }

    private static DaprMQException mapGenericError(int status, String body) {
        String message = Json.errorMessageFrom(body, status);
        return switch (status) {
            case 400 -> new ValidationException(message);
            case 404 -> new ActorNotFoundException(message);
            default -> new DaprMQException(message);
        };
    }

    private static DaprMQException mapLockError(String errorCode, String message) {
        if (errorCode == null) {
            return new DaprMQException(message);
        }
        return switch (errorCode) {
            case "LOCK_NOT_FOUND" -> new LockNotFoundException(message);
            case "LOCK_EXPIRED" -> new LockExpiredException(message);
            case "SESSION_LEASE_EXPIRED" -> new SessionLeaseExpiredException(message);
            case "INVALID_LEASE_ID" -> new InvalidLeaseIdException(message);
            case "INVALID_LOCK_ID", "INVALID_TTL" -> new ValidationException(message);
            default -> new DaprMQException(message, errorCode);
        };
    }
}
