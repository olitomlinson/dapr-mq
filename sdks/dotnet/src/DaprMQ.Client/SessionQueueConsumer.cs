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
    public int PrefetchCount { get; init; } = 10;
    public int MinBackoffSeconds { get; init; } = 1;
    public int MaxBackoffSeconds { get; init; } = 60;
    public SessionHandlerFailureAction OnHandlerException { get; init; } = SessionHandlerFailureAction.DeadLetterMessage;
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum SessionHandlerFailureAction
{
    DeadLetterMessage,
    AbandonSession,
    Both
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
    private readonly CancellationTokenSource _stopCts = new();

    private CancellationTokenRegistration _externalCancellationRegistration;
    private List<Task>? _slotTasks;

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

    public Task StartAsync(CancellationToken ct = default)
    {
        if (ct.CanBeCanceled)
        {
            _externalCancellationRegistration = ct.Register(() => _stopCts.Cancel());
        }

        _slotTasks = Enumerable.Range(0, _options.MaxConcurrentSessions)
            .Select(_ => Task.Run(() => RunSlotAsync(_stopCts.Token)))
            .ToList();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops claiming new sessions, gives in-flight handlers up to DrainTimeout to finish, then
    /// closes their streams - closing the stream is itself what releases the session, no
    /// separate ReleaseSessionAsync call is needed here.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _stopCts.Cancel();

        if (_slotTasks is { Count: > 0 })
        {
            var drain = Task.WhenAll(_slotTasks);
            await Task.WhenAny(drain, Task.Delay(_options.DrainTimeout, ct));
        }
    }

    private async Task RunSlotAsync(CancellationToken stopToken)
    {
        var backoffSeconds = _options.MinBackoffSeconds;

        while (!stopToken.IsCancellationRequested)
        {
            var sessionWasClaimed = false;
            try
            {
                await foreach (var delivery in _client.ConsumeSessionAsync(
                    _queueId, _options.TargetSessionId, _options.LeaseSeconds, _options.PrefetchCount, stopToken))
                {
                    sessionWasClaimed = true;
                    await HandleDeliveryAsync(delivery, stopToken);
                }

                sessionWasClaimed = true; // stream ended cleanly after a successful claim (drained)
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && stopToken.IsCancellationRequested)
            {
                break;
            }
            catch (NoSessionsAvailableException) { }
            catch (SessionNotFoundException) { }
            catch (SessionLockedException) { }
            catch (SessionActorUnavailableException) { }
            catch (SessionLostException)
            {
                sessionWasClaimed = true; // claim succeeded; the lease was lost afterward
            }
            catch (Exception) when (!stopToken.IsCancellationRequested)
            {
                // Any other exception surfacing mid-stream - including a handler exception
                // re-thrown by HandleDeliveryAsync under AbandonSession/Both - ends this slot's
                // current stream early. sessionWasClaimed is already true by the time the
                // `await foreach` body can throw, so the outer loop below retries immediately
                // rather than backing off as if the claim itself had failed.
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

    private async Task HandleDeliveryAsync(SessionDelivery delivery, CancellationToken ct)
    {
        var context = new SessionMessageContext(_queueId, delivery.SessionId, delivery.LockId, delivery.Item, delivery.Priority);
        try
        {
            await _handler(context, ct);
            await delivery.AckAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            switch (_options.OnHandlerException)
            {
                case SessionHandlerFailureAction.DeadLetterMessage:
                    await delivery.DeadLetterAsync(ct);
                    break;
                case SessionHandlerFailureAction.AbandonSession:
                    throw; // unwinds the `await foreach`, ending this slot's stream early
                case SessionHandlerFailureAction.Both:
                    await delivery.DeadLetterAsync(ct);
                    throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _externalCancellationRegistration.Dispose();
        _stopCts.Dispose();
    }
}
