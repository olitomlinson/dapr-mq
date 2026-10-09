using System.Diagnostics;
using System.Text.Json;

namespace DaprMQ.Client;

public record QueueConsumerOptions
{
    /// <summary>
    /// Delivered but unsettled messages the server keeps in flight to this consumer: the stream's
    /// <see cref="ConsumeOptions.PrefetchCount"/> (1-1000). Messages over
    /// <see cref="MaxConcurrentHandlers"/> wait locked, and the server keeps their locks alive.
    /// </summary>
    public int MaxActiveMessages { get; init; } = 100;

    /// <summary>Handlers running at once. 0 = unlimited, which <see cref="MaxActiveMessages"/> bounds.</summary>
    public int MaxConcurrentHandlers { get; init; } = 0;

    /// <summary>Passed to the stream; the server renews each lock until its message is settled.</summary>
    public TimeSpan LockTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Lets replicas share the queue, each holding its own locks.</summary>
    public bool AllowCompetingConsumers { get; init; } = true;

    /// <summary>
    /// Handles messages one at a time in queue order, including after a nack: forces a window of 1,
    /// one handler, and no competing consumers.
    /// </summary>
    public bool StrictOrder { get; init; }

    public QueueHandlerFailureAction OnHandlerError { get; init; } = QueueHandlerFailureAction.Nack;

    /// <summary>Paces nacks after handler errors, so a failing handler doesn't spin. 0 = unpaced.</summary>
    public double MaxRetriableErrorsPerSec { get; init; } = 10;

    /// <summary>Reconnect backoff after the stream breaks, doubling to the max; resets after a delivery.</summary>
    public int MinBackoffSeconds { get; init; } = 1;
    public int MaxBackoffSeconds { get; init; } = 60;

    /// <summary>How long <see cref="QueueConsumer.StopAsync"/> waits for running handlers before cancelling them.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public enum QueueHandlerFailureAction
{
    /// <summary>Return the message to its original position for redelivery.</summary>
    Nack,
    DeadLetter
}

public record QueueMessageContext(string QueueId, string LockId, JsonElement Item, int Priority, int DeliveryCount);

/// <summary>
/// Runs a handler over a plain queue's <see cref="IDaprMQClient.ConsumeAsync"/> stream: the handler's
/// success acks the message, an error nacks or dead-letters it, and a broken stream is reopened with
/// backoff (the server has already returned whatever was unsettled on it).
/// </summary>
public sealed class QueueConsumer : IAsyncDisposable
{
    private readonly IDaprMQClient _client;
    private readonly string _queueId;
    private readonly QueueConsumerOptions _options;
    private readonly Func<QueueMessageContext, CancellationToken, Task> _handler;
    // Cancelled when stopping begins: no more messages are started.
    private readonly CancellationTokenSource _stopCts = new();
    // Handlers' token: cancelled only once DrainTimeout runs out.
    private readonly CancellationTokenSource _handlerCts = new();
    private readonly object _stopGate = new();
    private readonly object _paceGate = new();
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private TimeSpan _nextNackSlot = TimeSpan.Zero;

    private CancellationTokenRegistration _externalCancellationRegistration;
    private Task? _loop;
    private Task? _drain;

    /// <summary>Test seam for backoff and nack pacing waits. Defaults to a real delay.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    public QueueConsumer(
        IDaprMQClient client,
        string queueId,
        QueueConsumerOptions options,
        Func<QueueMessageContext, CancellationToken, Task> handler)
    {
        _client = client;
        _queueId = queueId;
        _options = options;
        _handler = handler;
    }

    /// <summary>Starts consuming. Cancelling <paramref name="ct"/> stops the consumer as StopAsync does.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _loop = Task.Run(RunAsync);
        if (ct.CanBeCanceled)
        {
            _externalCancellationRegistration = ct.Register(() => BeginStop());
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops handing out messages, lets running handlers finish and settle for up to DrainTimeout
    /// (then cancels their token), and closes the stream, so the server returns every message not
    /// yet handled straight away. Cancelling <paramref name="ct"/> cancels the handlers and stops waiting.
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
        if (_loop == null)
        {
            return;
        }

        if (await Task.WhenAny(_loop, Task.Delay(_options.DrainTimeout)) != _loop)
        {
            _handlerCts.Cancel();
        }
        await _loop;
    }

