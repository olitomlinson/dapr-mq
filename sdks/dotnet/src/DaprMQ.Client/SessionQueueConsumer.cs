using System.Text.Json;
using DaprMQ.Client.Exceptions;
using Grpc.Core;

namespace DaprMQ.Client;

public record SessionQueueConsumerOptions
{
    public int MaxConcurrentSessions { get; init; } = 4;

    /// <summary>Sticky routing to one specific session. Must pair with MaxConcurrentSessions == 1.</summary>
    public string? TargetSessionId { get; init; }

    public int LeaseSeconds { get; init; } = 30;
    /// <summary>
    /// Items kept in flight per session. Above 1, a nack can reorder the session: items already
    /// delivered are handled before the nacked one comes back (same as Azure Service Bus prefetch).
    /// </summary>
    public int PrefetchCount { get; init; } = 1;
    public int MinBackoffSeconds { get; init; } = 1;
    public int MaxBackoffSeconds { get; init; } = 60;
    public SessionHandlerFailureAction OnHandlerException { get; init; } = SessionHandlerFailureAction.DeadLetterMessage;
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Max time (seconds) to wait for a message on the currently held session before the server
    /// treats it as drained and this slot moves on to claim another. 0 = server default (currently
    /// LeaseSeconds). See DaprMQGrpcService.ConsumeSession's idle-drain and Azure Service Bus's
    /// ServiceBusSessionProcessorOptions.SessionIdleTimeout, which this mirrors.
    /// </summary>
    public int SessionIdleTimeoutSeconds { get; init; } = 0;
}

public enum SessionHandlerFailureAction
{
    DeadLetterMessage,
    AbandonSession,
    Both,

    /// <summary>Return the message to the front of the session for redelivery.</summary>
    NackMessage
}

/// <summary>
/// No LeaseId here (unlike the low-level unary session API) - the ConsumeSession wire protocol
/// never exposes one to the client, since the server tracks the lease internally. See
/// <see cref="SessionDelivery"/>.
/// </summary>
public record SessionMessageContext(string QueueId, string SessionId, string LockId, JsonElement Item, int Priority);

/// <summary>
/// Manages a pool of `MaxConcurrentSessions` independent slots, each looping over a
/// <see cref="IDaprMQClient.ConsumeSessionAsync"/> stream: claim a session, hand each delivered
/// item to the caller's handler, ack/dead-letter it, and repeat until the session drains - then
/// claim another. Backoff on a failed claim mirrors HttpSinkActor's empty-queue backoff shape
/// (double on miss, reset on success, capped).
/// </summary>
public sealed class SessionQueueConsumer : IAsyncDisposable
{
    private readonly IDaprMQClient _client;
    private readonly string _queueId;
    private readonly SessionQueueConsumerOptions _options;
    private readonly Func<SessionMessageContext, CancellationToken, Task> _handler;
    // Cancelled when stopping begins: no more sessions or messages are started.
    private readonly CancellationTokenSource _stopCts = new();
    // Handlers' token: cancelled only once DrainTimeout runs out.
    private readonly CancellationTokenSource _handlerCts = new();
    private readonly object _stopGate = new();

    private CancellationTokenRegistration _externalCancellationRegistration;
    private List<Task>? _slotTasks;
    private Task? _drain;

