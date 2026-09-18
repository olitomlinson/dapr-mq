using System.Security.Cryptography;
using System.Text;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// State for SessionCoordinatorActor. Deliberately minimal - no queue storage, no segments, no
/// priorities. Just which session ids are known for this queue.
/// </summary>
public record SessionCoordinatorMetadata
{
    public List<string> SessionDirectory { get; init; } = new();
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

    private const int MinLeaseSeconds = 1;
    private const int MaxLeaseSeconds = 300;
    private const int LeaseIdLength = 11;

    // Same naming convention QueueActor uses to recognize a session actor's id
    // ("{queueId}-session-{sessionId}") - here used to build that id from this actor's own id
    // (which is the queueId, since SessionCoordinatorActor and its QueueActor share an id string).
    private const string SessionActorIdMarker = "-session-";

    public SessionCoordinatorActor(ActorHost host, IQueueActorInvoker queueActorInvoker) : base(host)
    {
        _queueActorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
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
    }

    /// <inheritdoc />
    public async Task<RegisterSessionResponse> RegisterSession(RegisterSessionRequest request)
    {
        if (string.IsNullOrEmpty(request.SessionId))
        {
            return new RegisterSessionResponse { Success = false };
        }

        var metadata = await GetMetadataAsync();
        if (!metadata.SessionDirectory.Contains(request.SessionId))
        {
            metadata = metadata with { SessionDirectory = metadata.SessionDirectory.Append(request.SessionId).ToList() };
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
            if (!metadata.SessionDirectory.Contains(request.SessionId))
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
            foreach (var candidate in metadata.SessionDirectory)
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
            if (reminderName.StartsWith("session-"))
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
