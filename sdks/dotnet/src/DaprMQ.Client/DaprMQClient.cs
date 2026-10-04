using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DaprMQ.Client.Exceptions;
using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.Client;

public class DaprMQClient : IDaprMQClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Fully-qualified gRPC service name (proto package + service), as registered with the server's
    // grpc.health.v1.Health service.
    private const string HealthServiceName = "daprmq.DaprMQ";
    private static readonly TimeSpan ReadyBackoffInitial = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReadyBackoffMax = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a stopping ConsumeSession stream waits for the server to finish (apply the acks it
    /// was sent, release the session) before the call is cancelled outright.
    /// </summary>
    internal static TimeSpan SessionDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly GrpcChannel? _grpcChannel;
    private readonly GrpcService.DaprMQClient _grpcClient;
    private readonly Health.HealthClient? _healthClient;
    private readonly bool _ownsHttpClient;
    private readonly bool _ownsGrpcChannel;

    /// <summary>
    /// DI-friendly constructor - both the HttpClient and GrpcChannel are already configured
    /// (base address / target) by the caller and are not disposed by this instance.
    /// </summary>
    public DaprMQClient(HttpClient httpClient, GrpcChannel grpcChannel)
        : this(httpClient, new GrpcService.DaprMQClient(grpcChannel), new Health.HealthClient(grpcChannel))
    {
        _grpcChannel = grpcChannel;
    }

    /// <summary>
    /// Test seam - lets tests substitute a CallInvoker-backed generated client (CallInvoker's
    /// methods are virtual, so it's directly Moq-mockable) without needing a live GrpcChannel.
    /// </summary>
    internal DaprMQClient(HttpClient httpClient, GrpcService.DaprMQClient grpcClient, Health.HealthClient? healthClient = null)
    {
        _httpClient = httpClient;
        _grpcChannel = null;
        _grpcClient = grpcClient;
        _healthClient = healthClient;
        _ownsHttpClient = false;
        _ownsGrpcChannel = false;
    }

    /// <summary>
    /// Convenience constructor that builds both the HttpClient and GrpcChannel internally.
    /// Disposed by this instance's DisposeAsync.
    /// </summary>
    public DaprMQClient(DaprMQClientOptions options)
        : this(new HttpClient { BaseAddress = options.HttpBaseAddress }, GrpcChannel.ForAddress(options.GrpcAddress))
    {
        _ownsHttpClient = true;
        _ownsGrpcChannel = true;
    }

    public async Task<EnqueueResult> EnqueueAsync(string queueId, IEnumerable<EnqueueItemDto> items, CancellationToken ct = default)
    {
        var body = new
        {
            items = items.Select(i => new
            {
                item = i.Item,
                priority = i.Priority,
                idempotencyKey = i.IdempotencyKey,
                sessionId = i.SessionId
            })
        };

        using var response = await _httpClient.PostAsJsonAsync(Path(queueId, "enqueue"), body, JsonOptions, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapGenericErrorAsync(response, ct);
        }

        var result = await ReadRequiredAsync<EnqueueResponseWire>(response, ct);
        return new EnqueueResult(result.Success, result.Message, result.ItemsEnqueued, result.ItemsDeduplicated);
    }

    public async Task<DequeueLockedResult?> DequeueLockedAsync(string queueId, int count = 1, int ttlSeconds = 30, string? leaseId = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(queueId, "dequeue"));
        request.Headers.Add("require-ack", "true");
        request.Headers.Add("count", count.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("ttl-seconds", ttlSeconds.ToString(CultureInfo.InvariantCulture));
        if (leaseId != null)
        {
            request.Headers.Add("lease-id", leaseId);
        }

        using var response = await _httpClient.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Locked)
        {
            var locked = await response.Content.ReadFromJsonAsync<LockedResponseWire>(JsonOptions, ct);
            return new DequeueLockedResult(Array.Empty<DequeueLockedItemDto>(), true, locked?.Message);
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadErrorMessageAsync(response, ct);
            // Dequeue's guard rejection carries no wire error code - 410 here is unambiguously the
            // session-lease guard (plain lock expiry doesn't apply to Dequeue), 400 covers both a
            // bad/missing lease-id and ordinary request validation (e.g. bad count).
            throw response.StatusCode == HttpStatusCode.Gone
                ? new SessionLeaseExpiredException(message)
                : new ValidationException(message);
        }

        var result = await ReadRequiredAsync<DequeueLockedResponseWire>(response, ct);
        var items = result.Items
            .Select(i => new DequeueLockedItemDto(i.Item, i.Priority, i.LockId, i.LockExpiresAt))
            .ToList();
        return new DequeueLockedResult(items, result.Locked, result.Message);
    }

    public async Task AcknowledgeAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default)
    {
        using var request = BuildJsonRequest(Path(queueId, "acknowledge"), new { lockId }, leaseId);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadFromJsonAsync<AcknowledgeResponseWire>(JsonOptions, ct);
        throw MapLockError(body?.ErrorCode, body?.Message ?? await ReadErrorMessageAsync(response, ct));
    }

    public async Task ExtendLockAsync(string queueId, string lockId, int additionalTtlSeconds, string? leaseId = null, CancellationToken ct = default)
    {
        using var request = BuildJsonRequest(Path(queueId, "extend-lock"), new { lockId, additionalTtlSeconds }, leaseId);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await ReadErrorMessageAsync(response, ct);
        // ExtendLock's error body carries no wire error code (unlike Acknowledge/DeadLetter), so
        // this is a best-effort status-based mapping only.
        throw response.StatusCode switch
        {
            HttpStatusCode.Gone => new LockExpiredException(message),
            HttpStatusCode.NotFound => new LockNotFoundException(message),
            _ => new ValidationException(message)
        };
    }

    public async Task DeadLetterAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default)
    {
        using var request = BuildJsonRequest(Path(queueId, "deadletter"), new { lockId }, leaseId);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadFromJsonAsync<DeadLetterResponseWire>(JsonOptions, ct);
        throw MapLockError(body?.ErrorCode, body?.Message ?? await ReadErrorMessageAsync(response, ct));
    }

    public async Task<SessionLease?> AcceptSessionAsync(string queueId, string? sessionId = null, int leaseSeconds = 30, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            Path(queueId, "sessions/accept"), new { sessionId, leaseSeconds }, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadErrorMessageAsync(response, ct);
            throw response.StatusCode switch
            {
                HttpStatusCode.NotFound => new SessionNotFoundException(message),
                HttpStatusCode.Locked => new SessionLockedException(message),
                HttpStatusCode.BadGateway => new SessionActorUnavailableException(message),
                _ => new ValidationException(message)
            };
        }

        var result = await ReadRequiredAsync<AcceptSessionResponseWire>(response, ct);
        return new SessionLease(result.SessionId, result.LeaseId, result.LeaseExpiresAt);
    }

    public async Task<SessionLease> RenewSessionLeaseAsync(string queueId, string sessionId, string leaseId, int additionalSeconds = 30, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            Path(queueId, $"sessions/{Uri.EscapeDataString(sessionId)}/renew"),
            new { leaseId, additionalSeconds }, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadErrorMessageAsync(response, ct);
            throw response.StatusCode == HttpStatusCode.Gone
                ? new SessionLeaseExpiredException(message)
                : new InvalidLeaseIdException(message);
        }

        var result = await ReadRequiredAsync<RenewSessionLeaseResponseWire>(response, ct);
        return new SessionLease(sessionId, leaseId, result.NewExpiresAt);
    }

    public async Task ReleaseSessionAsync(string queueId, string sessionId, string leaseId, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            Path(queueId, $"sessions/{Uri.EscapeDataString(sessionId)}/release"),
            new { leaseId }, JsonOptions, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidLeaseIdException(await ReadErrorMessageAsync(response, ct));
        }
    }

    public async IAsyncEnumerable<SessionDelivery> ConsumeSessionAsync(
        string queueId, string? sessionId, int leaseSeconds, int prefetchCount,
        [EnumeratorCancellation] CancellationToken ct = default,
        int sessionIdleTimeoutSeconds = 0)
    {
        // The caller's token is deliberately not bound to the call: cancelling a call drops acks
        // the server hasn't applied yet, and those have already been reported as done. Stopping -
        // by token, break or error - half-closes instead, and the call is only cancelled if the
        // server hasn't finished within SessionDrainTimeout.
        using var call = _grpcClient.ConsumeSession();
        using var drainCts = new CancellationTokenSource();
        using var writeLock = new SemaphoreSlim(1, 1);
        var halfClosed = false;

        // One writer at a time: acks come from consumer code while a stop can half-close the stream.
        async Task WriteAsync(global::DaprMQ.ApiServer.Grpc.ConsumeSessionRequest request)
        {
            await writeLock.WaitAsync();
            try
            {
                if (halfClosed)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("The session stream is closing; this message can no longer be settled on it.");
                }
                await call.RequestStream.WriteAsync(request);
            }
            finally
            {
                writeLock.Release();
            }
        }

        async Task HalfCloseAsync()
        {
            await writeLock.WaitAsync();
            try
            {
                if (!halfClosed)
                {
                    halfClosed = true;
                    await call.RequestStream.CompleteAsync();
                }
            }
            catch
            {
                // best-effort - the stream may already be broken
            }
            finally
            {
                writeLock.Release();
                drainCts.CancelAfter(SessionDrainTimeout);
            }
        }

        // Reads pass drainCts, never ct: cancelling a read's token cancels the whole call.
        using var stopping = ct.Register(() => _ = HalfCloseAsync());
        try
        {
            var start = new global::DaprMQ.ApiServer.Grpc.ConsumeSessionStart
            {
                QueueId = queueId,
                LeaseSeconds = leaseSeconds,
                PrefetchCount = prefetchCount,
                SessionIdleTimeoutSeconds = sessionIdleTimeoutSeconds
            };
            if (sessionId != null)
            {
                start.SessionId = sessionId;
            }

            await WriteAsync(new global::DaprMQ.ApiServer.Grpc.ConsumeSessionRequest { Start = start });

            var assignedSessionId = sessionId ?? string.Empty;

            while (await call.ResponseStream.MoveNext(drainCts.Token))
            {
                if (ct.IsCancellationRequested)
                {
                    continue; // stopping: let the server finish, but hand out nothing more
                }

                var response = call.ResponseStream.Current;
                switch (response.PayloadCase)
                {
                    case global::DaprMQ.ApiServer.Grpc.ConsumeSessionResponse.PayloadOneofCase.SessionAssigned:
                        assignedSessionId = response.SessionAssigned.SessionId;
                        break;

                    case global::DaprMQ.ApiServer.Grpc.ConsumeSessionResponse.PayloadOneofCase.Delivered:
                        var delivered = response.Delivered;
                        yield return new SessionDelivery
                        {
                            SessionId = assignedSessionId,
                            LockId = delivered.LockId,
                            Item = JsonDocument.Parse(delivered.ItemJson).RootElement.Clone(),
                            Priority = delivered.Priority,
                            LockExpiresAt = delivered.LockExpiresAt,
                            AckAsync = _ => WriteAsync(new global::DaprMQ.ApiServer.Grpc.ConsumeSessionRequest
                            {
                                Ack = new global::DaprMQ.ApiServer.Grpc.ConsumeSessionAck { LockId = delivered.LockId }
                            }),
                            DeadLetterAsync = _ => WriteAsync(new global::DaprMQ.ApiServer.Grpc.ConsumeSessionRequest
                            {
                                DeadLetter = new global::DaprMQ.ApiServer.Grpc.ConsumeSessionDeadLetter { LockId = delivered.LockId }
                            })
                        };
                        break;

                    case global::DaprMQ.ApiServer.Grpc.ConsumeSessionResponse.PayloadOneofCase.Error:
                        throw MapSessionError(response.Error.ErrorCode, response.Error.Message);

                    case global::DaprMQ.ApiServer.Grpc.ConsumeSessionResponse.PayloadOneofCase.SessionLost:
                        throw new SessionLostException(response.SessionLost.Message);
                }
            }

            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // However the consumer stopped, let the server apply what it was sent before the call
            // is disposed (which cancels it if still running).
            await HalfCloseAsync();
            try
            {
                while (await call.ResponseStream.MoveNext(drainCts.Token))
                {
                }
            }
            catch
            {
                // drain timed out, or the stream had already failed
            }
        }
    }

    /// <summary>
    /// Waits until the server reports SERVING over the standard gRPC health protocol
    /// (grpc.health.v1.Health/Watch). Retries while the server isn't listening yet; has no deadline
    /// of its own, so apply one with <paramref name="ct"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The server predates the health service.</exception>
    public async Task WaitForReadyAsync(CancellationToken ct = default)
    {
        var healthClient = _healthClient
            ?? throw new InvalidOperationException("This DaprMQClient was created without a health client.");
        var backoff = ReadyBackoffInitial;

        while (true)
        {
            try
            {
                using var call = healthClient.Watch(new HealthCheckRequest { Service = HealthServiceName }, cancellationToken: ct);
                await foreach (var response in call.ResponseStream.ReadAllAsync(ct))
                {
                    if (response.Status == HealthCheckResponse.Types.ServingStatus.Serving)
                    {
                        return;
                    }
                    backoff = ReadyBackoffInitial;
                }
                // Stream ended before SERVING (e.g. server shutting down) - reconnect.
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                throw new NotSupportedException("The DaprMQ server does not expose the gRPC health service; upgrade the server.", ex);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                // Server not listening yet - retry with backoff.
            }

            await Task.Delay(backoff, ct);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, ReadyBackoffMax.Ticks));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        if (_ownsGrpcChannel && _grpcChannel != null)
        {
            await _grpcChannel.ShutdownAsync();
        }
    }

    private static string Path(string queueId, string suffix) => $"queue/{Uri.EscapeDataString(queueId)}/{suffix}";

    private static HttpRequestMessage BuildJsonRequest(string path, object body, string? leaseId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        if (leaseId != null)
        {
            request.Headers.Add("lease-id", leaseId);
        }
        return request;
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct)
            ?? throw new DaprMQException($"Empty or malformed response body from {response.RequestMessage?.RequestUri}");
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponseWire>(JsonOptions, ct);
            return error?.Message ?? $"Request failed with status {(int)response.StatusCode}";
        }
        catch
        {
            return $"Request failed with status {(int)response.StatusCode}";
        }
    }

    private static async Task<DaprMQException> MapGenericErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var message = await ReadErrorMessageAsync(response, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => new ValidationException(message),
            HttpStatusCode.NotFound => new ActorNotFoundException(message),
            _ => new DaprMQException(message)
        };
    }

    private static DaprMQException MapLockError(string? errorCode, string message) => errorCode switch
    {
        "LOCK_NOT_FOUND" => new LockNotFoundException(message),
        "LOCK_EXPIRED" => new LockExpiredException(message),
        "SESSION_LEASE_EXPIRED" => new SessionLeaseExpiredException(message),
        "INVALID_LEASE_ID" => new InvalidLeaseIdException(message),
        "INVALID_LOCK_ID" or "INVALID_TTL" => new ValidationException(message),
        _ => new DaprMQException(message, errorCode)
    };

    private static DaprMQException MapSessionError(string errorCode, string message) => errorCode switch
    {
        "SESSION_NOT_FOUND" => new SessionNotFoundException(message),
        "SESSION_LOCKED" => new SessionLockedException(message),
        "NO_SESSIONS_AVAILABLE" => new NoSessionsAvailableException(message),
        "SESSION_ACTOR_UNAVAILABLE" => new SessionActorUnavailableException(message),
        _ => new DaprMQException(message, errorCode)
    };
}