    private async Task RunAsync()
    {
        var stopToken = _stopCts.Token;
        var strict = _options.StrictOrder;
        var streamOptions = new ConsumeOptions
        {
            PrefetchCount = strict ? 1 : Math.Max(_options.MaxActiveMessages, 1),
            LockTtl = _options.LockTtl,
            AllowCompetingConsumers = !strict && _options.AllowCompetingConsumers
        };
        var handlerLimit = strict ? 1 : _options.MaxConcurrentHandlers;
        using var slots = handlerLimit > 0 ? new SemaphoreSlim(handlerLimit) : null;
        var backoffSeconds = _options.MinBackoffSeconds;

        while (!stopToken.IsCancellationRequested)
        {
            var delivered = false;
            var running = new HashSet<Task>();
            var gate = new object();

            // On stop the stream stays open until running handlers have settled on it, then closes:
            // closing it under a handler would lose that message's ack.
            using var streamCts = new CancellationTokenSource();
            using var stopping = stopToken.Register(() =>
            {
                Task[] inFlight;
                lock (gate)
                {
                    inFlight = [.. running];
                }
                _ = Task.WhenAll(inFlight).ContinueWith(_ => streamCts.Cancel(), TaskScheduler.Default);
            });

            try
            {
                await foreach (var delivery in _client.ConsumeAsync(_queueId, streamOptions, streamCts.Token))
                {
                    delivered = true;
                    if (slots != null)
                    {
                        try
                        {
                            await slots.WaitAsync(stopToken);
                        }
                        catch (OperationCanceledException)
                        {
                            continue; // stopping: left unsettled, returned when the stream closes
                        }
                    }

                    lock (gate)
                    {
                        if (stopToken.IsCancellationRequested)
                        {
                            slots?.Release();
                            continue;
                        }

                        var task = HandleAsync(delivery, slots);
                        running.Add(task);
                        _ = task.ContinueWith(t => { lock (gate) { running.Remove(t); } }, TaskScheduler.Default);
                    }
                }
            }
            catch (Exception)
            {
                // Stopping, or the stream broke: either way, reopen (or stop) below.
            }

            Task[] remaining;
            lock (gate)
            {
                remaining = [.. running];
            }
            await Task.WhenAll(remaining);

            if (stopToken.IsCancellationRequested)
            {
                break;
            }

            if (delivered)
            {
                backoffSeconds = _options.MinBackoffSeconds;
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

    /// <summary>Runs the handler and settles the message. Never throws.</summary>
    private async Task HandleAsync(QueueDelivery delivery, SemaphoreSlim? slots)
    {
        await Task.Yield(); // run off the stream's reader
        var ct = _handlerCts.Token;
        try
        {
            try
            {
                await _handler(new QueueMessageContext(_queueId, delivery.LockId, delivery.Item, delivery.Priority, delivery.DeliveryCount), ct);
            }
            catch (Exception)
            {
                if (_stopCts.IsCancellationRequested)
                {
                    return; // stopping: left unsettled, returned when the stream closes
                }

                if (_options.OnHandlerError == QueueHandlerFailureAction.DeadLetter)
                {
                    await delivery.DeadLetterAsync(ct);
                }
                else
                {
                    await PaceNackAsync(ct);
                    await delivery.NackAsync(ct);
                }
                return;
            }

            await delivery.AckAsync(ct);
        }
        catch (Exception)
        {
            // The stream closed under the message (StreamClosedException) or settling failed: the
            // server returns it to the queue.
        }
        finally
        {
            slots?.Release();
        }
    }

    /// <summary>Waits for this nack's slot: at most MaxRetriableErrorsPerSec nacks a second.</summary>
    private Task PaceNackAsync(CancellationToken ct)
    {
        if (_options.MaxRetriableErrorsPerSec <= 0)
        {
            return Task.CompletedTask;
        }

        TimeSpan wait;
        lock (_paceGate)
        {
            var now = Stopwatch.GetElapsedTime(_epoch);
            var slot = _nextNackSlot > now ? _nextNackSlot : now;
            _nextNackSlot = slot + TimeSpan.FromSeconds(1 / _options.MaxRetriableErrorsPerSec);
            wait = slot - now;
        }
        return wait > TimeSpan.Zero ? DelayAsync(wait, ct) : Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _externalCancellationRegistration.Dispose();
        _stopCts.Dispose();
        _handlerCts.Dispose();
    }
}
