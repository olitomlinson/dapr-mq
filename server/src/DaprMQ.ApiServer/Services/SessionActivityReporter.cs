using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Worker-local tracking of whether each hosted session has work, off the actors' turns, and the
/// only way sessions register with their SessionCoordinatorActor. Session actors call
/// <see cref="Observe"/> after every method (an in-process call). The reporter sends each
/// coordinator only the lasting changes since it last told it:
/// - a session that got work (Active) straight away, so it is claimable about one coordinator call
///   after its enqueue, which never waits for it
/// - one that has stayed empty for the debounce period (Empty), on the tick
/// A quiet worker sends nothing.
///
/// Each queue has one sender with at most one call in flight. Changes that arrive during a call go
/// together in the next, so under load one turn registers many sessions. Nothing is marked sent
/// until its call succeeds, so a failed or deferred change is never resent as it was: the next send
/// carries each session's state as it is then. Reports from one reporter therefore arrive in order;
/// the coordinator's deferral window covers races with other workers.
/// See proposals/async-session-registration.md.
/// </summary>
public sealed class SessionActivityReporter : BackgroundService, ISessionActivityReporter
{
    private sealed class TrackedSession
    {
        public bool HasWork;
        public DateTimeOffset ChangedAt;

        /// <summary>What the coordinator was last told; null = unknown, so send the current state.</summary>
        public bool? LastSent;

        public bool Deactivating;
    }

    private readonly ISessionCoordinatorActorInvoker _invoker;
    private readonly SessionDirectoryConfig _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionActivityReporter> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(string QueueId, string SessionId), TrackedSession> _sessions = new();

    /// <summary>One per queue. Guarded by _gate.</summary>
    private sealed class QueueSender
    {
        public bool Busy;
        public bool Again;
        public bool IncludeEmpty;
        public Task Run = Task.CompletedTask;
    }

    private readonly Dictionary<string, QueueSender> _senders = new();
    private CancellationToken _stopping = CancellationToken.None;

