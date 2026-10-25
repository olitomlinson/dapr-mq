using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// State for SessionCoordinatorActor: every session known to the queue, plus the FIFO of sessions
/// waiting to be claimed. Kept in one blob - the directory stays small because eviction keeps up
/// with session creation (see proposals/session-directory.md, "Why the directory isn't segmented").
/// </summary>
public record SessionCoordinatorMetadata
{
    public Dictionary<string, SessionEntry> SessionDirectory { get; init; } = new();

    /// <summary>
    /// FIFO of Active, unleased session ids, in the order they got work or had their lease end. An
    /// any-available claim takes the head. Ids can go stale (evicted, Idle, leased); claims drop
    /// them when they reach the head.
    /// </summary>
    public List<string> ReadyQueue { get; init; } = new();
}

/// <summary>
/// One session's directory entry. Active means the session is believed to hold queued or locked
/// items; it is set by a registration, an Active report or a read that finds items, and cleared by
/// an Empty report, a lease end with nothing left, or a read that finds it empty.
/// </summary>
public record SessionEntry
{
    public bool Active { get; init; }

    /// <summary>The id is currently in the ReadyQueue.</summary>
    public bool Queued { get; init; }

    public double? IdleSince { get; init; }

    /// <summary>Idle: when the final eviction read is due. Active: when the backstop read is due.</summary>
    public double NextCheckAt { get; init; }

    /// <summary>Current backstop interval for an Active entry whose reads keep finding items.</summary>
    public double BackoffSeconds { get; init; }
}

/// <summary>
/// Counts kept beside the directory, written in the same save as "metadata". The only key the KEDA
/// scaler reads.
/// </summary>
public record DirectorySummary
{
    public int TotalCount { get; init; }

    /// <summary>The scaler's NonEmptySessions.</summary>
    public int ActiveCount { get; init; }
}

/// <summary>
/// Lease state for a claimed session, keyed "session-lock_{sessionId}". Unlike QueueActor's
/// per-item LockState, this never holds item payload - a session lease is purely a
/// consumer-ownership gate, not something that requeues anything on expiry.
/// </summary>
public record SessionLockState
{
    public required string SessionId { get; init; }
    public required string LeaseId { get; init; }
    public required double CreatedAt { get; init; }
    public required double ExpiresAt { get; init; }
}

/// <summary>
/// SessionCoordinatorActor - tracks the sessions of a logical queue, which of them have work, and
/// which are leased to a consumer. Workers tell it about changes (RegisterSession directly on a
/// session's first work, ReportSessionChanges from each worker's SessionActivityReporter), so it
/// never has to walk the sessions. See ISessionCoordinatorActor for the delegation rationale.
/// </summary>
public class SessionCoordinatorActor : Actor, ISessionCoordinatorActor, IRemindable
{
    private readonly IQueueActorInvoker _queueActorInvoker;
    private readonly IQueueActorStateReader _queueActorStateReader;
    private readonly ICatalogRegistrar _catalogRegistrar;
    private readonly SessionDirectoryConfig _config;
    private readonly TimeProvider _time;

    private const int MinLeaseSeconds = 1;
    private const int MaxLeaseSeconds = 300;
    private const int LeaseIdLength = 11;
    private const int MaxClaimAttempts = 16;

    // Same naming convention QueueActor uses to recognize a session actor's id
    // ("{queueId}-session-{sessionId}") - here used to build that id from this actor's own id
    // (which is the queueId, since SessionCoordinatorActor and its QueueActor share an id string).
    private const string SessionActorIdMarker = "-session-";

    internal const string MetadataKey = "metadata";
    internal const string SummaryKey = "dir_summary";
    internal const string EvictionReminderName = "directory-evict";
    internal const string CatchUpReminderName = "directory-evict-catch-up";
    private const string LeaseReminderPrefix = "session-";

    // When each session was last marked Active. Memory only: an Empty change this soon after may be
    // older than that registration, so it is deferred. Rebuilt on activation as "all Active entries,
    // now", which only defers Empty changes for one window.
    private readonly Dictionary<string, double> _lastActiveAt = new();

    private bool _evictionReminderRegistered;

