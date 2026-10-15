using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Worker-local tracking of whether each hosted session has work, off the actors' turns. Session
/// actors call <see cref="Observe"/> after every method (an in-process call); each tick, the
/// reporter sends every SessionCoordinatorActor only the lasting changes since it last told it:
/// a session that got work (Active, on the next tick), or one that has stayed empty for the
/// debounce period (Empty). A quiet worker sends nothing.
///
/// Ordering: one flush runs at a time and nothing is marked sent until its call succeeds, so a
/// failed or deferred change is never resent as it was - the next tick sends each session's state
/// as it is then. Reports from one reporter therefore arrive in order; the coordinator's deferral
/// window covers races with the direct RegisterSession path and with other workers.
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

    public SessionActivityReporter(ISessionCoordinatorActorInvoker invoker, SessionDirectoryConfig config, TimeProvider time,
        ILogger<SessionActivityReporter> logger)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Observe(string queueId, string sessionId, bool hasWork, bool deactivating = false)
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

            session.Deactivating = deactivating;
        }
    }

    /// <inheritdoc />
    public void MarkReported(string queueId, string sessionId, bool hasWork)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue((queueId, sessionId), out var session) && session.HasWork == hasWork)
            {
                session.LastSent = hasWork;
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
            }
        }
    }

    /// <summary>
    /// Sends each coordinator its sessions' changes, one call per queue, then forgets deactivated
    /// sessions whose final state the coordinator has accepted. Single caller only.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batches = TakeChanges();
        await Task.WhenAll(batches.Select(batch => SendAsync(batch.Key, batch.Value, cancellationToken)));

        lock (_gate)
        {
            foreach (var key in _sessions.Where(s => s.Value.Deactivating && s.Value.LastSent == s.Value.HasWork).Select(s => s.Key).ToList())
            {
                _sessions.Remove(key);
            }
        }
    }

    private Dictionary<string, List<(string SessionId, TrackedSession Session, bool HasWork)>> TakeChanges()
    {
        var now = _time.GetUtcNow();
        var batches = new Dictionary<string, List<(string, TrackedSession, bool)>>();
        lock (_gate)
        {
            foreach (var ((queueId, sessionId), session) in _sessions)
            {
                bool? toSend = session.HasWork ? true
                    : session.Deactivating || now - session.ChangedAt >= _config.EmptyDebounce ? false
                    : null; // still settling
                if (toSend == null || toSend == session.LastSent)
                {
                    continue;
                }

                if (!batches.TryGetValue(queueId, out var batch))
                {
                    batches[queueId] = batch = [];
                }
                batch.Add((sessionId, session, toSend.Value));
            }
        }
        return batches;
    }

    private async Task SendAsync(string queueId, List<(string SessionId, TrackedSession Session, bool HasWork)> changes, CancellationToken cancellationToken)
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Nothing is marked sent: the next tick sends each session's state as it is then.
            _logger.LogDebug(ex, "Session change report to {QueueId} failed; resending current state next tick", queueId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
