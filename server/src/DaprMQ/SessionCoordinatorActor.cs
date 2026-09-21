using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// State for SessionCoordinatorActor. Deliberately minimal - no queue storage, no segments, no
/// priorities. Just which session ids are known for this queue, plus each one's directory-sweep
/// scheduling data (see SweepCandidate) - kept in the same dictionary rather than a second
/// collection so membership and sweep bookkeeping can never drift out of sync, and so the sweep
/// can rank every entry from the one read it already does each tick.
/// </summary>
public record SessionCoordinatorMetadata
{
    public Dictionary<string, SweepCandidate> SessionDirectory { get; init; } = new();
}

/// <summary>
/// Per-session bookkeeping for SessionCoordinatorActor's directory-sweep reminder. NextCheckAt
/// doubles as both the eligibility filter (a candidate is skipped until this time) and the
/// priority order (ascending - a freshly-registered entry's default 0 sorts first, i.e. an
/// unchecked session is maximally overdue), so the sweep can pick a fair batch without reading
/// anything beyond the one metadata blob it already loads.
/// </summary>
public record SweepCandidate
{
    /// <summary>
    /// Set the first time a sweep tick finds this session empty with no active lease; cleared if
    /// a later tick finds it non-empty again. A second consecutive empty confirmation (this
    /// already set) is what triggers eviction - a single reading isn't trusted, to narrow the
    /// window where a producer's enqueue could race the directory removal.
    /// </summary>
    public double? FirstConfirmedEmptyAt { get; init; }

    public double NextCheckAt { get; init; }

    /// <summary>
    /// Current backoff applied after a "not empty" result, doubled (capped) each time it recurs,
    /// so a session that's legitimately in steady use - or has an abandoned, never-claimed
    /// backlog - doesn't get re-checked at full sweep frequency forever.
    /// </summary>
    public double BackoffSeconds { get; init; }
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
/// SessionCoordinatorActor - tracks the set of known session ids for a logical queue, and which
/// are currently leased to a consumer. See ISessionCoordinatorActor for the delegation rationale.
/// </summary>
public class SessionCoordinatorActor : Actor, ISessionCoordinatorActor, IRemindable
{
    private readonly IQueueActorInvoker _queueActorInvoker;
    private readonly IQueueActorStateReader _queueActorStateReader;

    private const int MinLeaseSeconds = 1;
    private const int MaxLeaseSeconds = 300;
    private const int LeaseIdLength = 11;

    // Same naming convention QueueActor uses to recognize a session actor's id
    // ("{queueId}-session-{sessionId}") - here used to build that id from this actor's own id
    // (which is the queueId, since SessionCoordinatorActor and its QueueActor share an id string).
    private const string SessionActorIdMarker = "-session-";

    private const string SweepReminderName = "directory-sweep";
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private const int SweepBatchSize = 200;
    private const double InitialBackoffSeconds = 3600; // 1 hour
    private const double MaxBackoffSeconds = 86400; // 1 day, capped
    private const double SecondPassDelaySeconds = 300; // re-check soon for the second confirmation
    private const double LeaseSkipRecheckSeconds = 3600; // don't let a leased session dominate every batch

    public SessionCoordinatorActor(ActorHost host, IQueueActorInvoker queueActorInvoker, IQueueActorStateReader queueActorStateReader) : base(host)
    {
        _queueActorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
        _queueActorStateReader = queueActorStateReader ?? throw new ArgumentNullException(nameof(queueActorStateReader));
    }

    protected override async Task OnActivateAsync()
    {
        var metadataExists = await StateManager.TryGetStateAsync<SessionCoordinatorMetadata>("metadata");
        if (!metadataExists.HasValue)
        {
            await StateManager.SetStateAsync("metadata", new SessionCoordinatorMetadata());
            await StateManager.SaveStateAsync();
            Logger.LogDebug("SessionCoordinatorActor activated and metadata initialized");
        }
        else
        {
            Logger.LogDebug("SessionCoordinatorActor activated with existing metadata");
        }

        try
        {
            await RegisterReminderAsync(
                SweepReminderName,
                null,
                TimeSpan.Zero,
                SweepInterval);
        }
        catch (Exception ex)
        {
            // Scheduler service not available - the directory just doesn't get swept until a
            // later activation manages to register it, matching this actor's other reminders'
            // degradation. Not a correctness issue, only delayed cleanup.
            Logger.LogDebug(ex, "Directory-sweep reminder registration failed (scheduler unavailable)");
        }
    }