    public SessionCoordinatorActor(ActorHost host, IQueueActorInvoker queueActorInvoker, IQueueActorStateReader queueActorStateReader,
        ICatalogRegistrar catalogRegistrar, SessionDirectoryConfig config, TimeProvider time) : base(host)
    {
        _queueActorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
        _queueActorStateReader = queueActorStateReader ?? throw new ArgumentNullException(nameof(queueActorStateReader));
        _catalogRegistrar = catalogRegistrar ?? throw new ArgumentNullException(nameof(catalogRegistrar));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    protected override async Task OnActivateAsync()
    {
        // A queue that only ever receives session enqueues never activates its own QueueActor, so
        // the coordinator (same id) registers it in the catalog.
        _catalogRegistrar.Register(new CatalogEntry { Id = Id.GetId(), Kind = CatalogEntryKind.Queue, RegisteredAt = DateTimeOffset.UtcNow });

        var metadataExists = await StateManager.TryGetStateAsync<SessionCoordinatorMetadata>(MetadataKey);
        if (!metadataExists.HasValue)
        {
            await StateManager.SetStateAsync(MetadataKey, new SessionCoordinatorMetadata());
            await StateManager.SetStateAsync(SummaryKey, new DirectorySummary());
            await StateManager.SaveStateAsync();
            Logger.LogDebug("SessionCoordinatorActor activated and metadata initialized");
            return;
        }

        double now = Now();
        foreach (var (sessionId, entry) in metadataExists.Value.SessionDirectory)
        {
            if (entry.Active)
            {
                _lastActiveAt[sessionId] = now;
            }
        }

        if (metadataExists.Value.SessionDirectory.Count > 0)
        {
            await EnsureEvictionReminderAsync();
        }
    }

    /// <inheritdoc />
    public async Task<RegisterSessionResponse> RegisterSession(RegisterSessionRequest request)
    {
        if (string.IsNullOrEmpty(request.SessionId))
        {
            return new RegisterSessionResponse { Success = false };
        }

        await ApplyChangesAsync([new SessionChange { SessionId = request.SessionId, HasWork = true }]);
        return new RegisterSessionResponse { Success = true };
    }

    /// <inheritdoc />
    public async Task<ReportSessionChangesResponse> ReportSessionChanges(ReportSessionChangesRequest request)
    {
        var deferred = await ApplyChangesAsync(request.Changes);
        return new ReportSessionChangesResponse { Success = true, Deferred = deferred };
    }

    /// <summary>
    /// Active is always applied: a wrong Active heals at the next claim or lease end. Empty is the
    /// one that can strand work, so it is deferred if the session was marked Active within the
    /// deferral window - it may have been sent before that registration and arrived after it.
    /// </summary>
    private async Task<List<string>> ApplyChangesAsync(IEnumerable<SessionChange> changes)
    {
        double now = Now();
        var metadata = await GetMetadataAsync();
        var summary = await GetSummaryAsync();
        var directory = new Dictionary<string, SessionEntry>(metadata.SessionDirectory);
        var ready = new List<string>(metadata.ReadyQueue);
        var deferred = new List<string>();
        bool changed = false;

        foreach (var change in changes)
        {
            if (string.IsNullOrEmpty(change.SessionId))
            {
                continue;
            }

            directory.TryGetValue(change.SessionId, out var entry);
            if (change.HasWork)
            {
                _lastActiveAt[change.SessionId] = now;
                if (entry is { Active: true })
                {
                    continue;
                }

                // An unknown session is added. This also covers an enqueue into a session actor that
                // is still in memory after its entry was evicted.
                if (entry == null)
                {
                    summary = summary with { TotalCount = summary.TotalCount + 1 };
                }

                entry = await MarkActiveAsync(entry ?? new SessionEntry(), change.SessionId, ready, now, neverClaimed: entry == null);
                summary = summary with { ActiveCount = summary.ActiveCount + 1 };
            }
            else
            {
                if (entry is not { Active: true })
                {
                    continue; // unknown or already Idle: nothing to do
                }

                if (_lastActiveAt.TryGetValue(change.SessionId, out var at) && now - at < _config.DeferralWindow.TotalSeconds)
                {
                    deferred.Add(change.SessionId);
                    continue;
                }

                entry = MarkIdle(entry, now);
                summary = summary with { ActiveCount = summary.ActiveCount - 1 };
            }

            directory[change.SessionId] = entry;
            changed = true;
        }

        PruneLastActiveAt(now);

        if (changed)
        {
            await SetDirectoryAsync(metadata with { SessionDirectory = directory, ReadyQueue = ready }, summary);
            await StateManager.SaveStateAsync();
            await EnsureEvictionReminderAsync();
        }

        return deferred;
    }

    /// <inheritdoc />
    public async Task<AcceptSessionResponse> AcceptSession(AcceptSessionRequest request)
    {
        var result = await AcceptCoreAsync(request);
        await StateManager.SaveStateAsync();
        return result;
    }

    // Every state change a turn makes is saved together, once, at the end of the turn (see CLAUDE.md:
    // one save per turn). The cores below stage changes but never save.
    private async Task<AcceptSessionResponse> AcceptCoreAsync(AcceptSessionRequest request)
    {
        int leaseSeconds = Math.Max(MinLeaseSeconds, Math.Min(MaxLeaseSeconds, request.LeaseSeconds));

        if (!string.IsNullOrEmpty(request.SessionId))
        {
            return await AcceptTargetedAsync(request.SessionId, leaseSeconds);
        }

        return await AcceptAnyAvailableAsync(leaseSeconds);
    }

    private async Task<AcceptSessionResponse> AcceptTargetedAsync(string sessionId, int leaseSeconds)
    {
        var metadata = await GetMetadataAsync();
        if (!metadata.SessionDirectory.ContainsKey(sessionId) && !await TryAddFromSessionStateAsync(sessionId))
        {
            return new AcceptSessionResponse
            {
                Success = false,
                ErrorCode = "SESSION_NOT_FOUND",
                ErrorMessage = $"Session '{sessionId}' is not known to this queue"
            };
        }

        if (await IsLeasedAsync(sessionId, Now()))
        {
            return new AcceptSessionResponse
            {
                Success = false,
                ErrorCode = "SESSION_LOCKED",
                ErrorMessage = $"Session '{sessionId}' is currently claimed by another consumer"
            };
        }

        // If the session is in the ReadyQueue it stays there, and is dropped as leased when it
        // reaches the head.
        return await ClaimAsync(sessionId, leaseSeconds)
            ?? SessionActorUnavailable(sessionId);
    }

    /// <summary>
    /// A session is registered by its worker's reporter shortly after its first enqueue, so a targeted
    /// accept can arrive first. Reads the session's state (no activation) and, if it holds items, adds
    /// it as Active and unqueued, staged for this turn's save. Returns false if it has none or the read
    /// fails.
    /// </summary>
    private async Task<bool> TryAddFromSessionStateAsync(string sessionId)
    {
        SessionActorState state;
        try
        {
            using var timeout = new CancellationTokenSource(_config.LeaseEndCallTimeout);
            state = await _queueActorStateReader.ReadSessionStateAsync(new ActorId(SessionActorId(sessionId)), timeout.Token);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read unregistered session {SessionId}", sessionId);
            return false;
        }

        if (state.ItemCount + state.LockCount == 0)
        {
            return false;
        }

        double now = Now();
        var metadata = await GetMetadataAsync();
        var summary = await GetSummaryAsync();
        _lastActiveAt[sessionId] = now;
        await SetDirectoryAsync(
            metadata with
            {
                SessionDirectory = WithAdded(metadata.SessionDirectory, sessionId,
                    new SessionEntry { Active = true, NextCheckAt = now + _config.Backstop.TotalSeconds })
            },
            summary with { TotalCount = summary.TotalCount + 1, ActiveCount = summary.ActiveCount + 1 });
        await EnsureEvictionReminderAsync();
        return true;
    }

    private static Dictionary<string, SessionEntry> WithAdded(Dictionary<string, SessionEntry> directory, string sessionId, SessionEntry entry) =>
        new(directory) { [sessionId] = entry };

    /// <summary>
    /// Takes the ReadyQueue's head. Stale ids (evicted, Idle, leased) are dropped; a session whose
    /// actor can't be reached goes to the tail so it doesn't block the head. Bounded by
    /// MaxClaimAttempts, and never tries the same session twice in one call.
    /// </summary>
    private async Task<AcceptSessionResponse> AcceptAnyAvailableAsync(int leaseSeconds)
    {
        var tried = new HashSet<string>();
        string? unreachable = null;

        for (int attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            var metadata = await GetMetadataAsync();
            if (metadata.ReadyQueue.Count == 0)
            {
                break;
            }

            string sessionId = metadata.ReadyQueue[0];
            if (!tried.Add(sessionId))
            {
                break; // back round to a session that already failed this call
            }

            metadata.SessionDirectory.TryGetValue(sessionId, out var entry);
            if (entry is not { Active: true } || await IsLeasedAsync(sessionId, Now()))
            {
                await SetMetadataAsync(RemoveFromReady(metadata, sessionId));
                continue;
            }

            var claimed = await ClaimAsync(sessionId, leaseSeconds);
            if (claimed != null)
            {
                return claimed;
            }

            // The lease sync is a cross-actor await: re-read before moving the id to the tail.
            var current = RemoveFromReady(await GetMetadataAsync(), sessionId);
            await SetMetadataAsync(current with
            {
                SessionDirectory = WithEntry(current.SessionDirectory, sessionId, e => e with { Queued = true }),
                ReadyQueue = [.. current.ReadyQueue, sessionId]
            });
            unreachable = sessionId;
        }

        if (unreachable != null)
        {
            return SessionActorUnavailable(unreachable);
        }

        return new AcceptSessionResponse
        {
            Success = false,
            ErrorCode = "NO_SESSIONS_AVAILABLE",
            ErrorMessage = "No sessions are currently available to claim"
        };
    }

    /// <summary>
    /// Syncs a new lease onto the session actor, then stages it for the turn's save. Returns null if
    /// the sync failed, in which case nothing is staged.
    /// </summary>
    private async Task<AcceptSessionResponse?> ClaimAsync(string sessionId, int leaseSeconds)
    {
        double now = Now();
        string leaseId = GenerateLeaseId();
        double expiresAt = now + leaseSeconds;

        if (!await TrySyncSessionLeaseAsync(sessionId, leaseId, expiresAt))
        {
            return null;
        }

        await StateManager.SetStateAsync($"session-lock_{sessionId}", new SessionLockState
        {
            SessionId = sessionId,
            LeaseId = leaseId,
            CreatedAt = now,
            ExpiresAt = expiresAt
        });

        // Re-read rather than reusing an earlier copy: the lease sync above is a cross-actor await,
        // and a change that lands during it must not be clobbered by a stale directory.
        var metadata = await GetMetadataAsync();
        if (metadata.ReadyQueue.Count > 0 && metadata.ReadyQueue[0] == sessionId)
        {
            await SetMetadataAsync(RemoveFromReady(metadata, sessionId));
        }

        try
        {
            // Before the turn's save: if that save fails, the reminder finds no lease and does nothing.
            await RegisterReminderAsync(
                LeaseReminderPrefix + sessionId,
                null,
                TimeSpan.FromSeconds(leaseSeconds),
                TimeSpan.FromMilliseconds(-1)); // -1 means fire once
        }
        catch (Exception ex)
        {
            // Scheduler service not available - the eviction pass pushes a session whose lease has
            // lapsed back onto the ReadyQueue instead.
            Logger.LogDebug(ex, "Reminder registration failed for session {SessionId} (scheduler unavailable)", sessionId);
        }

        Logger.LogInformation("Claimed session {SessionId} with lease {LeaseId}, TTL {LeaseSeconds}s", sessionId, leaseId, leaseSeconds);

        return new AcceptSessionResponse
        {
            Success = true,
            SessionId = sessionId,
            LeaseId = leaseId,
            LeaseExpiresAt = expiresAt
        };
    }

    private static AcceptSessionResponse SessionActorUnavailable(string sessionId) => new()
    {
        Success = false,
        ErrorCode = "SESSION_ACTOR_UNAVAILABLE",
        ErrorMessage = $"Failed to sync lease with the queue actor for session '{sessionId}'"
    };

    /// <inheritdoc />
    public async Task<RenewSessionLeaseResponse> RenewSessionLease(RenewSessionLeaseRequest request)
    {
        var existing = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{request.SessionId}");
        double now = Now();

        if (!existing.HasValue || now >= existing.Value.ExpiresAt)
        {
            if (existing.HasValue)
            {
                // Opportunistic cleanup of a stale (expired but not yet reaped) record.
                await StateManager.RemoveStateAsync($"session-lock_{request.SessionId}");
                await StateManager.SaveStateAsync();
            }

            return new RenewSessionLeaseResponse
            {
                Success = false,
                ErrorCode = "SESSION_LEASE_EXPIRED",
                ErrorMessage = $"No active lease for session '{request.SessionId}'"
            };
        }

        var lockData = existing.Value;
        if (lockData.LeaseId != request.LeaseId)
        {
            return new RenewSessionLeaseResponse
            {
                Success = false,
                ErrorCode = "INVALID_LEASE_ID",
                ErrorMessage = "leaseId does not match the current lease holder"
            };
        }

        double newExpiresAt = lockData.ExpiresAt + request.AdditionalSeconds;

        bool synced = await TrySyncSessionLeaseAsync(request.SessionId, lockData.LeaseId, newExpiresAt);
        if (!synced)
        {
            return new RenewSessionLeaseResponse
            {
                Success = false,
                ErrorCode = "SESSION_ACTOR_UNAVAILABLE",
                ErrorMessage = $"Failed to sync lease with the queue actor for session '{request.SessionId}'"
            };
        }

        // The claim's expiry reminder is left as it is: when it fires, it finds the later expiry and
        // re-arms for the time left (ExpireLeaseAsync), so a renewal makes no scheduler calls.
        var updated = lockData with { ExpiresAt = newExpiresAt };
        await StateManager.SetStateAsync($"session-lock_{request.SessionId}", updated);
        await StateManager.SaveStateAsync();

        Logger.LogDebug("Renewed session {SessionId} lease, new expiry {NewExpiresAt}", request.SessionId, newExpiresAt);

        return new RenewSessionLeaseResponse
        {
            Success = true,
            NewExpiresAt = newExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<ReleaseSessionResponse> ReleaseSession(ReleaseSessionRequest request)
    {
        var result = await ReleaseCoreAsync(request);
        await StateManager.SaveStateAsync();
        return result;
    }

    private async Task<ReleaseSessionResponse> ReleaseCoreAsync(ReleaseSessionRequest request)
    {
        var existing = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{request.SessionId}");
        if (!existing.HasValue)
        {
            // Idempotent: nothing to release is a success, not an error - a consumer's cleanup
            // path (e.g. after its own heartbeat already lost the race) shouldn't need special-casing.
            return new ReleaseSessionResponse { Success = true };
        }

        if (existing.Value.LeaseId != request.LeaseId)
        {
            return new ReleaseSessionResponse
            {
                Success = false,
                ErrorCode = "INVALID_LEASE_ID",
                ErrorMessage = "leaseId does not match the current lease holder"
            };
        }

        await StateManager.RemoveStateAsync($"session-lock_{request.SessionId}");

        try
        {
            await UnregisterReminderAsync(LeaseReminderPrefix + request.SessionId);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to unregister reminder for session {SessionId}", request.SessionId);
        }

        // Clears the session actor's cached lease and reports what is left in it. If this fails, the
        // cached lease self-heals at its own ActiveSessionLeaseExpiresAt, and the session is treated
        // as still holding items.
        long? remaining = null;
        try
        {
            using var timeout = new CancellationTokenSource(_config.LeaseEndCallTimeout);
            var cleared = await _queueActorInvoker.InvokeMethodAsync<ClearSessionLeaseResponse>(
                new ActorId(SessionActorId(request.SessionId)),
                "ClearSessionLease",
                timeout.Token);
            if (cleared.Success)
            {
                remaining = cleared.Remaining;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to clear synced lease on session actor for {SessionId}", request.SessionId);
        }

        await ApplyLeaseEndAsync(request.SessionId, remaining);

        Logger.LogDebug("Released session {SessionId}", request.SessionId);

        return new ReleaseSessionResponse { Success = true };
    }

    public async Task<ReleaseAndAcceptSessionResponse> ReleaseAndAcceptSession(ReleaseAndAcceptSessionRequest request)
    {
        var release = await ReleaseCoreAsync(new ReleaseSessionRequest { SessionId = request.SessionId, LeaseId = request.LeaseId });
        var accept = await AcceptCoreAsync(new AcceptSessionRequest { LeaseSeconds = request.LeaseSeconds });
        await StateManager.SaveStateAsync();
        return new ReleaseAndAcceptSessionResponse { Release = release, Accept = accept };
    }

    /// <summary>
    /// Reminder callback: lease expiry ("session-{id}"), and the eviction pass (periodic and
    /// catch-up).
    /// </summary>
    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        try
        {
            if (reminderName == EvictionReminderName || reminderName == CatchUpReminderName)
            {
                await EvictionPassAsync();
            }
            else if (reminderName.StartsWith(LeaseReminderPrefix))
            {
                await ExpireLeaseAsync(reminderName[LeaseReminderPrefix.Length..]);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in ReceiveReminderAsync for reminder {ReminderName}", reminderName);
        }
    }

    private async Task ExpireLeaseAsync(string sessionId)
    {
        var existing = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{sessionId}");
        if (!existing.HasValue)
        {
            // Already released - the release did the lease-end bookkeeping.
            Logger.LogDebug("Session {SessionId} lease already released, skipping cleanup", sessionId);
            return;
        }

        double timeLeft = existing.Value.ExpiresAt - Now();
        if (timeLeft > 0)
        {
            // Renewed since this reminder was set: wait for the new expiry instead. If the scheduler
            // is unavailable, the eviction pass requeues the session once the lease lapses.
            try
            {
                await RegisterReminderAsync(LeaseReminderPrefix + sessionId, null, TimeSpan.FromSeconds(timeLeft), TimeSpan.FromMilliseconds(-1));
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to re-arm the expiry reminder for renewed session {SessionId}", sessionId);
            }
            return;
        }

        await StateManager.RemoveStateAsync($"session-lock_{sessionId}");
        Logger.LogDebug("Session {SessionId} lease auto-expired and cleaned up", sessionId);

        long? remaining = null;
        try
        {
            using var timeout = new CancellationTokenSource(_config.LeaseEndCallTimeout);
            var sessionState = await _queueActorStateReader.ReadSessionStateAsync(new ActorId(SessionActorId(sessionId)), timeout.Token);
            remaining = (long)sessionState.ItemCount + sessionState.LockCount;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to read session {SessionId} after its lease expired; treating it as holding items", sessionId);
        }

        await ApplyLeaseEndAsync(sessionId, remaining);
        await StateManager.SaveStateAsync();
    }

    /// <summary>
    /// A lease has ended. Nothing left makes the entry Idle; items left (or an unknown count, which
    /// errs toward availability) put the session back on the ReadyQueue. Stages the change; the
    /// caller's turn saves it.
    /// </summary>
    private async Task ApplyLeaseEndAsync(string sessionId, long? remaining)
    {
        double now = Now();
        var metadata = await GetMetadataAsync();
        var summary = await GetSummaryAsync();
        var directory = new Dictionary<string, SessionEntry>(metadata.SessionDirectory);
        var ready = new List<string>(metadata.ReadyQueue);
        directory.TryGetValue(sessionId, out var entry);

        if (remaining == 0)
        {
            if (entry is not { Active: true })
            {
                return;
            }

            directory[sessionId] = MarkIdle(entry, now);
            summary = summary with { ActiveCount = summary.ActiveCount - 1 };
        }
        else
        {
            if (entry == null)
            {
                summary = summary with { TotalCount = summary.TotalCount + 1 };
            }

            if (entry is not { Active: true })
            {
                summary = summary with { ActiveCount = summary.ActiveCount + 1 };
                _lastActiveAt[sessionId] = now;
            }

            directory[sessionId] = await MarkActiveAsync(entry ?? new SessionEntry(), sessionId, ready, now);
        }

        await SetDirectoryAsync(metadata with { SessionDirectory = directory, ReadyQueue = ready }, summary);
        await EnsureEvictionReminderAsync();
    }

    /// <summary>
    /// Reads due entries and evicts, promotes, demotes or backs them off. Bounded in time
    /// (MaxPassDuration, excluding its one save) and in reads (MaxReadsPerPass); a pass that stops
    /// with entries still due schedules a catch-up pass rather than looping inside the turn.
    /// Entries whose read didn't finish are left exactly as they were.
    /// </summary>
    private async Task EvictionPassAsync()
    {
        double now = Now();
        var summary = await GetSummaryAsync();
        if (summary.TotalCount == 0)
        {
            await UnregisterEvictionReminderAsync();
            return;
        }

        // Real time, not _time: the cap is about how long callers actually wait on this turn.
        using var deadline = new CancellationTokenSource(_config.MaxPassDuration);
        var metadata = await GetMetadataAsync();
        var directory = new Dictionary<string, SessionEntry>(metadata.SessionDirectory);
        var ready = new List<string>(metadata.ReadyQueue);
        bool changed = false;
        bool stoppedEarly = false;
        var toRead = new List<string>();

        try
        {
            // Repair: an Active session whose lease lapsed without its expiry reminder firing is
            // pushed back onto the ReadyQueue. No read: a wrong push heals at the next lease end.
            foreach (var (sessionId, entry) in metadata.SessionDirectory.Where(e => e.Value is { Active: true, Queued: false }))
            {
                if (!await IsLeasedAsync(sessionId, now, deadline.Token))
                {
                    ready.Add(sessionId);
                    directory[sessionId] = entry with { Queued = true };
                    changed = true;
                }
            }

            var due = directory.Where(e => e.Value.NextCheckAt <= now).OrderBy(e => e.Value.NextCheckAt).Select(e => e.Key).ToList();
            foreach (var sessionId in due)
            {
                if (toRead.Count >= _config.MaxReadsPerPass)
                {
                    stoppedEarly = true;
                    break;
                }

                var entry = directory[sessionId];
                if (await IsLeasedAsync(sessionId, now, deadline.Token))
                {
                    // A lease end decides a leased session; look again later.
                    var later = entry.Active ? _config.Backstop : _config.Grace;
                    directory[sessionId] = entry with { NextCheckAt = now + later.TotalSeconds };
                    changed = true;
                    continue;
                }

                toRead.Add(sessionId);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            stoppedEarly = true;
        }

        var results = await ReadSessionsAsync(toRead, deadline.Token);
        if (results.Count < toRead.Count)
        {
            stoppedEarly = true;
        }

        foreach (var (sessionId, sessionState) in results)
        {
            var entry = directory[sessionId];
            bool hasWork = !sessionState.IsEmpty;

            if (!entry.Active && !hasWork)
            {
                directory.Remove(sessionId);
                ready.Remove(sessionId);
                _lastActiveAt.Remove(sessionId);
                summary = summary with { TotalCount = summary.TotalCount - 1 };
                Logger.LogDebug("Evicted idle session {SessionId} from directory", sessionId);
            }
            else if (!entry.Active)
            {
                // A lost Active report: promote it.
                directory[sessionId] = await MarkActiveAsync(entry, sessionId, ready, now);
                _lastActiveAt[sessionId] = now;
                summary = summary with { ActiveCount = summary.ActiveCount + 1 };
            }
            else if (!hasWork)
            {
                // A lost Empty report: demote it.
                directory[sessionId] = MarkIdle(entry, now);
                summary = summary with { ActiveCount = summary.ActiveCount - 1 };
            }
            else
            {
                // Still has items: double the interval since the last read (1 h, 2 h, 4 h, ... capped).
                double backoff = Math.Min(
                    Math.Max(entry.BackoffSeconds, _config.Backstop.TotalSeconds) * 2,
                    _config.MaxBackstop.TotalSeconds);
                var backedOff = entry with { NextCheckAt = now + backoff, BackoffSeconds = backoff };
                if (!backedOff.Queued)
                {
                    ready.Add(sessionId);
                    backedOff = backedOff with { Queued = true };
                }
                directory[sessionId] = backedOff;
            }

            changed = true;
        }

        if (changed)
        {
            await SetDirectoryAsync(metadata with { SessionDirectory = directory, ReadyQueue = ready }, summary);
            await StateManager.SaveStateAsync();
        }

        if (summary.TotalCount == 0)
        {
            await UnregisterEvictionReminderAsync();
        }
        else if (stoppedEarly)
        {
            await ScheduleCatchUpAsync();
        }
    }

    /// <summary>
    /// Reads sessions' persisted state in parallel, straight from the state store (no activation).
    /// Returns only the reads that finished before the deadline; a failed read has no result.
    /// </summary>
    private async Task<Dictionary<string, SessionActorState>> ReadSessionsAsync(List<string> sessionIds, CancellationToken deadline)
    {
        var results = new ConcurrentDictionary<string, SessionActorState>();
        if (sessionIds.Count == 0)
        {
            return new Dictionary<string, SessionActorState>();
        }

        try
        {
            await Parallel.ForEachAsync(
                sessionIds,
                new ParallelOptions { MaxDegreeOfParallelism = _config.ReadParallelism, CancellationToken = deadline },
                async (sessionId, ct) =>
                {
                    try
                    {
                        results[sessionId] = await _queueActorStateReader.ReadSessionStateAsync(new ActorId(SessionActorId(sessionId)), ct);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // Inconclusive - fail closed, leave the entry as it was.
                        Logger.LogDebug(ex, "Failed to read session {SessionId} during the eviction pass; leaving as-is", sessionId);
                    }
                });
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Deadline hit: in-flight reads were aborted, finished ones are kept.
        }

        return new Dictionary<string, SessionActorState>(results);
    }

    /// <param name="neverClaimed">The session had no entry, so no claim can have leased it: skip the
    /// lease read. If it were leased after all, an any-available claim drops it at the head.</param>
    private async Task<SessionEntry> MarkActiveAsync(SessionEntry entry, string sessionId, List<string> ready, double now, bool neverClaimed = false)
    {
        var active = entry with
        {
            Active = true,
            IdleSince = null,
            BackoffSeconds = 0,
            NextCheckAt = now + _config.Backstop.TotalSeconds
        };

        if (!active.Queued && (neverClaimed || !await IsLeasedAsync(sessionId, now)))
        {
            ready.Add(sessionId);
            active = active with { Queued = true };
        }

        return active;
    }

    private SessionEntry MarkIdle(SessionEntry entry, double now) => entry with
    {
        Active = false,
        IdleSince = now,
        BackoffSeconds = 0,
        NextCheckAt = now + _config.Grace.TotalSeconds
    };

    private async Task<bool> IsLeasedAsync(string sessionId, double now, CancellationToken cancellationToken = default)
    {
        var lease = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{sessionId}", cancellationToken);
        return lease.HasValue && now < lease.Value.ExpiresAt;
    }

    private static SessionCoordinatorMetadata RemoveFromReady(SessionCoordinatorMetadata metadata, string sessionId)
    {
        var ready = new List<string>(metadata.ReadyQueue);
        ready.Remove(sessionId);
        return metadata with
        {
            ReadyQueue = ready,
            SessionDirectory = WithEntry(metadata.SessionDirectory, sessionId, e => e with { Queued = false })
        };
    }

    private static Dictionary<string, SessionEntry> WithEntry(
        Dictionary<string, SessionEntry> directory, string sessionId, Func<SessionEntry, SessionEntry> update)
    {
        var copy = new Dictionary<string, SessionEntry>(directory);
        if (copy.TryGetValue(sessionId, out var entry))
        {
            copy[sessionId] = update(entry);
        }
        return copy;
    }

    private void PruneLastActiveAt(double now)
    {
        double window = _config.DeferralWindow.TotalSeconds;
        foreach (var sessionId in _lastActiveAt.Where(e => now - e.Value >= window).Select(e => e.Key).ToList())
        {
            _lastActiveAt.Remove(sessionId);
        }
    }

    /// <summary>
    /// Registers the periodic eviction reminder once per activation. Re-registering on every change
    /// would keep resetting its due time, so under steady changes it would never fire.
    /// </summary>
    private async Task EnsureEvictionReminderAsync()
    {
        if (_evictionReminderRegistered)
        {
            return;
        }

        try
        {
            await RegisterReminderAsync(EvictionReminderName, null, _config.EvictionPeriod, _config.EvictionPeriod);
            _evictionReminderRegistered = true;
        }
        catch (Exception ex)
        {
            // Scheduler service not available - entries just aren't evicted until a later change
            // or activation manages to register it. Delayed cleanup only.
            Logger.LogDebug(ex, "Eviction reminder registration failed (scheduler unavailable)");
        }
    }

    private async Task UnregisterEvictionReminderAsync()
    {
        try
        {
            await UnregisterReminderAsync(EvictionReminderName);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to unregister the eviction reminder");
        }
        _evictionReminderRegistered = false;
    }

    private async Task ScheduleCatchUpAsync()
    {
        try
        {
            await RegisterReminderAsync(CatchUpReminderName, null, _config.CatchUpDelay, TimeSpan.FromMilliseconds(-1));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Catch-up reminder registration failed; the backlog drains at the normal period");
        }
    }

    /// <summary>
    /// Syncs a lease onto the target session's own QueueActor instance before this actor commits
    /// to it - see the plan's "sync ordering" invariant: the session actor must agree before
    /// AcceptSession/RenewSessionLease report success, never after.
    /// </summary>
    private async Task<bool> TrySyncSessionLeaseAsync(string sessionId, string leaseId, double expiresAt)
    {
        try
        {
            var result = await _queueActorInvoker.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                new ActorId(SessionActorId(sessionId)),
                "SetSessionLease",
                new SetSessionLeaseRequest { LeaseId = leaseId, ExpiresAt = expiresAt });
            return result.Success;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to sync session lease for {SessionId}", sessionId);
            return false;
        }
    }

    private string SessionActorId(string sessionId) => $"{Id.GetId()}{SessionActorIdMarker}{sessionId}";

    private double Now() => _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// Generate a cryptographically secure 11-character alphanumeric lease ID.
    /// </summary>
    private string GenerateLeaseId()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = new byte[LeaseIdLength];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        var result = new StringBuilder(LeaseIdLength);
        foreach (var b in bytes)
        {
            result.Append(chars[b % chars.Length]);
        }

        return result.ToString();
    }

    private async Task<SessionCoordinatorMetadata> GetMetadataAsync()
    {
        var result = await StateManager.TryGetStateAsync<SessionCoordinatorMetadata>(MetadataKey);
        // Metadata is guaranteed to exist - initialized in OnActivateAsync before any methods are called
        return result.Value;
    }

    private async Task<DirectorySummary> GetSummaryAsync()
    {
        var result = await StateManager.TryGetStateAsync<DirectorySummary>(SummaryKey);
        return result.HasValue ? result.Value : new DirectorySummary();
    }

    private async Task SetMetadataAsync(SessionCoordinatorMetadata metadata)
    {
        await StateManager.SetStateAsync(MetadataKey, metadata);
    }

    /// <summary>"metadata" and "dir_summary" are always staged together, so one save commits both.</summary>
    private async Task SetDirectoryAsync(SessionCoordinatorMetadata metadata, DirectorySummary summary)
    {
        await StateManager.SetStateAsync(MetadataKey, metadata);
        await StateManager.SetStateAsync(SummaryKey, summary);
    }
}
