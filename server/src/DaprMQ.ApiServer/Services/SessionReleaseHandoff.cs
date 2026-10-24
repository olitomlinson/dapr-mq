using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Holds the lease of a ConsumeSession stream whose session drained, briefly, so the next
/// any-available stream for the same queue on this gateway can end it and claim its own session in
/// one coordinator turn (ReleaseAndAcceptSession) instead of two. One stream is one session, and a
/// consumer opens its next stream as soon as one drains, so the release and the claim arrive
/// moments apart. A lease nobody takes within the window is released on its own, as before.
/// </summary>
public sealed class SessionReleaseHandoff : IHostedService
{
    public sealed record ParkedRelease(string QueueId, string SessionId, string LeaseId);

    private sealed class Entry(ParkedRelease release)
    {
        public ParkedRelease Release { get; } = release;
        public ITimer? Timer;
    }

    private readonly ISessionCoordinatorActorInvoker _invoker;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionReleaseHandoff> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedList<Entry>> _parked = new();

    public SessionReleaseHandoff(ISessionCoordinatorActorInvoker invoker, TimeSpan window, TimeProvider time, ILogger<SessionReleaseHandoff> logger)
    {
        _invoker = invoker;
        _window = window;
        _time = time;
        _logger = logger;
    }

    /// <summary>Returns false when the handoff is off (a zero window): the caller releases itself.</summary>
    public bool Park(string queueId, string sessionId, string leaseId)
    {
        if (_window <= TimeSpan.Zero)
        {
            return false;
        }

        var entry = new Entry(new ParkedRelease(queueId, sessionId, leaseId));
        lock (_gate)
        {
            if (!_parked.TryGetValue(queueId, out var list))
            {
                _parked[queueId] = list = new LinkedList<Entry>();
            }
            list.AddLast(entry);
            entry.Timer = _time.CreateTimer(_ => _ = ExpireAsync(entry), null, _window, Timeout.InfiniteTimeSpan);
        }
        return true;
    }

    /// <summary>The oldest lease parked for this queue, now the caller's to end; null if there is none.</summary>
    public ParkedRelease? TryTake(string queueId)
    {
        lock (_gate)
        {
            if (!_parked.TryGetValue(queueId, out var list))
            {
                return null;
            }

            var entry = list.First!.Value;
            Remove(list, entry);
            return entry.Release;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Releases everything still parked, so a stopping gateway leaves no lease to lapse.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        List<Entry> all;
        lock (_gate)
        {
            all = _parked.Values.SelectMany(l => l).ToList();
            foreach (var entry in all)
            {
                entry.Timer?.Dispose();
            }
            _parked.Clear();
        }
        return Task.WhenAll(all.Select(e => ReleaseAsync(e.Release)));
    }

    private async Task ExpireAsync(Entry entry)
    {
        lock (_gate)
        {
            if (!_parked.TryGetValue(entry.Release.QueueId, out var list) || !list.Contains(entry))
            {
                return; // taken, or released by StopAsync
            }
            Remove(list, entry);
        }
        await ReleaseAsync(entry.Release);
    }

    // Called under _gate.
    private void Remove(LinkedList<Entry> list, Entry entry)
    {
        entry.Timer?.Dispose();
        list.Remove(entry);
        if (list.Count == 0)
        {
            _parked.Remove(entry.Release.QueueId);
        }
    }

    private async Task ReleaseAsync(ParkedRelease release)
    {
        try
        {
            await _invoker.InvokeMethodAsync<ReleaseSessionRequest, ReleaseSessionResponse>(
                new ActorId(release.QueueId),
                ActorMethodNames.ReleaseSession,
                new ReleaseSessionRequest { SessionId = release.SessionId, LeaseId = release.LeaseId },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release session {SessionId} of queue {QueueId} after its handoff window", release.SessionId, release.QueueId);
        }
    }
}