    /// <inheritdoc />
    public async Task<RegisterSessionResponse> RegisterSession(RegisterSessionRequest request)
    {
        if (string.IsNullOrEmpty(request.SessionId))
        {
            return new RegisterSessionResponse { Success = false };
        }

        var metadata = await GetMetadataAsync();
        if (!metadata.SessionDirectory.ContainsKey(request.SessionId))
        {
            var updatedDirectory = new Dictionary<string, SweepCandidate>(metadata.SessionDirectory)
            {
                [request.SessionId] = new SweepCandidate()
            };
            metadata = metadata with { SessionDirectory = updatedDirectory };
            await SetMetadataAsync(metadata);
            await StateManager.SaveStateAsync();
            Logger.LogDebug("Registered session {SessionId} in directory", request.SessionId);
        }

        return new RegisterSessionResponse { Success = true };
    }

    /// <inheritdoc />
    public async Task<AcceptSessionResponse> AcceptSession(AcceptSessionRequest request)
    {
        var metadata = await GetMetadataAsync();
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int leaseSeconds = Math.Max(MinLeaseSeconds, Math.Min(MaxLeaseSeconds, request.LeaseSeconds));

        string sessionId;
        if (!string.IsNullOrEmpty(request.SessionId))
        {
            // Targeted claim.
            if (!metadata.SessionDirectory.ContainsKey(request.SessionId))
            {
                return new AcceptSessionResponse
                {
                    Success = false,
                    ErrorCode = "SESSION_NOT_FOUND",
                    ErrorMessage = $"Session '{request.SessionId}' is not known to this queue"
                };
            }

            var existingLock = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{request.SessionId}");
            if (existingLock.HasValue && now < existingLock.Value.ExpiresAt)
            {
                return new AcceptSessionResponse
                {
                    Success = false,
                    ErrorCode = "SESSION_LOCKED",
                    ErrorMessage = $"Session '{request.SessionId}' is currently claimed by another consumer"
                };
            }

            sessionId = request.SessionId;
        }
        else
        {
            // Any-available claim: first directory entry with no live lease.
            string? found = null;
            foreach (var candidate in metadata.SessionDirectory.Keys)
            {
                var candidateLock = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{candidate}");
                if (!candidateLock.HasValue || now >= candidateLock.Value.ExpiresAt)
                {
                    found = candidate;
                    break;
                }
            }

            if (found == null)
            {
                return new AcceptSessionResponse
                {
                    Success = false,
                    ErrorCode = "NO_SESSIONS_AVAILABLE",
                    ErrorMessage = "No sessions are currently available to claim"
                };
            }

            sessionId = found;
        }

        string leaseId = GenerateLeaseId();
        double expiresAt = now + leaseSeconds;

        bool synced = await TrySyncSessionLeaseAsync(sessionId, leaseId, expiresAt);
        if (!synced)
        {
            return new AcceptSessionResponse
            {
                Success = false,
                ErrorCode = "SESSION_ACTOR_UNAVAILABLE",
                ErrorMessage = $"Failed to sync lease with the queue actor for session '{sessionId}'"
            };
        }

        var lockState = new SessionLockState
        {
            SessionId = sessionId,
            LeaseId = leaseId,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };
        await StateManager.SetStateAsync($"session-lock_{sessionId}", lockState);
        await StateManager.SaveStateAsync();

        try
        {
            await RegisterReminderAsync(
                $"session-{sessionId}",
                null,
                TimeSpan.FromSeconds(leaseSeconds),
                TimeSpan.FromMilliseconds(-1)); // -1 means fire once
        }
        catch (Exception ex)
        {
            // Scheduler service not available - lease expiry will rely on manual checks, matching
            // QueueActor's item-lock reminder degradation.
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

    /// <inheritdoc />
    public async Task<RenewSessionLeaseResponse> RenewSessionLease(RenewSessionLeaseRequest request)
    {
        var existing = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{request.SessionId}");
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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

        var updated = lockData with { ExpiresAt = newExpiresAt };
        await StateManager.SetStateAsync($"session-lock_{request.SessionId}", updated);
        await StateManager.SaveStateAsync();

        try
        {
            await UnregisterReminderAsync($"session-{request.SessionId}");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to unregister old reminder for session {SessionId}", request.SessionId);
        }

        try
        {
            double newTtlSeconds = newExpiresAt - now;
            if (newTtlSeconds > 0)
            {
                await RegisterReminderAsync(
                    $"session-{request.SessionId}",
                    null,
                    TimeSpan.FromSeconds(newTtlSeconds),
                    TimeSpan.FromMilliseconds(-1));
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to register updated reminder for session {SessionId}", request.SessionId);
        }

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
        await StateManager.SaveStateAsync();

        try
        {
            await UnregisterReminderAsync($"session-{request.SessionId}");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to unregister reminder for session {SessionId}", request.SessionId);
        }

        // Best-effort - if this fails, the session actor's cached lease self-heals at its own
        // ActiveSessionLeaseExpiresAt (§2.3 of the plan).
        try
        {
            string sessionActorId = $"{Id.GetId()}{SessionActorIdMarker}{request.SessionId}";
            await _queueActorInvoker.InvokeMethodAsync<ClearSessionLeaseResponse>(
                new ActorId(sessionActorId),
                "ClearSessionLease");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to clear synced lease on session actor for {SessionId}", request.SessionId);
        }

        Logger.LogDebug("Released session {SessionId}", request.SessionId);

        return new ReleaseSessionResponse { Success = true };
    }

    /// <summary>
    /// Reminder callback for auto-expiring session leases. Simpler than QueueActor's item-lock
    /// reminder - a session lease never holds item payload, so there's nothing to requeue, just
    /// the lease record to delete.
    /// </summary>
    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        try
        {
            if (reminderName == SweepReminderName)
            {
                await SweepDirectoryAsync();
            }
            else if (reminderName.StartsWith("session-"))
            {
                string sessionId = reminderName["session-".Length..];
                var existing = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{sessionId}");
                if (existing.HasValue)
                {
                    await StateManager.RemoveStateAsync($"session-lock_{sessionId}");
                    await StateManager.SaveStateAsync();
                    Logger.LogDebug("Session {SessionId} lease auto-expired and cleaned up", sessionId);
                }
                else
                {
                    // Already released - reminder not unregistered. No action needed.
                    Logger.LogDebug("Session {SessionId} lease already released, skipping cleanup", sessionId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in ReceiveReminderAsync for reminder {ReminderName}", reminderName);
        }
    }

    /// <summary>
    /// Reminder callback for the periodic directory sweep. Picks a bounded, fairly-ranked batch of
    /// candidates due for a check, evicts ones confirmed empty (no active lease, no items) on two
    /// consecutive ticks, and leaves everything else untouched or backed off. See SweepCandidate
    /// for what each field means and why the eviction requires two confirmations, not one.
    /// </summary>
    private async Task SweepDirectoryAsync()
    {
        var metadata = await GetMetadataAsync();
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var batch = metadata.SessionDirectory
            .Where(kv => kv.Value.NextCheckAt <= now)
            .OrderBy(kv => kv.Value.NextCheckAt)
            .Take(SweepBatchSize)
            .Select(kv => kv.Key)
            .ToList();

        if (batch.Count == 0)
        {
            return;
        }

        var updatedDirectory = new Dictionary<string, SweepCandidate>(metadata.SessionDirectory);

        foreach (var sessionId in batch)
        {
            var candidate = updatedDirectory[sessionId];

            var leaseState = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{sessionId}");
            if (leaseState.HasValue && now < leaseState.Value.ExpiresAt)
            {
                // In use - free local check, no cross-actor call. Still bump NextCheckAt so a
                // leased session doesn't keep re-winning batch selection every tick.
                updatedDirectory[sessionId] = candidate with { NextCheckAt = now + LeaseSkipRecheckSeconds };
                continue;
            }

            bool isEmpty;
            try
            {
                string sessionActorId = $"{Id.GetId()}{SessionActorIdMarker}{sessionId}";
                isEmpty = await _queueActorStateReader.IsSessionEmptyAsync(new ActorId(sessionActorId));
            }
            catch (Exception ex)
            {
                // Inconclusive read - fail closed, leave the candidate exactly as it was and
                // retry once it's next eligible rather than risk evicting on bad information.
                Logger.LogDebug(ex, "Failed to check emptiness for session {SessionId} during directory sweep; leaving as-is", sessionId);
                continue;
            }

            if (!isEmpty)
            {
                double newBackoff = candidate.BackoffSeconds <= 0
                    ? InitialBackoffSeconds
                    : Math.Min(candidate.BackoffSeconds * 2, MaxBackoffSeconds);
                updatedDirectory[sessionId] = candidate with
                {
                    FirstConfirmedEmptyAt = null,
                    NextCheckAt = now + newBackoff,
                    BackoffSeconds = newBackoff
                };
                continue;
            }

            if (candidate.FirstConfirmedEmptyAt == null)
            {
                // First confirmation - re-check soon rather than evicting on a single reading.
                updatedDirectory[sessionId] = candidate with
                {
                    FirstConfirmedEmptyAt = now,
                    NextCheckAt = now + SecondPassDelaySeconds,
                    BackoffSeconds = 0
                };
            }
            else
            {
                // Second consecutive empty confirmation - safe to evict.
                updatedDirectory.Remove(sessionId);
                Logger.LogDebug("Evicted idle session {SessionId} from directory", sessionId);
            }
        }

        metadata = metadata with { SessionDirectory = updatedDirectory };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();
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
            string sessionActorId = $"{Id.GetId()}{SessionActorIdMarker}{sessionId}";
            var result = await _queueActorInvoker.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                new ActorId(sessionActorId),
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
        var result = await StateManager.TryGetStateAsync<SessionCoordinatorMetadata>("metadata");
        // Metadata is guaranteed to exist - initialized in OnActivateAsync before any methods are called
        return result.Value;
    }

    private async Task SetMetadataAsync(SessionCoordinatorMetadata metadata)
    {
        await StateManager.SetStateAsync("metadata", metadata);
    }
}
