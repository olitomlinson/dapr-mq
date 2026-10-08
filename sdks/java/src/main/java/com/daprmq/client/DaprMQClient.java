package com.daprmq.client;

import com.daprmq.client.errors.ActorNotFoundException;
import com.daprmq.client.errors.DaprMQException;
import com.daprmq.client.errors.DaprMQUnavailableException;
import com.daprmq.client.errors.DeliveryUnknownException;
import com.daprmq.client.errors.InvalidLeaseIdException;
import com.daprmq.client.errors.LockExpiredException;
import com.daprmq.client.errors.LockNotFoundException;
import com.daprmq.client.errors.SessionActorUnavailableException;
import com.daprmq.client.errors.SessionLeaseExpiredException;
import com.daprmq.client.errors.SessionLockedException;
import com.daprmq.client.errors.SessionNotFoundException;
import com.daprmq.client.errors.ValidationException;
import com.daprmq.client.internal.Json;
import com.daprmq.client.types.AcknowledgeBatchResult;
import com.daprmq.client.types.DequeueLockedItem;
import com.daprmq.client.types.DequeueLockedResult;
import com.daprmq.client.types.EnqueueItem;
import com.daprmq.client.types.EnqueueResult;
import com.daprmq.client.types.LockAcknowledgeResult;
import com.daprmq.client.types.NackResult;
import com.daprmq.client.types.SessionLease;
import com.daprmq.grpc.DaprMQGrpc;
import com.fasterxml.jackson.databind.JsonNode;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;
import io.grpc.Status;
import io.grpc.StatusRuntimeException;
import io.grpc.health.v1.HealthCheckRequest;
import io.grpc.health.v1.HealthCheckResponse;
import io.grpc.health.v1.HealthGrpc;

import java.io.IOException;
import java.net.ConnectException;
import java.net.URI;
import java.net.http.HttpConnectTimeoutException;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ThreadLocalRandom;
import java.util.concurrent.TimeUnit;

/**
 * REST-backed for every operation except {@link #consumeSession}, which is the one method built
 * on the {@code ConsumeSession} gRPC streaming RPC - everything else (enqueue, dequeueLocked,
 * acknowledge, extendLock, deadLetter, nack, acceptSession, renewSessionLease, releaseSession) is a
 * plain HTTP call under the hood.
 */
public final class DaprMQClient implements AutoCloseable, SessionCapableClient {
    /** The health service {@link #waitForReady()} watches by default: queue operations can be served. */
    public static final String OPERATIONS_HEALTH_SERVICE = "daprmq.DaprMQ.operations";

    private static final String DELIVERY_MARKER = "daprmq-delivery";
    private static final String RETRY_TIMEOUT_HEADER = "daprmq-retry-timeout";
    /** Each attempt's own limit: matches the server's per-call safety limit (DELIVERY_ATTEMPT_MAX_SECONDS). */
    private static final Duration ATTEMPT_TIMEOUT = Duration.ofSeconds(100);

    private final String httpBaseUrl;
    private final HttpClient httpClient;
    private final DaprMQGrpc.DaprMQStub asyncStub;
    private final HealthGrpc.HealthBlockingStub healthStub;
    private final RetryOptions retry;
    private final ManagedChannel ownedChannel;

    /**
     * DI-friendly constructor - the caller already owns {@code grpcChannel}'s lifecycle; it is not
     * shut down by {@link #close()}.
     */
    public DaprMQClient(String httpBaseUrl, ManagedChannel grpcChannel) {
        this(httpBaseUrl, grpcChannel, RetryOptions.defaults());
    }

    /** As {@link #DaprMQClient(String, ManagedChannel)}, with retry behaviour. */
    public DaprMQClient(String httpBaseUrl, ManagedChannel grpcChannel, RetryOptions retry) {
        this(httpBaseUrl, HttpClient.newHttpClient(), DaprMQGrpc.newStub(grpcChannel), HealthGrpc.newBlockingStub(grpcChannel), retry, null);
    }

    /** Test seam - lets tests substitute a mock/in-process stub directly. */
    DaprMQClient(String httpBaseUrl, HttpClient httpClient, DaprMQGrpc.DaprMQStub asyncStub) {
        this(httpBaseUrl, httpClient, asyncStub, null, RetryOptions.defaults(), null);
    }

    /** Test seam - also substitutes the health stub and retry behaviour. */
    DaprMQClient(String httpBaseUrl, HttpClient httpClient, DaprMQGrpc.DaprMQStub asyncStub, HealthGrpc.HealthBlockingStub healthStub, RetryOptions retry) {
        this(httpBaseUrl, httpClient, asyncStub, healthStub, retry, null);
    }