    public SessionActivityReporter(ISessionCoordinatorActorInvoker invoker, SessionDirectoryConfig config, TimeProvider time,
        ILogger<SessionActivityReporter> logger)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Observe(string queueId, string sessionId, bool hasWork, bool reported = false)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue((queueId, sessionId), out var session))
            {
                _sessions[(queueId, sessionId)] = session = new TrackedSession { HasWork = hasWork, ChangedAt = _time.GetUtcNow() };
            }
            else if (session.HasWork != hasWork)
            {
                session.HasWork = hasWork;
                session.ChangedAt = _time.GetUtcNow();
            }

            session.Deactivating = false;
            if (reported)
            {
                session.LastSent = hasWork;
            }
            else if (hasWork && session.LastSent != true)
            {
                KickLocked(queueId, includeEmpty: false);
            }
        }
    }

    /// <inheritdoc />
    public void MarkDeactivating(string queueId, string sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue((queueId, sessionId), out var session))
            {
                session.Deactivating = true;
            }
        }
    }

    /// <inheritdoc />
    public void ResetReported(string queueId, string sessionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue((queueId, sessionId), out var session))
            {
                session.LastSent = null;
                if (session.HasWork)
                {
                    KickLocked(queueId, includeEmpty: false);
                }
            }
        }
    }

    /// <inheritdoc />
    public bool IsReportedActive(string queueId, string sessionId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue((queueId, sessionId), out var session) && session.LastSent == true;
        }
    }

    /// <summary>Every send started so far has finished (tests).</summary>
    internal Task WhenIdleAsync()
    {
        Task[] runs;
        lock (_gate)
        {
            runs = _senders.Values.Select(s => s.Run).ToArray();
        }
        return Task.WhenAll(runs);
    }

    // Called under _gate. Starts the queue's sender, or tells a busy one to go round again.
    private void KickLocked(string queueId, bool includeEmpty)
    {
        if (!_senders.TryGetValue(queueId, out var sender))
        {
            _senders[queueId] = sender = new QueueSender();
        }

        sender.IncludeEmpty |= includeEmpty;
        if (sender.Busy)
        {
            sender.Again = true;
            return;
        }

        sender.Busy = true;
        // Off the caller's thread: Observe runs inside an actor's turn.
        sender.Run = Task.Run(() => RunSenderAsync(queueId, sender));
    }

    /// <summary>
    /// Sends the queue's changes, one call at a time, until there are none left. Active changes go on
    /// any run; Empty changes only on a run the tick asked for, so a deferred Empty waits for the
    /// next tick rather than being resent at once.
    /// </summary>
    private async Task RunSenderAsync(string queueId, QueueSender sender)
    {
        var backoff = _config.ReportRetryInitial;
        while (true)
        {
            List<(string SessionId, TrackedSession Session, bool HasWork)> batch;
            lock (_gate)
            {
                bool includeEmpty = sender.IncludeEmpty;
                sender.IncludeEmpty = false;
                sender.Again = false;
                batch = TakeChangesLocked(queueId, includeEmpty, _config.ReportBatchMax);
                if (batch.Count == 0)
                {
                    sender.Busy = false;
                    return;
                }
            }

            if (await SendAsync(queueId, batch, _stopping))
            {
                backoff = _config.ReportRetryInitial;
                continue;
            }

            try
            {
                await Task.Delay(backoff, _time, _stopping);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    sender.Busy = false;
                }
                return;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _config.ReportRetryMax.Ticks));
        }
    }

    /// <summary>
    /// The tick. Forgets deactivated sessions whose final state the coordinator has accepted, then
    /// starts each queue's sender with Empty changes included. It doesn't wait for the sends, so a
    /// queue whose coordinator is unreachable (its sender retrying) doesn't hold up the others.
    /// Single caller only.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var key in _sessions.Where(s => s.Value.Deactivating && s.Value.LastSent == s.Value.HasWork).Select(s => s.Key).ToList())
            {
                _sessions.Remove(key);
            }

            foreach (var queueId in _sessions.Keys.Select(k => k.QueueId).Distinct().ToList())
            {
                KickLocked(queueId, includeEmpty: true);
            }
        }
        return Task.CompletedTask;
    }

    // Called under _gate.
    private List<(string SessionId, TrackedSession Session, bool HasWork)> TakeChangesLocked(string queueId, bool includeEmpty, int max)
    {
        var now = _time.GetUtcNow();
        var batch = new List<(string, TrackedSession, bool)>();
        foreach (var ((sessionQueueId, sessionId), session) in _sessions)
        {
            if (sessionQueueId != queueId)
            {
                continue;
            }

            bool? toSend = session.HasWork ? true
                : !includeEmpty ? null
                : session.Deactivating || now - session.ChangedAt >= _config.EmptyDebounce ? false
                : null; // still settling
            if (toSend == null || toSend == session.LastSent)
            {
                continue;
            }

            batch.Add((sessionId, session, toSend.Value));
            if (batch.Count == max)
            {
                break;
            }
        }
        return batch;
    }

    /// <summary>Returns whether the coordinator accepted the call.</summary>
    private async Task<bool> SendAsync(string queueId, List<(string SessionId, TrackedSession Session, bool HasWork)> changes, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_config.ReportCallTimeout);
            var response = await _invoker.InvokeMethodAsync<ReportSessionChangesRequest, ReportSessionChangesResponse>(
                new ActorId(queueId),
                ActorMethodNames.ReportSessionChanges,
                new ReportSessionChangesRequest
                {
                    Changes = changes.Select(c => new SessionChange { SessionId = c.SessionId, HasWork = c.HasWork }).ToList()
                },
                timeout.Token);

            var deferred = response.Deferred.ToHashSet();
            lock (_gate)
            {
                foreach (var change in changes)
                {
                    // A deferred session stays unsent, so its current state goes on the next tick.
                    change.Session.LastSent = deferred.Contains(change.SessionId) ? null : change.HasWork;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Nothing is marked sent: the retry sends each session's state as it is then.
            _logger.LogDebug(ex, "Session change report to {QueueId} failed; retrying with the current state", queueId);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false; // stopping
        }
    }

    /// <summary>
    /// On shutdown (SIGTERM), sends everything pending, Empty included, before the senders are
    /// cancelled. Waits at most <see cref="SessionDirectoryConfig.ReportCallTimeout"/>, and no longer
    /// than the host allows. A session whose report is still lost is registered by its backstop
    /// reminder.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            foreach (var queueId in _sessions.Keys.Select(k => k.QueueId).Distinct().ToList())
            {
                KickLocked(queueId, includeEmpty: true);
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_config.ReportCallTimeout);
        try
        {
            await WhenIdleAsync().WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Shutting down with session reports still unsent; their backstop reminders will register them");
        }

        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.ReportTick, _time, stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session activity report tick failed");
            }
        }
    }
}