    /// <summary>
    /// Test seam - substituted by tests to assert on requested backoff durations without
    /// actually waiting real seconds. Defaults to a real delay.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    public SessionQueueConsumer(
        IDaprMQClient client,
        string queueId,
        SessionQueueConsumerOptions options,
        Func<SessionMessageContext, CancellationToken, Task> handler)
    {
        if (options.TargetSessionId != null && options.MaxConcurrentSessions != 1)
        {
            throw new ArgumentException(
                $"{nameof(SessionQueueConsumerOptions.TargetSessionId)} requires {nameof(SessionQueueConsumerOptions.MaxConcurrentSessions)} == 1.",
                nameof(options));
        }

        _client = client;
        _queueId = queueId;
        _options = options;
        _handler = handler;
    }

    /// <summary>Starts consuming. Cancelling <paramref name="ct"/> stops the consumer as StopAsync does.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _slotTasks = Enumerable.Range(0, _options.MaxConcurrentSessions)
            .Select(_ => Task.Run(RunSlotAsync))
            .ToList();

        if (ct.CanBeCanceled)
        {
            _externalCancellationRegistration = ct.Register(() => BeginStop());
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops claiming sessions, lets in-flight handlers finish and settle for up to DrainTimeout
    /// (then cancels their token), and returns once every stream has closed - closing the stream
    /// is itself what releases the session. Prefetched, unhandled messages return with their
    /// session. Cancelling <paramref name="ct"/> cancels the handlers and stops waiting.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            await BeginStop().WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _handlerCts.Cancel();
        }
    }

    private Task BeginStop()
    {
        lock (_stopGate)
        {
            return _drain ??= DrainAsync();
        }
    }

    private async Task DrainAsync()
    {
        _stopCts.Cancel();
        if (_slotTasks is not { Count: > 0 })
        {
            return;
        }

        var slots = Task.WhenAll(_slotTasks);
        if (await Task.WhenAny(slots, Task.Delay(_options.DrainTimeout)) != slots)
        {
            _handlerCts.Cancel();
        }
        await slots;
    }

    private async Task RunSlotAsync()
    {
        var stopToken = _stopCts.Token;
        var backoffSeconds = _options.MinBackoffSeconds;

        while (!stopToken.IsCancellationRequested)
        {
            var sessionWasClaimed = false;

            // The stream is closed when stopping begins if the slot is idle, otherwise once the
            // running message is settled - closing it under a handler would lose that message's ack.
            using var streamCts = new CancellationTokenSource();
            var gate = new object();
            var handling = false;
            using var stopping = stopToken.Register(() =>
            {
                lock (gate)
                {
                    if (!handling)
                    {
                        streamCts.Cancel();
                    }
                }
            });

            try
            {
                await foreach (var delivery in _client.ConsumeSessionAsync(
                    _queueId, _options.TargetSessionId, _options.LeaseSeconds, _options.PrefetchCount, streamCts.Token,
                    sessionIdleTimeoutSeconds: _options.SessionIdleTimeoutSeconds))
                {
                    sessionWasClaimed = true;
                    lock (gate)
                    {
                        handling = !stopToken.IsCancellationRequested;
                    }
                    if (!handling)
                    {
                        break; // stopping: don't start another message, even a prefetched one
                    }

                    var abandon = await HandleDeliveryAsync(delivery);
                    lock (gate)
                    {
                        handling = false;
                    }
                    if (abandon || stopToken.IsCancellationRequested)
                    {
                        break; // ends this stream, after the server applies what was sent
                    }
                }

                sessionWasClaimed = true; // stream ended cleanly after a successful claim (drained)
            }
            catch (NoSessionsAvailableException) { }
            catch (SessionNotFoundException) { }
            catch (SessionLockedException) { }
            catch (SessionActorUnavailableException) { }
            catch (SessionLostException)
            {
                sessionWasClaimed = true; // claim succeeded; the lease was lost afterward
            }
            catch (Exception)
            {
                // Stopping, or any other exception surfacing mid-stream (a broken stream, a failed
                // settle), ends this slot's current stream early. sessionWasClaimed is already true
                // by the time the `await foreach` body can throw, so the outer loop below retries
                // immediately rather than backing off as if the claim itself had failed.
            }

            if (stopToken.IsCancellationRequested)
            {
                break;
            }

            if (sessionWasClaimed)
            {
                backoffSeconds = _options.MinBackoffSeconds;
                continue;
            }

            try
            {
                await DelayAsync(TimeSpan.FromSeconds(backoffSeconds), stopToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoffSeconds = Math.Min(backoffSeconds * 2, _options.MaxBackoffSeconds);
        }
    }

    /// <summary>Runs the handler and settles the message; returns whether to abandon the session.</summary>
    private async Task<bool> HandleDeliveryAsync(SessionDelivery delivery)
    {
        var ct = _handlerCts.Token;
        var context = new SessionMessageContext(_queueId, delivery.SessionId, delivery.LockId, delivery.Item, delivery.Priority);
        try
        {
            await _handler(context, ct);
        }
        catch (Exception)
        {
            if (_stopCts.IsCancellationRequested)
            {
                return true; // stopping: leave it unsettled to return with the session
            }

            switch (_options.OnHandlerException)
            {
                case SessionHandlerFailureAction.AbandonSession:
                    return true;
                case SessionHandlerFailureAction.Both:
                    await delivery.DeadLetterAsync(ct);
                    return true;
                case SessionHandlerFailureAction.NackMessage:
                    await delivery.NackAsync(ct);
                    return false;
                default:
                    await delivery.DeadLetterAsync(ct);
                    return false;
            }
        }

        await delivery.AckAsync(ct);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _externalCancellationRegistration.Dispose();
        _stopCts.Dispose();
        _handlerCts.Dispose();
    }
}