    private DaprMQClient(String httpBaseUrl, HttpClient httpClient, DaprMQGrpc.DaprMQStub asyncStub,
                         HealthGrpc.HealthBlockingStub healthStub, RetryOptions retry, ManagedChannel ownedChannel) {
        this.httpBaseUrl = httpBaseUrl.replaceAll("/+$", "");
        this.httpClient = httpClient;
        this.asyncStub = asyncStub;
        this.healthStub = healthStub;
        this.retry = retry;
        this.ownedChannel = ownedChannel;
    }

    /** Convenience factory - builds and owns a plaintext gRPC channel, closed by {@link #close()}. */
    public static DaprMQClient create(String httpBaseUrl, String grpcTarget) {
        return create(httpBaseUrl, grpcTarget, RetryOptions.defaults());
    }

    /** As {@link #create(String, String)}, with retry behaviour. */
    public static DaprMQClient create(String httpBaseUrl, String grpcTarget, RetryOptions retry) {
        ManagedChannel channel = ManagedChannelBuilder.forTarget(grpcTarget).usePlaintext().build();
        return new DaprMQClient(httpBaseUrl, HttpClient.newHttpClient(), DaprMQGrpc.newStub(channel), HealthGrpc.newBlockingStub(channel), retry, channel);
    }

    public EnqueueResult enqueue(String queueId, List<EnqueueItem> items) {
        // Keys are fixed before the first attempt, so a retry re-sends the same ones.
        List<String> keys = new ArrayList<>();
        List<Map<String, Object>> wireItems = new ArrayList<>();
        for (EnqueueItem i : items) {
            String key = i.idempotencyKey() != null ? i.idempotencyKey()
                    : retry.autoIdempotencyKeys() ? UUID.randomUUID().toString().replace("-", "") : null;
            keys.add(key);
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("item", i.item());
            m.put("priority", i.priority());
            m.put("idempotencyKey", key);
            m.put("sessionId", i.sessionId());
            wireItems.add(m);
        }
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("items", wireItems);

        // An unknown outcome is only safe to repeat when the server can de-duplicate every item.
        boolean allKeyed = keys.stream().allMatch(k -> k != null);
        HttpResponse<String> response = send("enqueue", queueId, jsonRequest(path(queueId, "enqueue"), body, null), allKeyed, keys);
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
        return dequeueLocked(queueId, count, ttlSeconds, leaseId, false);
    }

    /**
     * {@code allowCompetingConsumers} lets several consumers hold locks on the queue at once (the
     * server otherwise answers 423 Locked while any lock is outstanding) - needed when scaling a
     * consumer out to multiple replicas.
     */
    public DequeueLockedResult dequeueLocked(String queueId, int count, int ttlSeconds, String leaseId, boolean allowCompetingConsumers) {
        Map<String, String> headers = new LinkedHashMap<>();
        headers.put("require-ack", "true");
        headers.put("count", String.valueOf(count));
        headers.put("ttl-seconds", String.valueOf(ttlSeconds));
        if (leaseId != null) {
            headers.put("lease-id", leaseId);
        }
        if (allowCompetingConsumers) {
            headers.put("allow-competing-consumers", "true");
        }

        HttpRequest.Builder request = HttpRequest.newBuilder().uri(URI.create(path(queueId, "dequeue"))).POST(HttpRequest.BodyPublishers.noBody());
        headers.forEach(request::header);
        HttpResponse<String> response = send("dequeueLocked", queueId, request, false, null);

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
        HttpResponse<String> response = postJson("acknowledge", queueId, path(queueId, "acknowledge"), body, leaseId);
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

    public AcknowledgeBatchResult acknowledgeBatch(String queueId, List<String> lockIds) {
        return acknowledgeBatch(queueId, lockIds, null);
    }

    /**
     * Acknowledges up to 1,000 locks in one call, with an outcome per lock: one lock that expired or
     * was already settled does not fail the rest. Throws only for whole-call failures (bad lease,
     * invalid request). An unknown outcome is retried automatically, since re-sending is harmless;
     * after such a retry, {@code LOCK_NOT_FOUND} can mean "already settled".
     */
    public AcknowledgeBatchResult acknowledgeBatch(String queueId, List<String> lockIds, String leaseId) {
        Map<String, Object> body = Map.of("lockIds", lockIds);
        HttpResponse<String> response = send("acknowledgeBatch", queueId,
                jsonRequest(path(queueId, "acknowledge-batch"), body, leaseId), true, null);
        JsonNode parsed = Json.tryParse(response.body());
        if (isSuccess(response.statusCode())) {
            List<LockAcknowledgeResult> results = new ArrayList<>();
            if (parsed != null) {
                for (JsonNode r : parsed.path("results")) {
                    results.add(new LockAcknowledgeResult(r.path("lockId").asText(), r.path("outcome").asText()));
                }
            }
            return new AcknowledgeBatchResult(parsed == null ? 0 : parsed.path("itemsAcknowledged").asInt(0), results);
        }

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

        HttpResponse<String> response = postJson("extendLock", queueId, path(queueId, "extend-lock"), body, leaseId);
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
        HttpResponse<String> response = postJson("deadLetter", queueId, path(queueId, "deadletter"), body, leaseId);
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

    public NackResult nack(String queueId, String lockId) {
        return nack(queueId, lockId, null);
    }

    /**
     * Returns a locked item to its original position in the queue. Counts as a delivery attempt:
     * past the server's max delivery count the item is dead-lettered instead
     * ({@link NackResult#deadLettered()}).
     */
    public NackResult nack(String queueId, String lockId, String leaseId) {
        Map<String, Object> body = Map.of("lockId", lockId);
        HttpResponse<String> response = postJson("nack", queueId, path(queueId, "nack"), body, leaseId);
        JsonNode parsed = Json.tryParse(response.body());
        if (isSuccess(response.statusCode())) {
            return new NackResult(
                    parsed != null && parsed.path("deadLettered").asBoolean(false),
                    parsed == null ? 0 : parsed.path("deliveryCount").asInt(0),
                    parsed == null ? null : Json.textOrNull(parsed, "dlqId"));
        }

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

        HttpResponse<String> response = postJson("acceptSession", queueId, path(queueId, "sessions/accept"), body, null);

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

        HttpResponse<String> response = postJson("renewSessionLease", queueId, path(queueId, "sessions/" + encode(sessionId) + "/renew"), body, null);

        if (!isSuccess(response.statusCode())) {
            String message = Json.errorMessageFrom(response.body(), response.statusCode());
            throw response.statusCode() == 410 ? new SessionLeaseExpiredException(message) : new InvalidLeaseIdException(message);
        }

        JsonNode result = Json.requireParsed(response.body(), response.uri().toString());
        return new SessionLease(sessionId, leaseId, result.path("newExpiresAt").asDouble());
    }

    public void releaseSession(String queueId, String sessionId, String leaseId) {
        Map<String, Object> body = Map.of("leaseId", leaseId);
        HttpResponse<String> response = postJson("releaseSession", queueId, path(queueId, "sessions/" + encode(sessionId) + "/release"), body, null);
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

    /**
     * Blocks until queue operations can be served (gRPC health service
     * {@value #OPERATIONS_HEALTH_SERVICE}), or the thread is interrupted
     * ({@link CancellationException}).
     */
    public void waitForReady() {
        waitForReady(OPERATIONS_HEALTH_SERVICE, null);
    }

    /** As {@link #waitForReady()}, giving up after {@code timeout}: false if it ran out first. */
    public boolean waitForReady(Duration timeout) {
        return waitForReady(OPERATIONS_HEALTH_SERVICE, timeout);
    }

    /**
     * Waits until the server reports SERVING for {@code service} over the standard gRPC health
     * protocol (grpc.health.v1.Health/Watch); "daprmq.DaprMQ" means just this server instance is
     * ready. Reconnects while the server isn't listening.
     *
     * @param timeout how long to wait, or null for no limit
     * @return true once SERVING; false if {@code timeout} ran out first
     * @throws UnsupportedOperationException if the server doesn't expose the health service
     * @throws CancellationException if the thread is interrupted
     */
    public boolean waitForReady(String service, Duration timeout) {
        if (healthStub == null) {
            throw new IllegalStateException("This DaprMQClient was created without a gRPC channel or health stub.");
        }
        long deadline = timeout == null ? Long.MAX_VALUE : System.nanoTime() + timeout.toNanos();
        long backoffMillis = 250;

        while (true) {
            long remainingNanos = deadline - System.nanoTime();
            if (remainingNanos <= 0) {
                return false;
            }
            HealthGrpc.HealthBlockingStub stub = timeout == null ? healthStub : healthStub.withDeadlineAfter(remainingNanos, TimeUnit.NANOSECONDS);
            try {
                Iterator<HealthCheckResponse> stream = stub.watch(HealthCheckRequest.newBuilder().setService(service).build());
                while (stream.hasNext()) {
                    if (stream.next().getStatus() == HealthCheckResponse.ServingStatus.SERVING) {
                        return true;
                    }
                    backoffMillis = 250;
                }
                // Stream ended before SERVING (e.g. server shutting down) - reconnect.
            } catch (StatusRuntimeException e) {
                Status.Code code = e.getStatus().getCode();
                if (code == Status.Code.UNIMPLEMENTED) {
                    throw new UnsupportedOperationException("The DaprMQ server does not expose the gRPC health service; upgrade the server.", e);
                }
                if (code == Status.Code.DEADLINE_EXCEEDED) {
                    return false;
                }
                if (code == Status.Code.CANCELLED && Thread.currentThread().isInterrupted()) {
                    throw new CancellationException("waitForReady interrupted");
                }
                if (code != Status.Code.UNAVAILABLE) {
                    throw e;
                }
                // Server not listening yet - retry with backoff.
            }
            sleep(Math.min(backoffMillis, Math.max(1, TimeUnit.NANOSECONDS.toMillis(deadline - System.nanoTime()))));
            backoffMillis = Math.min(backoffMillis * 2, 2_000);
        }
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

    private HttpResponse<String> postJson(String operation, String queueId, String path, Object body, String leaseId) {
        return send(operation, queueId, jsonRequest(path, body, leaseId), false, null);
    }

    private static HttpRequest.Builder jsonRequest(String path, Object body, String leaseId) {
        HttpRequest.Builder builder = HttpRequest.newBuilder()
                .uri(URI.create(path))
                .header("content-type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofString(Json.write(body)));
        if (leaseId != null) {
            builder.header("lease-id", leaseId);
        }
        return builder;
    }

    /**
     * One REST call under the retry contract (sdks/testing/RETRIES_AND_READINESS.md): not-delivered
     * failures are retried within the retry timeout, unknown outcomes only when
     * {@code unknownIsRetryable}. Every attempt tells the server how much retry time is left
     * (daprmq-retry-timeout); it never cuts a delivered call short, which runs until the thread is
     * interrupted or the per-attempt limit. Returns any other response for the caller to map.
     */
    private HttpResponse<String> send(String operation, String queueId, HttpRequest.Builder request,
                                      boolean unknownIsRetryable, List<String> idempotencyKeys) {
        boolean retries = !retry.timeout().isZero() && !retry.timeout().isNegative();
        long deadline = System.nanoTime() + retry.timeout().toNanos();
        long backoffNanos = retry.initialBackoff().toNanos();

        while (true) {
            long remainingNanos = deadline - System.nanoTime();
            HttpRequest.Builder attempt = request.copy().timeout(ATTEMPT_TIMEOUT);
            if (retries) {
                long ms = Math.max(1, TimeUnit.NANOSECONDS.toMillis(remainingNanos));
                attempt.setHeader(RETRY_TIMEOUT_HEADER, String.valueOf(ms));
            }

            boolean notDelivered;
            String reason;
            try {
                HttpResponse<String> response = httpClient.send(attempt.build(), HttpResponse.BodyHandlers.ofString());
                String marker = response.headers().firstValue(DELIVERY_MARKER).orElse(null);
                if (!"not-delivered".equals(marker) && !"unknown".equals(marker)) {
                    return response;
                }
                notDelivered = "not-delivered".equals(marker);
                reason = Json.errorMessageFrom(response.body(), response.statusCode());
            } catch (ConnectException | HttpConnectTimeoutException e) {
                notDelivered = true; // the connection never opened: nothing was sent
                reason = e.toString();
            } catch (IOException e) {
                notDelivered = false; // sent, then broke or timed out
                reason = e.toString();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new CancellationException(operation + " interrupted");
            }

            boolean retryable = notDelivered || unknownIsRetryable;
            long delayNanos = (long) (ThreadLocalRandom.current().nextDouble() * backoffNanos);
            long timeLeftNanos = deadline - System.nanoTime() - delayNanos;
            if (!retries || !retryable || timeLeftNanos < retry.minAttemptWindow().toNanos()) {
                if (notDelivered) {
                    throw new DaprMQUnavailableException("DaprMQ is unavailable; " + operation + " was not performed: " + reason, operation, queueId);
                }
                throw new DeliveryUnknownException("The outcome of " + operation + " is unknown: it may or may not have been performed (" + reason + ")",
                        operation, queueId, idempotencyKeys);
            }

            sleep(TimeUnit.NANOSECONDS.toMillis(delayNanos));
            backoffNanos = Math.min(backoffNanos * 2, retry.maxBackoff().toNanos());
        }
    }

    private static void sleep(long millis) {
        try {
            Thread.sleep(millis);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new CancellationException("interrupted");
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
            case "INVALID_LOCK_ID", "INVALID_TTL", "VALIDATION_ERROR" -> new ValidationException(message);
            default -> new DaprMQException(message, errorCode);
        };
    }
}
