using System.Security.Cryptography;
using System.Text;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using DaprMQ.Interfaces;
using System.Runtime.CompilerServices;

namespace DaprMQ;

/// <summary>
/// Actor metadata containing configuration and queue state.
/// </summary>
public record ActorMetadata
{
    public MetadataConfig Config { get; init; } = new();
    public Dictionary<int, QueueMetadata> Queues { get; init; } = new();
    public string? ErrorMessage { get; init; }
    public int LockCount { get; init; } = 0;

    /// <summary>
    /// Enforcement cache for the currently-active session lease, synced here by
    /// SessionCoordinatorActor via SetSessionLease/ClearSessionLease. Null on a plain (non-session)
    /// queue actor, and null on a session actor whenever no lease is currently held. Checked by the
    /// guard on Dequeue/DequeueLocked/Acknowledge/ExtendLock/DeadLetter.
    /// </summary>
    public string? ActiveSessionLeaseId { get; init; }
    public double? ActiveSessionLeaseExpiresAt { get; init; }

    /// <summary>
    /// Expiry buckets that currently hold at least one outstanding lock id, each the key suffix of a
    /// "locks_exp_{bucket}" state entry. Dapr actor state has no key enumeration, so without this the
    /// sweep could not find a lock it wasn't handed the id for. Only bucket numbers are kept here -
    /// the ids themselves live in the bucket entries, so this stays small (bounded by
    /// MaxLockTtlSeconds / LockBucketWidthSeconds) even with thousands of locks outstanding, which
    /// matters because every operation reads this blob.
    ///
    /// Always empty on a session actor: its locks are scoped to the session lease rather than to a
    /// per-item expiry, so they are indexed in the single "locks_session" entry instead.
    /// </summary>
    public List<long> LockExpiryBuckets { get; init; } = new();

    /// <summary>
    /// Next value to stamp on an enqueued item, incremented once per item. Actor-wide rather than
    /// per-priority: the count-desync repair in DequeueWithPriorityAsync drops a priority's
    /// QueueMetadata outright, which would reset a counter living there and reissue numbers already
    /// in use. Sparse within any one priority as a result, which is fine - the restore only ever
    /// compares these, never indexes by them.
    /// </summary>
    public long NextSequence { get; init; } = 0;
}

/// <summary>
/// Configuration settings for the actor.
/// </summary>
public record MetadataConfig
{
    public int SegmentSize { get; init; } = 100;
    public int BufferSegments { get; init; } = 1;
    public bool DedupEnabled { get; init; } = true;
}

/// <summary>
/// Metadata for a single priority queue.
/// </summary>
public record QueueMetadata
{
    public int HeadSegment { get; init; }
    public int TailSegment { get; init; }
    public int Count { get; init; }
    public int? HeadOffloadedSegment { get; init; }
    public int? TailOffloadedSegment { get; init; }
}

/// <summary>
/// Queue segment item containing item data.
/// </summary>
public record QueueSegmentItem
{
    public required string ItemJson { get; init; }

    /// <summary>
    /// Monotonic position stamp, taken from ActorMetadata.NextSequence at enqueue and never
    /// reassigned - a restored item keeps the number it was given. Within a priority, queue order is
    /// Sequence order, which is what lets an expired lock's item be merged back into the exact
    /// position it left from regardless of how the expiries were batched. Required rather than
    /// defaulted: an unstamped item would silently sort equal to every other unstamped item.
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>
    /// How many times this item has been handed out and then reclaimed by a lock or session-lease
    /// expiry. Never required, so a first delivery is simply the default 0. Once it exceeds
    /// LockConfig.MaxDeliveryCount the item is dead-lettered instead of requeued, which is what stops
    /// a poison message cycling forever. Note this counts *detected* lapses: expiry is swept lazily,
    /// so escalation tracks consumer activity rather than wall-clock time.
    /// </summary>
    public int DeliveryCount { get; init; }
}

/// <summary>
/// Lock state for DequeueLocked operations.
/// Stores the dequeued item data along with lock metadata.
/// </summary>
public record LockState
{
    public required string LockId { get; init; }
    public required double CreatedAt { get; init; }
    public required double ExpiresAt { get; init; }
    public required int Priority { get; init; }
    public required int HeadSegment { get; init; }  // Kept for debugging/backward compat
    public required string ItemJson { get; init; }  // Stores dequeued item
    public required bool CompetingConsumerMode { get; init; }  // Whether competing consumers are enabled for this lock

    /// <summary>
    /// The item's QueueSegmentItem.Sequence, carried across the dequeue -> lock -> restore round trip
    /// so expiry can put it back where it came from. Not required: a lock planted directly in state
    /// (tests, repair tooling) restores ahead of everything, which is the safe direction.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>
    /// Carried through from the queued item so the count survives the dequeue -> lock -> requeue round
    /// trip rather than resetting each time the item is locked. See QueueSegmentItem.DeliveryCount.
    /// </summary>
    public int DeliveryCount { get; init; }
}

/// <summary>
/// Marker written at state key "idem_{key}" (one entry per idempotency key, with a native
/// per-entry TTL) to record that an IdempotencyKey has already been used. TTL enforcement is
/// native to the state store; CreatedAt is for debugging only.
/// </summary>
public record IdempotencyMarker
{
    public required double CreatedAt { get; init; }
}

/// <summary>
/// QueueActor - A FIFO queue-based Dapr actor with priority support.
/// Implements segmented storage (100 items per segment) for scalable queue operations.
/// </summary>
public class QueueActor : Actor, IQueueActor
{
    private readonly IQueueActorInvoker _actorInvoker;
    private readonly IBlobReaperActorInvoker _blobReaperActorInvoker;
    private readonly ISessionCoordinatorActorInvoker _sessionCoordinatorActorInvoker;
    private readonly IObjectClaimTokenIssuer _objectClaimTokenIssuer;
    private readonly BlobReapConfig _blobReapConfig;
    private readonly IdempotencyConfig _idempotencyConfig;
    private readonly LockConfig _lockConfig;

    private const int MaxSegmentSize = 100;
    private const int MinLockTtlSeconds = 1;
    private const int MaxLockTtlSeconds = 300;

    /// <summary>
    /// Granularity of the lock expiry index. Sets how many buckets a full 300s of outstanding locks
    /// can span (300/5 = 60 metadata entries at most), not how promptly a lock expires - see
    /// ExpiryBucketFor for why the floor/re-check scheme keeps expiry exact.
    /// </summary>
    private const int LockBucketWidthSeconds = 5;

    /// <summary>Index of a session actor's lease-scoped locks; see ActorMetadata.LockExpiryBuckets.</summary>
    private const string SessionLockIndexKey = "locks_session";
    /// <summary>Matches the bulk DequeueLocked cap, so one batch settles what one bulk dequeue produced.</summary>
    private const int MaxAcknowledgeBatchSize = 1000;
    private const int LockIdLength = 11;
    private const int MaxIdempotencyKeyLength = 128;

    // Naming convention for a per-session queue actor's id: "{queueId}-session-{sessionId}".
    // A session actor recognizes its own role purely from its own Id - no wire-level field is
    // needed on Enqueue/EnqueueItem for this. Same risk class as the existing "-deadletter"/"-sink"
    // conventions: a queueId that happens to contain this marker would be misread as a session
    // actor. Accepted, not solved, consistent with those existing conventions.
    private const string SessionActorIdMarker = "-session-";

    // Suffix of a dead-letter queue actor's id. A session queue's DLQ is
    // "{queueId}-session-{sessionId}-deadletter", so it carries the session marker too and must
    // be excluded from session self-registration - a DLQ is not a claimable session.
    private const string DeadLetterActorIdSuffix = "-deadletter";

    private static bool IsQueueCorrupted(ActorMetadata metadata) =>
        !string.IsNullOrEmpty(metadata.ErrorMessage);

    /// <summary>True if this actor's own id marks it as a per-session queue actor.</summary>
    private bool IsSessionActor() => Id.GetId().IndexOf(SessionActorIdMarker, StringComparison.Ordinal) > 0;

    /// <summary>
    /// The index entry a lock expiring at <paramref name="expiresAt"/> belongs to. Floor, not ceiling:
    /// the sweep treats a bucket as due from its *earliest* possible expiry and then re-checks each
    /// lock's own ExpiresAt, so a lock is never resolved late. Ceiling would hold a bucket back until
    /// every lock in it had certainly expired, delaying a short-TTL lock by up to the bucket width.
    /// </summary>
    private static long ExpiryBucketFor(double expiresAt) =>
        (long)Math.Floor(expiresAt / LockBucketWidthSeconds) * LockBucketWidthSeconds;

    private static string ExpiryBucketKey(long bucket) => $"locks_exp_{bucket}";

    /// <summary>
    /// Adds lock ids to the index, staged (no save). On a session actor everything goes in the single
    /// "locks_session" entry; otherwise ids are grouped into their expiry bucket. Ids are appended in
    /// dequeue order, which is the original FIFO order, so a bulk requeue can replay them faithfully.
    /// </summary>
    private async Task<ActorMetadata> IndexLocksAsync(ActorMetadata metadata, IEnumerable<string> lockIds, double expiresAt)
    {
        if (IsSessionActor())
        {
            var existing = await StateManager.TryGetStateAsync<List<string>>(SessionLockIndexKey);
            var ids = existing.HasValue ? new List<string>(existing.Value) : new List<string>();
            ids.AddRange(lockIds);
            await StateManager.SetStateAsync(SessionLockIndexKey, ids);
            return metadata;
        }

        long bucket = ExpiryBucketFor(expiresAt);
        string key = ExpiryBucketKey(bucket);

        var bucketState = await StateManager.TryGetStateAsync<List<string>>(key);
        var bucketIds = bucketState.HasValue ? new List<string>(bucketState.Value) : new List<string>();
        bucketIds.AddRange(lockIds);
        await StateManager.SetStateAsync(key, bucketIds);

        if (metadata.LockExpiryBuckets.Contains(bucket))
        {
            return metadata;
        }

        var buckets = new List<long>(metadata.LockExpiryBuckets) { bucket };
        buckets.Sort();
        return metadata with { LockExpiryBuckets = buckets };
    }

    /// <summary>
    /// Removes a lock id from a *session* actor's index, staged (no save).
    ///
    /// Deliberately not done for the bucketed (plain-queue) index. There, settling is the hot path and
    /// a bucket can hold every lock created in one TTL window - with a bulk dequeue that is thousands
    /// of ids, so rewriting the list on each settle is O(n) per ack and O(n^2) over a batch, which is
    /// enough to stall the actor. Instead the bucketed index is an over-approximation maintained
    /// solely by the sweep, which already skips ids whose lock has gone. Settling stays O(1).
    ///
    /// A session's index can't use that trick: it has no expiry bucket to age out, so under a
    /// long-lived lease it would accumulate an entry per message ever delivered. Its length is bounded
    /// by the consumer's outstanding prefetch instead, which is small, so pruning here is cheap.
    /// </summary>
    private Task DeindexSessionLockAsync(string lockId) => DeindexSessionLocksAsync([lockId]);

    /// <summary>
    /// Batch form of DeindexSessionLockAsync: one rewrite of the index however many locks settle.
    /// </summary>
    private async Task DeindexSessionLocksAsync(IReadOnlyCollection<string> lockIds)
    {
        if (!IsSessionActor())
        {
            return;
        }

        var existing = await StateManager.TryGetStateAsync<List<string>>(SessionLockIndexKey);
        if (!existing.HasValue)
        {
            return;
        }

        var removing = lockIds.ToHashSet();
        var ids = new List<string>(existing.Value);
        if (ids.RemoveAll(removing.Contains) == 0)
        {
            return;
        }

        if (ids.Count == 0)
        {
            await StateManager.RemoveStateAsync(SessionLockIndexKey);
        }
        else
        {
            await StateManager.SetStateAsync(SessionLockIndexKey, ids);
        }
    }

    /// <summary>
    /// Guard for Dequeue/DequeueLocked/Acknowledge/ExtendLock/DeadLetter: if this actor is a session
    /// actor with a currently-synced lease (ActiveSessionLeaseId set), the caller must present the
    /// matching, still-valid LeaseId. A no-op (returns true) when ActiveSessionLeaseId is null -
    /// the ordinary case for a plain, non-session queue actor, and for a session actor with no
    /// lease currently synced onto it.
    /// </summary>
    private static bool TryAuthorizeSessionLease(ActorMetadata metadata, string? leaseId, out string? errorCode, out string? errorMessage)
    {
        if (metadata.ActiveSessionLeaseId == null)
        {
            errorCode = null;
            errorMessage = null;
            return true;
        }

        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (metadata.ActiveSessionLeaseExpiresAt == null || now >= metadata.ActiveSessionLeaseExpiresAt.Value)
        {
            errorCode = "SESSION_LEASE_EXPIRED";
            errorMessage = "Session lease has expired";
            return false;
        }

        if (leaseId != metadata.ActiveSessionLeaseId)
        {
            errorCode = "INVALID_LEASE_ID";
            errorMessage = "leaseId does not match the current session lease holder";
            return false;
        }

        errorCode = null;
        errorMessage = null;
        return true;
    }

    public QueueActor(
        ActorHost host,
        IQueueActorInvoker queueActorInvoker,
        IBlobReaperActorInvoker blobReaperActorInvoker,
        ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker,
        IObjectClaimTokenIssuer objectClaimTokenIssuer,
        BlobReapConfig blobReapConfig,
        IdempotencyConfig idempotencyConfig,
        LockConfig lockConfig) : base(host)
    {
        _actorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
        _blobReaperActorInvoker = blobReaperActorInvoker ?? throw new ArgumentNullException(nameof(blobReaperActorInvoker));
        _sessionCoordinatorActorInvoker = sessionCoordinatorActorInvoker ?? throw new ArgumentNullException(nameof(sessionCoordinatorActorInvoker));
        _objectClaimTokenIssuer = objectClaimTokenIssuer ?? throw new ArgumentNullException(nameof(objectClaimTokenIssuer));
        _blobReapConfig = blobReapConfig ?? throw new ArgumentNullException(nameof(blobReapConfig));
        _idempotencyConfig = idempotencyConfig ?? throw new ArgumentNullException(nameof(idempotencyConfig));
        _lockConfig = lockConfig ?? throw new ArgumentNullException(nameof(lockConfig));
    }

    /// <summary>
    /// If itemJson is a blob-reference envelope, schedules deletion of the underlying blob on
    /// BlobReaperActor using the configurable backstop TTL. Called at whichever point the item
    /// becomes irrecoverably removed from queue state: immediately after dequeue for plain Dequeue,
    /// or on Acknowledge for DequeueLocked. This is a backstop only - if the issued ObjectClaimToken
    /// is redeemed via the download endpoint, deletion gets postponed further from that point.
    /// </summary>
    private async Task ScheduleBlobReapingIfNeeded(string? itemJson)
    {
        if (itemJson == null || !BlobReferenceEnvelope.TryParse(itemJson, out var blobReference))
        {
            return;
        }

        // Blob references contain '/' (e.g. "prefix/objectId"), which breaks Dapr's actor HTTP
        // routing if used directly as an ActorId (the id is embedded as a URL path segment).
        // Derive a stable, slash-free id instead, so repeated calls for the same blob target the
        // same reaper actor instance rather than fanning out unboundedly.
        var reaperActorId = new ActorId(HashBlobReference(blobReference!));
        try
        {
            await _blobReaperActorInvoker.InvokeMethodAsync<ScheduleDeletionRequest>(
                reaperActorId,
                "ScheduleDeletion",
                new ScheduleDeletionRequest
                {
                    BlobReference = blobReference!,
                    DelaySeconds = _blobReapConfig.BackstopSeconds
                });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to schedule blob reaping for {BlobReference}", blobReference);
        }
    }

    private static string HashBlobReference(string blobReference)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(blobReference));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Validates a client-supplied IdempotencyKey before it's used as (a suffix of) an actor
    /// state key. Used raw, not hashed - bounds length to stay well under Postgres's B-tree
    /// index row-size ceiling, and rejects control characters (including NUL, which Postgres
    /// `text` columns cannot store at all).
    /// </summary>
    private static bool IsValidIdempotencyKey(string idempotencyKey)
    {
        return idempotencyKey.Length <= MaxIdempotencyKeyLength
            && !idempotencyKey.Any(char.IsControl);
    }

    /// <summary>
    /// Called when the actor is activated. Initializes metadata structure if it doesn't exist,
    /// then - if this actor is acting as a per-session queue actor and hasn't yet done so -
    /// self-registers with the queue-level actor's session directory.
    /// </summary>
    protected override async Task OnActivateAsync()
    {
        // Initialize metadata structure if it doesn't exist
        var metadataExists = await StateManager.TryGetStateAsync<ActorMetadata>("metadata");
        ActorMetadata metadata;
        if (!metadataExists.HasValue)
        {
            metadata = new ActorMetadata
            {
                Config = new MetadataConfig
                {
                    SegmentSize = MaxSegmentSize,
                    BufferSegments = 1,
                    DedupEnabled = true
                },
                Queues = new Dictionary<int, QueueMetadata>(),
                LockCount = 0
            };
            await StateManager.SetStateAsync("metadata", metadata);
            await StateManager.SaveStateAsync();
            Logger.LogDebug("Actor activated and metadata initialized");
        }
        else
        {
            metadata = metadataExists.Value;
            Logger.LogDebug("Actor activated with existing metadata");
        }

        await RegisterAsSessionActorIfNeededAsync();
    }

    /// <summary>
    /// If this actor's own id identifies it as a per-session queue actor
    /// ("{queueId}-session-{sessionId}"), tells the SessionCoordinatorActor for this queue about
    /// this session via RegisterSession - on every activation, not just the first, so a session
    /// whose directory entry was ever pruned (e.g. by the coordinator's TTL sweep) self-heals the
    /// next time it activates, unconditionally. RegisterSession itself is idempotent (a no-op if
    /// already present), so this is safe and cheap to repeat. Best-effort: a failure is logged and
    /// left for the next activation to retry, it never fails activation itself.
    ///
    /// Because this runs (and calls out) on every activation rather than once ever, it can
    /// trigger a reentrancy deadlock - SessionCoordinatorActor calling SetSessionLease/
    /// ClearSessionLease on a cold session actor, whose activation calls back into the
    /// coordinator via this method while the coordinator's own turn is still outstanding
    /// (A -> B -> A). Fixed by enabling Dapr actor reentrancy - see the note on
    /// options.ReentrancyConfig in Program.cs, and SessionReentrancyTests for a test that forces
    /// this exact cold-reactivation path.
    /// </summary>
    private async Task RegisterAsSessionActorIfNeededAsync()
    {
        string ownId = Id.GetId();
        int markerIndex = ownId.IndexOf(SessionActorIdMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            return; // not a session actor
        }

        if (ownId.EndsWith(DeadLetterActorIdSuffix, StringComparison.Ordinal))
        {
            // This is a session queue's dead-letter actor, not the session itself. Registering it
            // would put a phantom "{sessionId}-deadletter" entry in the coordinator directory,
            // which an any-available claim could then hand to a consumer - redelivering items that
            // were deliberately dead-lettered.
            return;
        }

        string queueId = ownId[..markerIndex];
        string sessionId = ownId[(markerIndex + SessionActorIdMarker.Length)..];
        if (string.IsNullOrEmpty(queueId) || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        try
        {
            var result = await _sessionCoordinatorActorInvoker.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
                new ActorId(queueId),
                "RegisterSession",
                new RegisterSessionRequest { SessionId = sessionId });

            if (result.Success)
            {
                Logger.LogDebug("Session actor {OwnId} registered with SessionCoordinatorActor {QueueId}", ownId, queueId);
            }
            else
            {
                Logger.LogWarning("RegisterSession rejected for session actor {OwnId} against SessionCoordinatorActor {QueueId}", ownId, queueId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to self-register session actor {OwnId} with SessionCoordinatorActor {QueueId}; will retry on next activation", ownId, queueId);
        }
    }

    /// <summary>
    /// Called by this actor's SessionCoordinatorActor to sync the enforcement cache used to gate
    /// Dequeue/DequeueLocked/Acknowledge/ExtendLock/DeadLetter to the current lease holder.
    /// </summary>
    public async Task<SetSessionLeaseResponse> SetSessionLease(SetSessionLeaseRequest request)
    {
        // A new consumer claiming this session is the trigger that returns a dead consumer's locked
        // items, so this must run before the incoming lease overwrites the lapsed one.
        await SweepExpiredLocksAsync();

        var metadata = await GetMetadataAsync();
        metadata = metadata with
        {
            ActiveSessionLeaseId = request.LeaseId,
            ActiveSessionLeaseExpiresAt = request.ExpiresAt
        };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();
        return new SetSessionLeaseResponse { Success = true };
    }

    /// <summary>
    /// Called by this actor's SessionCoordinatorActor, best-effort, when a lease on this session
    /// is explicitly released.
    /// </summary>
    public async Task<ClearSessionLeaseResponse> ClearSessionLease()
    {
        var metadata = await GetMetadataAsync();
        metadata = metadata with
        {
            ActiveSessionLeaseId = null,
            ActiveSessionLeaseExpiresAt = null
        };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();
        return new ClearSessionLeaseResponse { Success = true };
    }

    /// <summary>
    /// Resolves locks whose TTL has lapsed, requeueing their items so they can be delivered again.
    /// Replaces the per-lock "lock-{lockId}" reminders: there is no scheduled trigger at all, so this
    /// runs at the top of the operations that could otherwise observe a stale lock (and on activation).
    ///
    /// Called before the LockCount gate in Dequeue and before the concurrency checks in DequeueLocked -
    /// that ordering is load-bearing, since a queue whose locks have all expired would otherwise keep
    /// reporting itself locked and never recover.
    ///
    /// Self-contained: it commits its own batch, so callers just invoke it and then re-read metadata.
    /// Bounded by LockConfig.SweepBatchSize so one operation can't stall behind a huge backlog; any
    /// remainder is picked up by the next operation.
    /// </summary>
    private async Task SweepExpiredLocksAsync()
    {
        // Session actors don't expire locks per item - their locks are scoped to the session lease
        // and are resolved in bulk when that lapses.
        if (IsSessionActor())
        {
            await ReclaimLapsedSessionLocksAsync();
            return;
        }

        var metadata = await GetMetadataAsync();
        if (metadata.LockExpiryBuckets.Count == 0)
        {
            return; // fast path: nothing outstanding, and no extra state read to discover that
        }

        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Oldest first, so a backlog capped by SweepBatchSize still drains in expiry order.
        var dueBuckets = metadata.LockExpiryBuckets.Where(b => now >= b).OrderBy(b => b).ToList();
        if (dueBuckets.Count == 0)
        {
            return;
        }

        int budget = Math.Max(1, _lockConfig.SweepBatchSize);
        int expired = 0;

        // Collected across every due bucket and restored in one merge at the end, rather than put
        // back one at a time: RestoreInOrderInternal rewrites the head segment, so doing it per item
        // would rewrite it once per lock for no gain.
        var restored = new List<(QueueSegmentItem Item, int Priority)>();
        var deadLettered = new List<string>();
        var emptiedBuckets = new List<long>();

        foreach (var bucket in dueBuckets)
        {
            if (budget <= 0)
            {
                break;
            }

            string bucketKey = ExpiryBucketKey(bucket);
            var bucketState = await StateManager.TryGetStateAsync<List<string>>(bucketKey);
            if (!bucketState.HasValue)
            {
                emptiedBuckets.Add(bucket); // index entry with no backing list - drop it
                continue;
            }

            var survivors = new List<string>();
            var ids = bucketState.Value;

            for (int i = 0; i < ids.Count; i++)
            {
                string lockId = ids[i];

                if (budget <= 0)
                {
                    // Out of budget: everything not yet examined stays for the next operation.
                    survivors.AddRange(ids.Skip(i));
                    break;
                }

                var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
                if (!lockState.HasValue)
                {
                    continue; // already acknowledged/dead-lettered; drop the stale index entry
                }

                // A bucket is due from its earliest possible expiry, so it can hold locks that have
                // not lapsed yet (and locks extended into a later expiry). Check each one.
                if (now < lockState.Value.ExpiresAt)
                {
                    survivors.Add(lockId);
                    continue;
                }

                budget--;

                int deliveryCount = lockState.Value.DeliveryCount + 1;
                if (deliveryCount > _lockConfig.MaxDeliveryCount)
                {
                    // Left in state, indexed and counted - dead-lettering reaches another actor, so it
                    // runs after this sweep's local commit, and only its success releases the lock. If
                    // it fails, the index entry is what lets a later sweep find the lock again.
                    survivors.Add(lockId);
                    deadLettered.Add(lockId);
                    continue;
                }

                expired++;
                restored.Add((ReclaimedItem(lockState.Value, deliveryCount), lockState.Value.Priority));
                await StateManager.RemoveStateAsync($"{lockId}-lock");
            }

            if (survivors.Count == 0)
            {
                await StateManager.RemoveStateAsync(bucketKey);
                emptiedBuckets.Add(bucket);
            }
            else if (survivors.Count != ids.Count)
            {
                await StateManager.SetStateAsync(bucketKey, survivors);
            }
        }

        // Skipped when nothing local changed - but dead-lettering below still runs.
        if (expired > 0 || emptiedBuckets.Count > 0)
        {
            await RestoreInOrderInternal(restored);

            if (restored.Count > 0)
            {
                Logger.LogInformation(
                    "Sweep expired {Expired} lock(s); restored {Restored} item(s) to their original positions",
                    expired, restored.Count);
            }

            // Re-read: the restore staged queue/metadata updates of its own.
            metadata = await GetMetadataAsync();
            var buckets = metadata.LockExpiryBuckets.Where(b => !emptiedBuckets.Contains(b)).ToList();
            await SetMetadataAsync(metadata with
            {
                LockCount = Math.Max(0, metadata.LockCount - expired),
                LockExpiryBuckets = buckets
            });
            await StateManager.SaveStateAsync();
        }

        // Cross-actor, so kept out of the batch above and committed per item - see
        // DeadLetterExpiredLockAsync.
        foreach (var lockId in deadLettered)
        {
            var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
            if (lockState.HasValue)
            {
                await DeadLetterExpiredLockAsync(lockId, lockState.Value);
            }
        }
    }

    /// <summary>
    /// Returns every outstanding lock held by this session to the queue, in one batch, once the
    /// session lease has lapsed. This is the session equivalent of per-item expiry: the lease is the
    /// only authority, mirroring Azure Service Bus where the session lock is "an umbrella for the
    /// message locks" and individual messages are never renewed.
    ///
    /// Self-healing by design. The coordinator's lease-expiry reminder only deletes its own record and
    /// never notifies this actor, and ClearSessionLease is best-effort, so the session actor decides
    /// for itself by comparing the synced expiry against now. A cold session actor resolves on its next
    /// activation - which is whenever someone actually wants the items.
    /// </summary>
    private async Task ReclaimLapsedSessionLocksAsync()
    {
        var metadata = await GetMetadataAsync();
        if (metadata.LockCount == 0)
        {
            return;
        }

        // A lease that is still live keeps its locks, however old their nominal ExpiresAt is.
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool leaseLive = metadata.ActiveSessionLeaseId != null
            && metadata.ActiveSessionLeaseExpiresAt.HasValue
            && now < metadata.ActiveSessionLeaseExpiresAt.Value;
        if (leaseLive)
        {
            return;
        }

        var indexState = await StateManager.TryGetStateAsync<List<string>>(SessionLockIndexKey);
        if (!indexState.HasValue || indexState.Value.Count == 0)
        {
            return;
        }

        // Ordering comes from each item's Sequence rather than the index order, so a session that
        // lapses more than once still hands its items back exactly as the consumer found them.
        var restored = new List<(QueueSegmentItem Item, int Priority)>();
        var deadLettered = new List<string>();

        foreach (var lockId in indexState.Value)
        {
            var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
            if (!lockState.HasValue)
            {
                continue;
            }

            int deliveryCount = lockState.Value.DeliveryCount + 1;
            if (deliveryCount > _lockConfig.MaxDeliveryCount)
            {
                deadLettered.Add(lockId);
                continue;
            }

            restored.Add((ReclaimedItem(lockState.Value, deliveryCount), lockState.Value.Priority));
            await StateManager.RemoveStateAsync($"{lockId}-lock");
        }

        await RestoreInOrderInternal(restored);

        // Locks still awaiting dead-lettering stay indexed and counted, so a failed DLQ enqueue
        // leaves them for the next reclaim rather than orphaning them.
        if (deadLettered.Count == 0)
        {
            await StateManager.RemoveStateAsync(SessionLockIndexKey);
        }
        else
        {
            await StateManager.SetStateAsync(SessionLockIndexKey, new List<string>(deadLettered));
        }

        // The lapsed lease is deliberately left in place: it is what makes the guard refuse callers
        // with SESSION_LEASE_EXPIRED until SetSessionLease installs the next holder.
        metadata = await GetMetadataAsync();
        await SetMetadataAsync(metadata with
        {
            LockCount = Math.Max(0, metadata.LockCount - restored.Count)
        });
        await StateManager.SaveStateAsync();

        Logger.LogInformation(
            "Session lease lapsed on {ActorId}; restored {Restored} locked item(s) to their original positions",
            Id.GetId(), restored.Count);

        // Cross-actor, so kept out of the batch above and committed per item - see DeadLetterExpiredLockAsync.
        foreach (var lockId in deadLettered)
        {
            var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
            if (lockState.HasValue)
            {
                await DeadLetterExpiredLockAsync(lockId, lockState.Value);
            }
        }
    }

    /// <summary>
    /// Returns reclaimed items to the exact position they were taken from, by merging them into the
    /// head segment in Sequence order.
    ///
    /// Sequence is what makes this independent of *how* the reclaim arrives. Restoring "to the front"
    /// is only correct for a single uncapped batch: a backlog split by SweepBatchSize, or locks
    /// lapsing across separate sweeps, hands back a later run after an earlier one is already back in
    /// the queue, and prepending each run would reverse them. Merging by Sequence gets the same
    /// answer whatever the arrival order.
    ///
    /// The head segment is always the right place to merge into, and the work is bounded by it. An
    /// item in a segment beyond the head has never been dequeued (restores only ever write at or
    /// below the head), so everything that *has* been dequeued - which is every item reaching this
    /// method - carries a lower Sequence than anything past the head segment.
    ///
    /// Segment numbers are free to go negative - every consumer of them is relative arithmetic, and the
    /// offload/load ranges always sit ahead of head, so a restored segment is never offload-eligible.
    /// Staged only; the caller commits.
    /// </summary>
    private async Task RestoreInOrderInternal(List<(QueueSegmentItem Item, int Priority)> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var metadata = await GetMetadataAsync();

        foreach (var group in items.GroupBy(i => i.Priority).OrderBy(g => g.Key))
        {
            int priority = group.Key;

            // A drained priority has had its entry removed entirely, which is the usual case when a
            // consumer had locked everything. Falling back to segment 0 keeps the restore in the live
            // range instead of special-casing an absent queue.
            metadata.Queues.TryGetValue(priority, out var queueMeta);
            int headSegment = queueMeta?.HeadSegment ?? 0;
            int tailSegment = queueMeta?.TailSegment ?? headSegment - 1;
            int count = queueMeta?.Count ?? 0;

            string headKey = $"queue_{priority}_seg_{headSegment}";
            var headState = await StateManager.TryGetStateAsync<Queue<QueueSegmentItem>>(headKey);
            var headItems = headState.HasValue ? headState.Value.ToList() : new List<QueueSegmentItem>();

            // The batch arrives in whatever order the buckets were walked, so it gets sorted; the
            // head segment is already in Sequence order, so combining the two is a linear merge.
            var restored = group.Select(i => i.Item).OrderBy(i => i.Sequence).ToList();
            var merged = new List<QueueSegmentItem>(restored.Count + headItems.Count);
            int r = 0, h = 0;
            while (r < restored.Count || h < headItems.Count)
            {
                merged.Add(h >= headItems.Count || (r < restored.Count && restored[r].Sequence <= headItems[h].Sequence)
                    ? restored[r++]
                    : headItems[h++]);
            }

            // Lay the merged run out ending on the original head slot, so anything already sitting in
            // later segments still follows it. Overflow spills into HeadSegment-1, -2, ...
            int chunks = (merged.Count + MaxSegmentSize - 1) / MaxSegmentSize;
            int firstSegment = headSegment - (chunks - 1);

            for (int c = 0; c < chunks; c++)
            {
                var chunk = merged.Skip(c * MaxSegmentSize).Take(MaxSegmentSize);
                var segmentQueue = new Queue<QueueSegmentItem>();
                foreach (var item in chunk)
                {
                    segmentQueue.Enqueue(item);
                }

                await StateManager.SetStateAsync($"queue_{priority}_seg_{firstSegment + c}", segmentQueue);
            }

            queueMeta = (queueMeta ?? new QueueMetadata()) with
            {
                HeadSegment = firstSegment,
                TailSegment = Math.Max(tailSegment, headSegment),
                Count = count + restored.Count
            };
            metadata = metadata with
            {
                Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [priority] = queueMeta }
            };
        }

        await SetMetadataAsync(metadata);
    }

    /// <summary>
    /// Rebuilds the queue entry a lock was holding, with its delivery count advanced for the lapse
    /// that is putting it back. Sequence is carried through untouched - that is what lets the restore
    /// find the item's original position.
    /// </summary>
    private static QueueSegmentItem ReclaimedItem(LockState lockData, int deliveryCount) =>
        new()
        {
            ItemJson = lockData.ItemJson,
            DeliveryCount = deliveryCount,
            Sequence = lockData.Sequence
        };

    /// <summary>
    /// Routes a poison item out to the dead-letter queue instead of requeueing it.
    ///
    /// Unlike the rest of the sweep this reaches another actor, and does so *before* the local state
    /// commit - so it commits per item rather than sharing the sweep's batch. Batching would mean a
    /// failure partway through left earlier items already in the DLQ with nothing committed locally,
    /// duplicating them on the retry. DLQ-first ordering is kept deliberately: at-least-once beats
    /// risking loss.
    /// </summary>
    private async Task DeadLetterExpiredLockAsync(string lockId, LockState lockData)
    {
        try
        {
            var result = await _actorInvoker.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                new ActorId($"{Id.GetId()}-deadletter"),
                "Enqueue",
                new EnqueueRequest
                {
                    Items = [new EnqueueItem { ItemJson = lockData.ItemJson, Priority = lockData.Priority }]
                });

            if (!result.Success)
            {
                Logger.LogError("DLQ enqueue rejected for lock {LockId}; leaving it for a later sweep", lockId);
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "DLQ enqueue failed for lock {LockId}; leaving it for a later sweep", lockId);
            return;
        }

        // The sweep left the lock indexed and counted until now. The bucketed index entry is left for
        // the sweep to discard, as on Acknowledge; the session index is pruned, as there.
        await StateManager.RemoveStateAsync($"{lockId}-lock");
        await DeindexSessionLockAsync(lockId);
        var metadata = await GetMetadataAsync();
        await SetMetadataAsync(metadata with { LockCount = Math.Max(0, metadata.LockCount - 1) });
        await StateManager.SaveStateAsync();

        Logger.LogWarning(
            "Lock {LockId} exceeded MaxDeliveryCount {MaxDeliveryCount}; dead-lettered",
            lockId, _lockConfig.MaxDeliveryCount);
    }

    /// <summary>
    /// Internal enqueue that stages changes without committing.
    /// Returns true if enqueue succeeded, false otherwise.
    /// </summary>
    private async Task<bool> EnqueueInternal(string itemJson, int priority)
    {
        // Validation
        if (string.IsNullOrEmpty(itemJson))
        {
            Logger.LogWarning("Enqueue failed: ItemJson is empty");
            return false;
        }

        if (priority < 0)
        {
            Logger.LogWarning($"Enqueue failed: priority must be >= 0, got {priority}");
            return false;
        }

        // Get metadata
        var metadata = await GetMetadataAsync();

        // Ensure priority queue exists
        if (!metadata.Queues.TryGetValue(priority, out var queueMeta))
        {
            queueMeta = new QueueMetadata
            {
                HeadSegment = 0,
                TailSegment = 0,
                Count = 0
            };
            metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [priority] = queueMeta } };
        }

        int tailSegment = queueMeta.TailSegment;
        int headSegment = queueMeta.HeadSegment;
        int count = queueMeta.Count;

        // Get current tail segment
        string segmentKey = $"queue_{priority}_seg_{tailSegment}";
        var segment = await StateManager.TryGetStateAsync<Queue<QueueSegmentItem>>(segmentKey);
        var segmentQueue = segment.HasValue ? segment.Value : new Queue<QueueSegmentItem>();

        // Check if segment is full BEFORE appending
        if (segmentQueue.Count >= MaxSegmentSize)
        {
            // Allocate new segment
            tailSegment++;
            segmentKey = $"queue_{priority}_seg_{tailSegment}";
            segmentQueue = new Queue<QueueSegmentItem>();
        }

        // Append item to segment (FIFO)
        var segmentItem = new QueueSegmentItem
        {
            ItemJson = itemJson,
            DeliveryCount = 0,
            Sequence = metadata.NextSequence
        };
        segmentQueue.Enqueue(segmentItem);
        metadata = metadata with { NextSequence = metadata.NextSequence + 1 };

        // Update metadata (count and pointers)
        count++;
        queueMeta = queueMeta with
        {
            HeadSegment = headSegment,
            TailSegment = tailSegment,
            Count = count
        };
        metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [priority] = queueMeta } };

        // Stage segment and metadata (don't commit)
        await StateManager.SetStateAsync(segmentKey, segmentQueue);
        await SetMetadataAsync(metadata);

        Logger.LogDebug($"Staged enqueue to priority {priority}, count now {count}");

        return true;
    }

    public async Task TestUnsafeUnload(UnsafeUnloadRequest request)
    {
        var metadata = await GetMetadataAsync();
        metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>() };
        await SetMetadataAsync(metadata);

        // this should blow up as it is an unload of unsaved data
        await this.StateManager.UnloadStateAsync("metadata", new UnloadStateOptions { AllowUnloadingWhenStateModified = true });
    }

    /// <summary>
    /// Enqueue items to the queue with optional priority per item.
    /// </summary>
    public async Task<EnqueueResponse> Enqueue(EnqueueRequest request)
    {
        // Get metadata
        var metadata = await GetMetadataAsync();

        // Check for corrupted state
        if (IsQueueCorrupted(metadata))
        {
            throw new InvalidOperationException($"Queue corrupted: {metadata.ErrorMessage}");
        }

        try
        {
            // Validate items array
            if (request.Items == null || request.Items.Count == 0)
            {
                Logger.LogWarning("Enqueue failed: Items array is empty or null");
                return new EnqueueResponse
                {
                    Success = false,
                    ItemsEnqueued = 0,
                    ErrorMessage = "Items array cannot be empty"
                };
            }

            if (request.Items.Count > 10000)
            {
                Logger.LogWarning($"Enqueue failed: Items count {request.Items.Count} exceeds maximum of 10000");
                return new EnqueueResponse
                {
                    Success = false,
                    ItemsEnqueued = 0,
                    ErrorMessage = "Maximum 10000 items per enqueue"
                };
            }

            // Validate all items before processing (fail fast)
            foreach (var item in request.Items)
            {
                if (string.IsNullOrEmpty(item.ItemJson))
                {
                    Logger.LogWarning("Enqueue failed: Item JSON is empty");
                    return new EnqueueResponse
                    {
                        Success = false,
                        ItemsEnqueued = 0,
                        ErrorMessage = "Item JSON cannot be empty"
                    };
                }

                if (item.Priority < 0)
                {
                    Logger.LogWarning($"Enqueue failed: Priority {item.Priority} must be >= 0");
                    return new EnqueueResponse
                    {
                        Success = false,
                        ItemsEnqueued = 0,
                        ErrorMessage = $"Priority must be >= 0, got {item.Priority}"
                    };
                }

                if (item.IdempotencyKey != null && !IsValidIdempotencyKey(item.IdempotencyKey))
                {
                    Logger.LogWarning($"Enqueue failed: IdempotencyKey exceeds {MaxIdempotencyKeyLength} chars or contains control characters");
                    return new EnqueueResponse
                    {
                        Success = false,
                        ItemsEnqueued = 0,
                        ErrorMessage = $"IdempotencyKey must be <= {MaxIdempotencyKeyLength} characters and contain no control characters"
                    };
                }
            }

            // Group by priority and process in order
            var groupedItems = request.Items
                .GroupBy(item => item.Priority)
                .OrderBy(g => g.Key);

            int totalEnqueued = 0;
            int totalDeduplicated = 0;
            var touchedIdempotencyKeys = new List<string>();
            var processedPriorities = new HashSet<int>();

            // Process each priority group
            foreach (var group in groupedItems)
            {
                int priority = group.Key;
                processedPriorities.Add(priority);

                foreach (var item in group)
                {
                    if (!string.IsNullOrEmpty(item.IdempotencyKey) && metadata.Config.DedupEnabled)
                    {
                        var idemKey = $"idem_{item.IdempotencyKey}";
                        var existingMarker = await StateManager.TryGetStateAsync<IdempotencyMarker>(idemKey);
                        if (existingMarker.HasValue)
                        {
                            totalDeduplicated++;
                            touchedIdempotencyKeys.Add(idemKey);
                            continue; // skip EnqueueInternal - already seen within the TTL window
                        }

                        await StateManager.SetStateAsync(
                            idemKey,
                            new IdempotencyMarker { CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                            TimeSpan.FromSeconds(_idempotencyConfig.TtlSeconds));
                        touchedIdempotencyKeys.Add(idemKey);
                    }

                    // Enqueue and stage changes (reuse existing EnqueueInternal)
                    bool success = await EnqueueInternal(item.ItemJson, priority);

                    if (!success)
                    {
                        // All-or-nothing: if any item fails, rollback not needed
                        // because SaveStateAsync hasn't been called yet
                        Logger.LogError($"Enqueue failed for item at priority {priority}");
                        return new EnqueueResponse
                        {
                            Success = false,
                            ItemsEnqueued = 0,
                            ErrorMessage = "Failed to enqueue item"
                        };
                    }

                    totalEnqueued++;
                }

                // Check and offload segments for this priority (non-blocking, best-effort). It
                // stages the metadata it's given, so it must be the metadata after this batch:
                // `metadata` was read before EnqueueInternal staged the items, and offloading
                // from it would write the batch's count, tail and sequence back out.
                await CheckAndOffloadSegmentsAsync(priority, await GetMetadataAsync());
            }

            // Commit all staged changes atomically (all items across all priorities)
            await StateManager.SaveStateAsync();

            // Bound actor in-memory growth: once committed, idempotency markers are safe to
            // unload from the state manager's tracker (best-effort, controlled by config).
            if (_idempotencyConfig.UnloadAfterCommit)
            {
                foreach (var idemKey in touchedIdempotencyKeys)
                {
                    try
                    {
                        await StateManager.UnloadStateAsync(idemKey);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to unload idempotency key state {IdemKey}", idemKey);
                    }
                }
            }

            Logger.LogDebug($"Enqueued {totalEnqueued} items across {processedPriorities.Count} priorities");

            return new EnqueueResponse
            {
                Success = true,
                ItemsEnqueued = totalEnqueued,
                ItemsDeduplicated = totalDeduplicated
            };
        }
        catch (InvalidOperationException)
        {
            // Re-throw corruption errors - queue must be repaired before continuing
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in Enqueue");
            return new EnqueueResponse
            {
                Success = false,
                ItemsEnqueued = 0,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Configures whether this queue honors IdempotencyKey dedup on Enqueue. Defaults to enabled;
    /// callable directly for a plain queue, or by TopicActor.Subscribe to opt a subscriber's
    /// provisioned queue out of dedup for items relayed from the topic.
    /// </summary>
    public async Task<ConfigureDedupResponse> ConfigureDedup(ConfigureDedupRequest request)
    {
        var metadata = await GetMetadataAsync();
        metadata = metadata with { Config = metadata.Config with { DedupEnabled = request.Enabled } };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();
        return new ConfigureDedupResponse { Success = true };
    }

    /// <summary>
    /// Dequeue one or more items from the queue (FIFO, lowest priority first).
    /// </summary>
    public async Task<DequeueResponse> Dequeue(DequeueRequest request)
    {
        // Before anything reads LockCount: a queue whose locks have all lapsed must serve, not report
        // itself locked.
        await SweepExpiredLocksAsync();

        var metadata = await GetMetadataAsync();

        // Check for corrupted state
        if (IsQueueCorrupted(metadata))
        {
            throw new InvalidOperationException($"Queue corrupted: {metadata.ErrorMessage}");
        }

        if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
        {
            return new DequeueResponse
            {
                Items = new List<DequeueItem>(),
                IsEmpty = false,
                Locked = false,
                Message = leaseErrorMessage,
                ErrorCode = leaseErrorCode
            };
        }

        // Validate count parameter
        if (request.Count < 0 || request.Count > 1000)
        {
            return new DequeueResponse
            {
                Items = new List<DequeueItem>(),
                IsEmpty = false,
                Locked = false,
                Message = "Count must be between 0 and 1000"
            };
        }

        var items = new List<DequeueItem>();

        // Dequeue up to Count items
        for (int i = 0; i < request.Count; i++)
        {
            var (response, priority, segmentItem) = await DequeueWithPriorityAsync();
            var itemJson = segmentItem?.ItemJson;

            // If locked, return what we have so far with lock info
            if (response.Locked)
            {
                await StateManager.SaveStateAsync();
                return new DequeueResponse
                {
                    Items = items,
                    Locked = true,
                    IsEmpty = false,
                    Message = response.Message,
                    LockExpiresAt = response.LockExpiresAt
                };
            }

            // If empty, return what we have so far
            if (response.IsEmpty)
            {
                await StateManager.SaveStateAsync();
                return new DequeueResponse
                {
                    Items = items,
                    Locked = false,
                    IsEmpty = items.Count == 0  // Only mark empty if we didn't dequeue anything
                };
            }

            // Add the dequeued item
            var isBlobRef = BlobReferenceEnvelope.TryParseEnvelope(itemJson!, out var envelope);
            items.Add(new DequeueItem
            {
                ItemJson = itemJson!,
                Priority = priority,
                ObjectClaimToken = isBlobRef ? _objectClaimTokenIssuer.Issue(envelope!.BlobReference, envelope.ContentType) : null
            });

            // Plain Dequeue has no lock/ack step - this is the sole finalization point for the item,
            // so schedule the backstop blob reap immediately rather than leaking the offloaded
            // object. The claim token issued above lets the caller redeem it later, which
            // postpones deletion further out from the download endpoint.
            if (isBlobRef)
            {
                await ScheduleBlobReapingIfNeeded(itemJson);
            }
        }

        // Commit all changes atomically
        await StateManager.SaveStateAsync();

        return new DequeueResponse
        {
            Items = items,
            Locked = false,
            IsEmpty = false
        };
    }

    /// <summary>
    /// Internal Dequeue method that returns item JSON, priority, and response metadata.
    /// This is used by DequeueLocked to track the original priority for expired lock restoration.
    /// Returns a tuple where:
    /// - response: Contains only metadata (Locked, IsEmpty, Message, LockExpiresAt)
    /// - priority: The priority level the item was dequeued from
    /// - item: The dequeued segment entry - payload, delivery count and Sequence (null if none)
    /// </summary>
    /// <param name="skipLockCheck">If true, skip the lock check (used for competing consumers)</param>
    private async Task<(DequeueResponse response, int priority, QueueSegmentItem? item)> DequeueWithPriorityAsync(bool skipLockCheck = false)
    {

        var metadata = await GetMetadataAsync();

        try
        {
            // Check if queue is locked (any active lock blocks Dequeue)
            if (!skipLockCheck)
            {
                if (metadata.LockCount > 0)
                {
                    Logger.LogDebug("Queue is locked, cannot dequeue");
                    return (new DequeueResponse
                    {
                        Locked = true,
                        IsEmpty = false,
                        Message = "Queue is locked by another operation"
                    }, -1, null);
                }
            }

            if (metadata.Queues.Count == 0)
            {
                return (new DequeueResponse { Locked = false, IsEmpty = true }, -1, null);
            }

            // Find lowest priority with items
            var sortedPriorities = metadata.Queues.Keys.OrderBy(p => p).ToList();

            foreach (var priority in sortedPriorities)
            {
                // Load any offloaded segments that are needed
                metadata = await CheckAndLoadSegmentsAsync(priority, metadata);

                var queueMeta = metadata.Queues[priority];

                if (queueMeta.Count == 0) continue;

                int headSegment = queueMeta.HeadSegment;
                int tailSegment = queueMeta.TailSegment;
                int count = queueMeta.Count;

                // Get head segment
                string segmentKey = $"queue_{priority}_seg_{headSegment}";
                var segment = await StateManager.TryGetStateAsync<Queue<QueueSegmentItem>>(segmentKey);

                if (!segment.HasValue || segment.Value.Count == 0)
                {
                    // Defensive: fix count desync
                    Logger.LogWarning($"Count desync detected for priority {priority}, removing queue metadata");
                    var updatedQueues = new Dictionary<int, QueueMetadata>(metadata.Queues);
                    updatedQueues.Remove(priority);
                    metadata = metadata with { Queues = updatedQueues };
                    await SetMetadataAsync(metadata);
                    continue;
                }

                // Dequeue single item from front (FIFO)
                var segmentQueue = segment.Value;
                var segmentItem = segmentQueue.Dequeue();

                // Handle segment cleanup
                if (segmentQueue.Count == 0)
                {
                    if (headSegment < tailSegment)
                    {
                        // More segments exist, move to next
                        // Delete the empty segment from state
                        await StateManager.RemoveStateAsync(segmentKey);

                        int oldHeadSegment = headSegment;
                        headSegment++;

                        // Update metadata pointers
                        count--;
                        queueMeta = queueMeta with
                        {
                            HeadSegment = headSegment,
                            TailSegment = tailSegment,
                            Count = count
                        };
                        metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [priority] = queueMeta } };
                        await SetMetadataAsync(metadata);

                        Logger.LogDebug($"[HEAD-ADVANCE] Actor {Id.GetId()}, Priority {priority}: " +
                            $"headSegment advancing from {oldHeadSegment} to {headSegment}, " +
                            $"tailSegment={tailSegment}, count={count}, " +
                            $"offloadedRange=({queueMeta.HeadOffloadedSegment}, {queueMeta.TailOffloadedSegment})");

                        Logger.LogDebug($"Dequeued item from priority {priority}, count now {count}");

                        // Return item JSON string directly with priority
                        return (new DequeueResponse { Locked = false, IsEmpty = false }, priority, segmentItem);
                    }
                    else
                    {
                        // Last segment empty, queue is now empty
                        // Delete the segment from state
                        await StateManager.RemoveStateAsync(segmentKey);
                        // Delete queue metadata
                        var updatedQueues = new Dictionary<int, QueueMetadata>(metadata.Queues);
                        updatedQueues.Remove(priority);
                        metadata = metadata with { Queues = updatedQueues };
                        await SetMetadataAsync(metadata);

                        Logger.LogDebug($"Dequeued last item from priority {priority}, queue now empty");

                        // Return item JSON string directly with priority
                        return (new DequeueResponse { Locked = false, IsEmpty = false }, priority, segmentItem);
                    }
                }
                else
                {
                    // Segment still has items, save it
                    await StateManager.SetStateAsync(segmentKey, segmentQueue);
                    count--;
                    queueMeta = queueMeta with
                    {
                        HeadSegment = headSegment,
                        TailSegment = tailSegment,
                        Count = count
                    };
                    metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [priority] = queueMeta } };
                    await SetMetadataAsync(metadata);

                    Logger.LogDebug($"Dequeued item from priority {priority}, count now {count}");

                    // Return item JSON string directly with priority
                    return (new DequeueResponse { Locked = false, IsEmpty = false }, priority, segmentItem);
                }
            }

            return (new DequeueResponse { Locked = false, IsEmpty = true }, -1, null);
        }
        catch (InvalidOperationException)
        {
            // Re-throw corruption errors - queue must be repaired before continuing
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in DequeueAsync");
            return (new DequeueResponse { Locked = false, IsEmpty = true }, -1, null);
        }
    }

    // Helper methods follow in next section...

    private async Task<ActorMetadata> GetMetadataAsync()
    {
        var result = await StateManager.TryGetStateAsync<ActorMetadata>("metadata");
        // Metadata is guaranteed to exist - initialized in OnActivateAsync before any methods are called
        return result.Value;
    }

    private async Task SetMetadataAsync(ActorMetadata metadata)
    {
        await StateManager.SetStateAsync("metadata", metadata);
    }

    /// <summary>
    /// Dequeue items with acknowledgement requirement (creates a lock).
    /// </summary>
    public async Task<DequeueLockedResponse> DequeueLocked(DequeueLockedRequest request)
    {
        // Before MaxConcurrency capacity and the legacy single-lock gate are computed, so neither is
        // sized against locks that are already dead.
        await SweepExpiredLocksAsync();

        var metadata = await GetMetadataAsync();

        // Check for corrupted state
        if (IsQueueCorrupted(metadata))
        {
            throw new InvalidOperationException($"Queue corrupted: {metadata.ErrorMessage}");
        }

        if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
        {
            return new DequeueLockedResponse
            {
                Locked = false,
                IsEmpty = false,
                Message = leaseErrorMessage,
                ErrorCode = leaseErrorCode
            };
        }

        try
        {
            // Get TTL (default 30, clamped to 1-300)
            int ttlSeconds = Math.Max(MinLockTtlSeconds, Math.Min(MaxLockTtlSeconds, request.TtlSeconds));

            // Get count (default 1, clamped to 1-1000)
            int count = Math.Max(1, Math.Min(1000, request.Count));

            // Apply MaxConcurrency limit if specified
            if (request.MaxConcurrency.HasValue)
            {
                int availableCapacity = Math.Max(0, request.MaxConcurrency.Value - metadata.LockCount);
                count = Math.Min(count, availableCapacity);

                if (count == 0)
                {
                    return new DequeueLockedResponse
                    {
                        Locked = false,
                        IsEmpty = false,
                        MaxConcurrencyReached = true,
                        Message = $"MaxConcurrency limit reached ({metadata.LockCount}/{request.MaxConcurrency.Value})"
                    };
                }
            }

            // Legacy mode: block if ANY lock exists
            if (!request.AllowCompetingConsumers && metadata.LockCount > 0)
            {
                return new DequeueLockedResponse
                {
                    Locked = true,
                    Message = "Queue is locked by another operation"
                };
            }

            // Competing consumer mode: proceed regardless of existing locks

            // Dequeue multiple items and create locks
            var lockedItems = new List<DequeueLockedItem>();
            var newLockIds = new List<string>();
            double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // On a session actor the lease is the authority and per-item TTL means nothing, so mirror
            // the lease expiry instead. The client still gets a meaningful LockExpiresAt, but nothing
            // here consults it - see ReclaimLapsedSessionLocksAsync.
            double lockExpiresAt = IsSessionActor() && metadata.ActiveSessionLeaseExpiresAt.HasValue
                ? metadata.ActiveSessionLeaseExpiresAt.Value
                : nowUnix + ttlSeconds;

            for (int i = 0; i < count; i++)
            {
                // Dequeue item (removes from queue) and store in lock
                // Skip lock check to allow parallel locks
                var (dequeueResult, priority, segmentItem) = await DequeueWithPriorityAsync(skipLockCheck: true);
                var itemJson = segmentItem?.ItemJson;

                // If queue is empty, return partial results
                if (itemJson == null)
                {
                    break;
                }

                // Create lock (stores dequeued item data)
                string lockId = GenerateLockId();

                var lockData = new LockState
                {
                    LockId = lockId,
                    CreatedAt = nowUnix,
                    ExpiresAt = lockExpiresAt,
                    Priority = priority,
                    HeadSegment = 0,  // No longer used but kept for backward compat
                    ItemJson = itemJson!,
                    CompetingConsumerMode = request.AllowCompetingConsumers,
                    DeliveryCount = segmentItem!.DeliveryCount,
                    Sequence = segmentItem.Sequence
                };

                await StateManager.SetStateAsync($"{lockId}-lock", lockData);

                // Track lock ID for reminder registration
                newLockIds.Add(lockId);

                // Add to response items
                // Note: unlike plain Dequeue, blob reaping is NOT scheduled here - the item is
                // recoverable via lock expiry/redelivery until Acknowledge finalizes it.
                var isItemBlobRef = BlobReferenceEnvelope.TryParseEnvelope(itemJson, out var blobEnvelope);
                lockedItems.Add(new DequeueLockedItem
                {
                    ItemJson = itemJson,
                    Priority = priority,
                    LockId = lockId,
                    LockExpiresAt = lockExpiresAt,
                    ObjectClaimToken = isItemBlobRef ? _objectClaimTokenIssuer.Issue(blobEnvelope!.BlobReference, blobEnvelope.ContentType) : null,
                    BlobContentType = isItemBlobRef ? blobEnvelope!.ContentType : null,
                    DeliveryCount = segmentItem.DeliveryCount
                });

                Logger.LogDebug("Created lock {LockId} ({Index}/{Count}) with TTL {TtlSeconds}s", lockId, i + 1, count, ttlSeconds);
            }

            // Check if we got any items
            if (lockedItems.Count == 0)
            {
                return new DequeueLockedResponse
                {
                    Locked = false,
                    IsEmpty = true,
                    Message = "Queue is empty"
                };
            }

            // Re-fetch metadata to get staged updates from dequeue loop (count decrements, headSegment advancements)
            metadata = await GetMetadataAsync();
            // Save all state atomically - increment lock counter
            metadata = metadata with { LockCount = metadata.LockCount + lockedItems.Count };
            // Index the whole batch in one write: every lock here shares lockExpiresAt, so they share
            // a bucket. This is what makes these locks findable again without a per-lock reminder.
            metadata = await IndexLocksAsync(metadata, newLockIds, lockExpiresAt);
            await SetMetadataAsync(metadata);
            await StateManager.SaveStateAsync();

            Logger.LogInformation("Created {Count} locks with TTL {TtlSeconds}s, expires at {LockExpiresAt}", lockedItems.Count, ttlSeconds, lockExpiresAt);

            return new DequeueLockedResponse
            {
                Items = lockedItems,
                Locked = false,
                IsEmpty = false,
                Message = $"Locked {lockedItems.Count} item(s)"
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in DequeueLockedAsync");
            return new DequeueLockedResponse
            {
                Locked = false,
                IsEmpty = true,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Acknowledge dequeued items using lock ID. Delegates to the batch settlement path with one id,
    /// keeping this endpoint's response and error codes as they were.
    /// </summary>
    public async Task<AcknowledgeResponse> Acknowledge(AcknowledgeRequest request)
    {
        try
        {
            var metadata = await GetMetadataAsync();
            if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
            {
                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = leaseErrorMessage ?? string.Empty,
                    ErrorCode = leaseErrorCode
                };
            }

            var outcome = (await SettleLocksAsync(metadata, [request.LockId]))[0].Outcome;

            return outcome switch
            {
                AckOutcomeAcknowledged => new AcknowledgeResponse
                {
                    Success = true,
                    Message = "Successfully acknowledged 1 item",
                    ItemsAcknowledged = 1
                },
                "INVALID_LOCK_ID" => new AcknowledgeResponse { Success = false, Message = "lock_id cannot be empty", ErrorCode = outcome },
                "LOCK_NOT_FOUND" => new AcknowledgeResponse { Success = false, Message = "Lock not found", ErrorCode = outcome },
                _ => new AcknowledgeResponse { Success = false, Message = "Lock has expired", ErrorCode = outcome }
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in AcknowledgeAsync");
            return new AcknowledgeResponse
            {
                Success = false,
                Message = $"Error: {ex.Message}",
                ErrorCode = "INTERNAL_ERROR"
            };
        }
    }

    /// <summary>
    /// Acknowledge up to MaxAcknowledgeBatchSize locks in one turn and one state transaction. Each lock
    /// gets its own outcome; only an invalid request or a bad session lease fails the whole call.
    /// </summary>
    public async Task<AcknowledgeBatchResponse> AcknowledgeBatch(AcknowledgeBatchRequest request)
    {
        try
        {
            var lockIds = request.LockIds ?? [];
            if (lockIds.Count == 0 || lockIds.Count > MaxAcknowledgeBatchSize)
            {
                return new AcknowledgeBatchResponse
                {
                    Success = false,
                    Message = $"lockIds must contain between 1 and {MaxAcknowledgeBatchSize} ids",
                    ErrorCode = "VALIDATION_ERROR"
                };
            }

            if (lockIds.Distinct().Count() != lockIds.Count)
            {
                return new AcknowledgeBatchResponse
                {
                    Success = false,
                    Message = "lockIds must not contain duplicates",
                    ErrorCode = "VALIDATION_ERROR"
                };
            }

            var metadata = await GetMetadataAsync();
            if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
            {
                return new AcknowledgeBatchResponse
                {
                    Success = false,
                    Message = leaseErrorMessage ?? string.Empty,
                    ErrorCode = leaseErrorCode
                };
            }

            var results = await SettleLocksAsync(metadata, lockIds);
            int settled = results.Count(r => r.Outcome == AckOutcomeAcknowledged);

            return new AcknowledgeBatchResponse
            {
                Success = true,
                Message = $"Acknowledged {settled} of {lockIds.Count} locks",
                ItemsAcknowledged = settled,
                Results = results
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in AcknowledgeBatch");
            return new AcknowledgeBatchResponse
            {
                Success = false,
                Message = $"Error: {ex.Message}",
                ErrorCode = "INTERNAL_ERROR"
            };
        }
    }

    private const string AckOutcomeAcknowledged = "ACKNOWLEDGED";

    /// <summary>
    /// The one settlement implementation behind Acknowledge and AcknowledgeBatch. The caller has
    /// already authorised the session lease. Every settled lock is removed in a single save, so
    /// either all ACKNOWLEDGED outcomes are durable or the call throws and none are.
    /// </summary>
    private async Task<List<AcknowledgeResult>> SettleLocksAsync(ActorMetadata metadata, IReadOnlyList<string> lockIds)
    {
        var results = new List<AcknowledgeResult>(lockIds.Count);
        var settledIds = new List<string>();
        var settledItemJson = new List<string>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var lockId in lockIds)
        {
            string outcome;
            if (string.IsNullOrEmpty(lockId))
            {
                outcome = "INVALID_LOCK_ID";
            }
            else
            {
                var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
                if (!lockState.HasValue)
                {
                    outcome = "LOCK_NOT_FOUND";
                }
                // On a plain queue the per-item TTL is the authority, so an expired lock can no longer be
                // settled - its item is already on its way back to the queue. Matches ExtendLock and
                // DeadLetter, which have always refused here. Session actors are exempt: their locks are
                // governed by the lease (checked by the caller), and LockState.ExpiresAt is only
                // informational, so a renewed lease must not be second-guessed by a stale per-item value.
                else if (!IsSessionActor() && now >= lockState.Value.ExpiresAt)
                {
                    outcome = "LOCK_EXPIRED";
                }
                else
                {
                    // Item already dequeued during DequeueLocked - just remove lock state.
                    await StateManager.RemoveStateAsync($"{lockId}-lock");
                    settledIds.Add(lockId);
                    settledItemJson.Add(lockState.Value.ItemJson);
                    outcome = AckOutcomeAcknowledged;
                }
            }

            results.Add(new AcknowledgeResult { LockId = lockId ?? string.Empty, Outcome = outcome });
        }

        if (settledIds.Count == 0)
        {
            return results;
        }

        await DeindexSessionLocksAsync(settledIds);
        await SetMetadataAsync(metadata with { LockCount = metadata.LockCount - settledIds.Count });

        await StateManager.SaveStateAsync();

        // Acknowledge is the finalization point for DequeueLocked items - if an item's payload was
        // offloaded to an object store, schedule deletion of the underlying blob. Best-effort.
        await Task.WhenAll(settledItemJson.Select(ScheduleBlobReapingIfNeeded));

        Logger.LogDebug("Acknowledged {Count} lock(s)", settledIds.Count);

        return results;
    }

    public async Task<ExtendLockResponse> ExtendLock(ExtendLockRequest request)
    {
        try
        {
            var metadata = await GetMetadataAsync();
            if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
            {
                return new ExtendLockResponse
                {
                    Success = false,
                    NewExpiresAt = 0,
                    ErrorCode = leaseErrorCode,
                    ErrorMessage = leaseErrorMessage
                };
            }

            // Validate lock_id
            if (string.IsNullOrEmpty(request.LockId))
            {
                return new ExtendLockResponse
                {
                    Success = false,
                    NewExpiresAt = 0,
                    ErrorCode = "INVALID_LOCK_ID",
                    ErrorMessage = "lock_id cannot be empty"
                };
            }

            // Validate additional_ttl_seconds
            if (request.AdditionalTtlSeconds <= 0)
            {
                return new ExtendLockResponse
                {
                    Success = false,
                    NewExpiresAt = 0,
                    ErrorCode = "INVALID_TTL",
                    ErrorMessage = "additional_ttl_seconds must be positive"
                };
            }

            string lockId = request.LockId;

            // Get lock state
            var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
            if (!lockState.HasValue)
            {
                return new ExtendLockResponse
                {
                    Success = false,
                    NewExpiresAt = 0,
                    ErrorCode = "LOCK_NOT_FOUND",
                    ErrorMessage = "Lock not found"
                };
            }

            var lockData = lockState.Value;

            // Check if lock expired
            double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now >= lockData.ExpiresAt)
            {
                return new ExtendLockResponse
                {
                    Success = false,
                    NewExpiresAt = 0,
                    ErrorCode = "LOCK_EXPIRED",
                    ErrorMessage = "Lock has expired"
                };
            }

            // Calculate new expiry time (add to current ExpiresAt, not now)
            double newExpiresAt = lockData.ExpiresAt + request.AdditionalTtlSeconds;

            // Update lock state with new expiry
            var updatedLock = lockData with { ExpiresAt = newExpiresAt };
            await StateManager.SetStateAsync($"{lockId}-lock", updatedLock);

            // File the id under its new expiry bucket so the sweep will still find it once the
            // extension runs out. The old entry is left behind on purpose: the sweep checks each
            // lock's real ExpiresAt and simply keeps a not-yet-expired id as a survivor, so a stale
            // duplicate costs one string rather than an O(n) rewrite of a potentially huge bucket.
            if (!IsSessionActor() && ExpiryBucketFor(newExpiresAt) != ExpiryBucketFor(lockData.ExpiresAt))
            {
                metadata = await IndexLocksAsync(metadata, [lockId], newExpiresAt);
                await SetMetadataAsync(metadata);
            }

            await StateManager.SaveStateAsync();


            Logger.LogDebug($"Extended lock {lockId} by {request.AdditionalTtlSeconds}s, new expiry: {newExpiresAt}");

            return new ExtendLockResponse
            {
                Success = true,
                NewExpiresAt = newExpiresAt,
                ErrorCode = null,
                ErrorMessage = null
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in ExtendLockAsync");
            return new ExtendLockResponse
            {
                Success = false,
                NewExpiresAt = 0,
                ErrorCode = "INTERNAL_ERROR",
                ErrorMessage = $"Error: {ex.Message}"
            };
        }
    }

    private const string ExtendOutcomeExtended = "EXTENDED";

    /// <summary>
    /// Renews up to MaxAcknowledgeBatchSize locks in one turn, each checked as ExtendLock checks it,
    /// with every change in a single save. A lock is renewed to expire TtlSeconds from now rather
    /// than having time added, so a renewer that runs late, or renews twice, can't let it drift.
    /// </summary>
    public async Task<ExtendLockBatchResponse> ExtendLockBatch(ExtendLockBatchRequest request)
    {
        try
        {
            var lockIds = request.LockIds ?? [];
            string? validationError =
                lockIds.Count == 0 || lockIds.Count > MaxAcknowledgeBatchSize ? $"lockIds must contain between 1 and {MaxAcknowledgeBatchSize} ids"
                : lockIds.Distinct().Count() != lockIds.Count ? "lockIds must not contain duplicates"
                : request.TtlSeconds <= 0 ? "ttlSeconds must be positive"
                : null;
            if (validationError != null)
            {
                return new ExtendLockBatchResponse { Success = false, Message = validationError, ErrorCode = "VALIDATION_ERROR" };
            }

            var metadata = await GetMetadataAsync();
            if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
            {
                return new ExtendLockBatchResponse { Success = false, Message = leaseErrorMessage ?? string.Empty, ErrorCode = leaseErrorCode };
            }

            var results = new List<ExtendLockResult>(lockIds.Count);
            var reindex = new List<(string LockId, double NewExpiresAt)>();
            // Sub-second, unlike the whole seconds elsewhere: truncating now would cost a renewed
            // lock up to a second of its TTL, which a short TTL can't spare between renewals.
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            foreach (var lockId in lockIds)
            {
                if (string.IsNullOrEmpty(lockId))
                {
                    results.Add(new ExtendLockResult { LockId = string.Empty, Outcome = "INVALID_LOCK_ID" });
                    continue;
                }

                var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
                if (!lockState.HasValue)
                {
                    results.Add(new ExtendLockResult { LockId = lockId, Outcome = "LOCK_NOT_FOUND" });
                    continue;
                }

                var lockData = lockState.Value;
                if (now >= lockData.ExpiresAt)
                {
                    results.Add(new ExtendLockResult { LockId = lockId, Outcome = "LOCK_EXPIRED" });
                    continue;
                }

                double newExpiresAt = Math.Max(lockData.ExpiresAt, now + request.TtlSeconds);
                if (newExpiresAt == lockData.ExpiresAt)
                {
                    results.Add(new ExtendLockResult { LockId = lockId, Outcome = ExtendOutcomeExtended, NewExpiresAt = newExpiresAt });
                    continue;
                }
                await StateManager.SetStateAsync($"{lockId}-lock", lockData with { ExpiresAt = newExpiresAt });
                if (!IsSessionActor() && ExpiryBucketFor(newExpiresAt) != ExpiryBucketFor(lockData.ExpiresAt))
                {
                    reindex.Add((lockId, newExpiresAt));
                }

                results.Add(new ExtendLockResult { LockId = lockId, Outcome = ExtendOutcomeExtended, NewExpiresAt = newExpiresAt });
            }

            if (results.Any(r => r.Outcome == ExtendOutcomeExtended))
            {
                // File each moved lock under its new expiry bucket, one index write per bucket. As in
                // ExtendLock, the stale entry in the old bucket is left for the sweep to skip.
                foreach (var bucket in reindex.GroupBy(r => ExpiryBucketFor(r.NewExpiresAt)))
                {
                    metadata = await IndexLocksAsync(metadata, bucket.Select(r => r.LockId).ToList(), bucket.First().NewExpiresAt);
                }
                if (reindex.Count > 0)
                {
                    await SetMetadataAsync(metadata);
                }

                await StateManager.SaveStateAsync();
            }

            int extended = results.Count(r => r.Outcome == ExtendOutcomeExtended);
            return new ExtendLockBatchResponse
            {
                Success = true,
                Message = $"Extended {extended} of {lockIds.Count} locks",
                Results = results
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in ExtendLockBatch");
            return new ExtendLockBatchResponse { Success = false, Message = $"Error: {ex.Message}", ErrorCode = "INTERNAL_ERROR" };
        }
    }

    public async Task<DeadLetterResponse> DeadLetter(DeadLetterRequest request)
    {
        try
        {
            var metadata = await GetMetadataAsync();
            if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = leaseErrorCode,
                    Message = leaseErrorMessage
                };
            }

            // Validate lock_id
            if (string.IsNullOrEmpty(request.LockId))
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "INVALID_LOCK_ID",
                    Message = "lock_id cannot be empty"
                };
            }

            string lockId = request.LockId;

            // Get lock state
            var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
            if (!lockState.HasValue)
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "LOCK_NOT_FOUND",
                    Message = "Lock not found"
                };
            }

            var lockData = lockState.Value;

            // Check if lock expired
            double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now >= lockData.ExpiresAt)
            {
                // Lock expired - return error without restoring to queue
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "LOCK_EXPIRED",
                    Message = "Lock has expired"
                };
            }

            string? dlqActorId = await MoveLockToDeadLetterAsync(lockId, lockData, metadata);
            if (dlqActorId == null)
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "DLQ_ENQUEUE_FAILED",
                    Message = "Failed to enqueue item to dead letter queue"
                };
            }

            return new DeadLetterResponse
            {
                Status = "SUCCESS",
                DlqId = dlqActorId,
                Message = "Item moved to dead letter queue and removed from main queue"
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in DeadLetterAsync");
            return new DeadLetterResponse
            {
                Status = "ERROR",
                ErrorCode = "INTERNAL_ERROR",
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Enqueues a locked item to the dead-letter queue, then voids its lock and commits. DLQ-first,
    /// so a failure leaves the lock in place rather than losing the item. Returns the DLQ actor id,
    /// or null if the DLQ refused it.
    /// </summary>
    private async Task<string?> MoveLockToDeadLetterAsync(string lockId, LockState lockData, ActorMetadata metadata)
    {
        string dlqActorId = $"{Id.GetId()}{DeadLetterActorIdSuffix}";
        var enqueueResult = await _actorInvoker.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            new ActorId(dlqActorId),
            "Enqueue",
            new EnqueueRequest
            {
                Items = [new EnqueueItem { ItemJson = lockData.ItemJson, Priority = lockData.Priority }]
            });

        if (!enqueueResult.Success)
        {
            return null;
        }

        // Item already removed from the main queue during DequeueLocked - just void the lock.
        await StateManager.RemoveStateAsync($"{lockId}-lock");

        await DeindexSessionLockAsync(lockId);
        await SetMetadataAsync(metadata with { LockCount = metadata.LockCount - 1 });

        await StateManager.SaveStateAsync();

        return dlqActorId;
    }

    /// <summary>
    /// Returns a locked item to the position it was taken from and voids the lock - an immediate,
    /// holder-initiated version of lock expiry. Counts as a delivery attempt, so past
    /// MaxDeliveryCount the item is dead-lettered instead, which stops a nack loop spinning forever.
    /// </summary>
    public async Task<NackResponse> Nack(NackRequest request)
    {
        try
        {
            var metadata = await GetMetadataAsync();
            if (!TryAuthorizeSessionLease(metadata, request.LeaseId, out var leaseErrorCode, out var leaseErrorMessage))
            {
                return new NackResponse
                {
                    Success = false,
                    Message = leaseErrorMessage ?? string.Empty,
                    ErrorCode = leaseErrorCode
                };
            }

            if (string.IsNullOrEmpty(request.LockId))
            {
                return new NackResponse
                {
                    Success = false,
                    Message = "lock_id cannot be empty",
                    ErrorCode = "INVALID_LOCK_ID"
                };
            }

            string lockId = request.LockId;

            var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
            if (!lockState.HasValue)
            {
                return new NackResponse
                {
                    Success = false,
                    Message = "Lock not found",
                    ErrorCode = "LOCK_NOT_FOUND"
                };
            }

            var lockData = lockState.Value;

            // Same rule as Acknowledge: on a plain queue an expired lock already belongs to the sweep;
            // on a session actor the lease (checked above) is the only authority.
            if (!IsSessionActor() && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= lockData.ExpiresAt)
            {
                return new NackResponse
                {
                    Success = false,
                    Message = "Lock has expired",
                    ErrorCode = "LOCK_EXPIRED"
                };
            }

            int deliveryCount = lockData.DeliveryCount + 1;

            if (deliveryCount > _lockConfig.MaxDeliveryCount)
            {
                string? dlqActorId = await MoveLockToDeadLetterAsync(lockId, lockData, metadata);
                if (dlqActorId == null)
                {
                    return new NackResponse
                    {
                        Success = false,
                        Message = "Failed to enqueue item to dead letter queue",
                        ErrorCode = "DLQ_ENQUEUE_FAILED",
                        DeliveryCount = lockData.DeliveryCount
                    };
                }

                Logger.LogWarning(
                    "Nack of lock {LockId} exceeded MaxDeliveryCount {MaxDeliveryCount}; dead-lettered",
                    lockId, _lockConfig.MaxDeliveryCount);

                return new NackResponse
                {
                    Success = true,
                    Message = "Item exceeded max delivery count and was moved to dead letter queue",
                    DeadLettered = true,
                    DeliveryCount = deliveryCount,
                    DlqId = dlqActorId
                };
            }

            await RestoreInOrderInternal([(ReclaimedItem(lockData, deliveryCount), lockData.Priority)]);
            await StateManager.RemoveStateAsync($"{lockId}-lock");
            await DeindexSessionLockAsync(lockId);

            // Re-read: the restore staged queue/metadata updates of its own.
            metadata = await GetMetadataAsync();
            await SetMetadataAsync(metadata with { LockCount = Math.Max(0, metadata.LockCount - 1) });

            await StateManager.SaveStateAsync();

            Logger.LogDebug("Nacked lock {LockId}; item returned to its original position", lockId);

            return new NackResponse
            {
                Success = true,
                Message = "Item returned to queue",
                DeliveryCount = deliveryCount
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in NackAsync");
            return new NackResponse
            {
                Success = false,
                Message = $"Error: {ex.Message}",
                ErrorCode = "INTERNAL_ERROR"
            };
        }
    }

    /// <summary>
    /// Generate a cryptographically secure 11-character alphanumeric lock ID.
    /// </summary>
    private string GenerateLockId()
    {
        // Generate 11-character alphanumeric string 
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = new byte[LockIdLength];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        var result = new StringBuilder(LockIdLength);
        foreach (var b in bytes)
        {
            result.Append(chars[b % chars.Length]);
        }

        return result.ToString();
    }

    /// <summary>
    /// Get configured buffer_segments value (default 1).
    /// </summary>
    private int GetBufferSegments(ActorMetadata metadata)
    {
        return metadata.Config.BufferSegments;
    }

    /// <summary>
    /// Get offloaded segment range for a priority queue.
    /// Returns (head, tail) or (null, null) if no offloaded segments exist.
    /// </summary>
    private (int? head, int? tail) GetOffloadedRange(ActorMetadata metadata, int priority)
    {
        if (!metadata.Queues.TryGetValue(priority, out var queueMeta))
            return (null, null);

        if (queueMeta.HeadOffloadedSegment.HasValue && queueMeta.TailOffloadedSegment.HasValue)
        {
            return (queueMeta.HeadOffloadedSegment.Value, queueMeta.TailOffloadedSegment.Value);
        }

        return (null, null);
    }

    /// <summary>
    /// Add a segment to the offloaded range (extends tail). Returns updated metadata.
    /// </summary>
    private ActorMetadata AddOffloadedSegment(ActorMetadata metadata, int priority, int segmentNum)
    {
        if (!metadata.Queues.TryGetValue(priority, out var queueMeta))
        {
            queueMeta = new QueueMetadata
            {
                HeadSegment = 0,
                TailSegment = 0,
                Count = 0
            };
        }

        // If no offloaded range exists, initialize both head and tail
        if (!queueMeta.HeadOffloadedSegment.HasValue)
        {
            queueMeta = queueMeta with
            {
                HeadOffloadedSegment = segmentNum,
                TailOffloadedSegment = segmentNum
            };
        }
        else
        {
            // Extend tail (segments added sequentially)
            queueMeta = queueMeta with { TailOffloadedSegment = segmentNum };
        }

        return metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [priority] = queueMeta } };
    }

    /// <summary>
    /// Remove a segment from the offloaded range (shrinks from head). Returns updated metadata.
    /// </summary>
    private ActorMetadata RemoveOffloadedSegment(ActorMetadata metadata, int priority, int segmentNum)
    {
        if (!metadata.Queues.TryGetValue(priority, out var queueMeta))
            return metadata;

        if (!queueMeta.HeadOffloadedSegment.HasValue || !queueMeta.TailOffloadedSegment.HasValue)
            return metadata;

        int head = queueMeta.HeadOffloadedSegment.Value;
        int tail = queueMeta.TailOffloadedSegment.Value;

        // Should only remove from head (FIFO)
        if (segmentNum == head)
        {
            if (head == tail)
            {
                // Last segment in range, clear both
                queueMeta = queueMeta with
                {
                    HeadOffloadedSegment = null,
                    TailOffloadedSegment = null
                };
            }
            else
            {
                // Move head forward
                queueMeta = queueMeta with { HeadOffloadedSegment = head + 1 };
            }

            return metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [priority] = queueMeta } };
        }

        return metadata;
    }

    /// <summary>
    /// Offload a full segment to the external state store.
    /// Returns updated metadata if successful, null otherwise (logs warning, doesn't throw).
    /// </summary>
    private async Task<ActorMetadata?> OffloadSegmentAsync(int priority, int segmentNum, Queue<QueueSegmentItem> segmentData, ActorMetadata metadata)
    {
        try
        {
            string segmentKey = $"queue_{priority}_seg_{segmentNum}";

            Logger.LogDebug($"[OFFLOAD-START] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}");

            // Add to offloaded range in metadata
            var updatedMetadata = AddOffloadedSegment(metadata, priority, segmentNum);

            // Unload from actor memory (stays in permanent store at same key)
            await StateManager.UnloadStateAsync(segmentKey);

            // Save metadata (staging only - commit happens in caller)
            await SetMetadataAsync(updatedMetadata);

            Logger.LogDebug($"[OFFLOAD-SUCCESS] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: Unloaded from actor memory");

            return updatedMetadata;
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[OFFLOAD-FAILED] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                $"Failed to offload - {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Load an offloaded segment from state store back into actor state.
    /// Returns updated metadata if successful, throws on error.
    /// </summary>
    private async Task<ActorMetadata?> LoadOffloadedSegmentAsync(int priority, int segmentNum, ActorMetadata metadata)
    {
        try
        {
            string segmentKey = $"queue_{priority}_seg_{segmentNum}";

            Logger.LogDebug($"[LOAD-START] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}");

            // Load segment from permanent store (Dapr hydrates automatically)
            var segmentData = await StateManager.TryGetStateAsync<Queue<QueueSegmentItem>>(segmentKey);

            if (!segmentData.HasValue || segmentData.Value == null || segmentData.Value.Count == 0)
            {
                string errorMsg = $"CORRUPTED: Segment {segmentNum} priority {priority} missing or empty. " +
                                  $"Offloaded range: {GetOffloadedRange(metadata, priority)}. " +
                                  $"Actor ID: {Id.GetId()}. Manual intervention required.";

                var corruptedMetadata = metadata with { ErrorMessage = errorMsg };
                await SetMetadataAsync(corruptedMetadata);
                await StateManager.SaveStateAsync();  // Persist error state immediately

                Logger.LogCritical($"[LOAD-MISSING] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: {errorMsg}");
                throw new InvalidOperationException(errorMsg);
            }

            // Remove from offloaded range
            var updatedMetadata = RemoveOffloadedSegment(metadata, priority, segmentNum);

            // Save metadata (staging only - commit happens in caller)
            await SetMetadataAsync(updatedMetadata);

            Logger.LogDebug($"[LOAD-SUCCESS] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: Loaded {segmentData.Value.Count} items from permanent store");

            return updatedMetadata;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"Failed to load offloaded segment {segmentNum} for priority {priority} (actor {Id.GetId()})");
            throw;  // Changed from return null - loading failures should be fatal
        }
    }


    /// <summary>
    /// Check and offload eligible segments for a priority queue.
    /// Called after Enqueue. Non-blocking - failures are logged but don't throw.
    /// </summary>
    private async Task CheckAndOffloadSegmentsAsync(int priority, ActorMetadata metadata)
    {
        try
        {
            if (!metadata.Queues.TryGetValue(priority, out var queueMeta))
                return;

            int headSegment = queueMeta.HeadSegment;
            int tailSegment = queueMeta.TailSegment;
            int bufferSegments = GetBufferSegments(metadata);
            var offloadedRange = GetOffloadedRange(metadata, priority);

            // Calculate eligible segment range
            int minOffload = headSegment + bufferSegments + 1;
            int maxOffload = tailSegment;

            Logger.LogDebug($"[OFFLOAD-CHECK] Actor {Id.GetId()}, Priority {priority}: " +
                $"headSegment={headSegment}, tailSegment={tailSegment}, bufferSegments={bufferSegments}, " +
                $"minOffload={minOffload}, maxOffload={maxOffload}, " +
                $"offloadedRange=({offloadedRange.head}, {offloadedRange.tail})");

            // Check each segment in range
            for (int segmentNum = minOffload; segmentNum < maxOffload; segmentNum++)
            {
                // Skip if already offloaded
                if (offloadedRange.head != null && offloadedRange.tail != null)
                {
                    if (segmentNum >= offloadedRange.head && segmentNum <= offloadedRange.tail)
                        continue;
                }

                // Check if segment exists and is full
                string segmentKey = $"queue_{priority}_seg_{segmentNum}";
                var segment = await StateManager.TryGetStateAsync<Queue<QueueSegmentItem>>(segmentKey);

                if (segment.HasValue && segment.Value.Count == MaxSegmentSize)
                {
                    bool alreadyOffloaded = (offloadedRange.head != null && offloadedRange.tail != null &&
                                            segmentNum >= offloadedRange.head && segmentNum <= offloadedRange.tail);

                    Logger.LogDebug($"[OFFLOAD-ELIGIBLE] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                        $"Full={segment.Value.Count == MaxSegmentSize}, AlreadyOffloaded={alreadyOffloaded}");

                    // Offload this segment (non-blocking on failure)
                    var updatedMetadata = await OffloadSegmentAsync(priority, segmentNum, segment.Value, metadata);
                    if (updatedMetadata != null)
                    {
                        metadata = updatedMetadata;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, $"Error checking/offloading segments for priority {priority} (actor {Id.GetId()})");
        }
    }

    /// <summary>
    /// Check and load offloaded segments that are needed for consumption.
    /// Called before Dequeue. Blocking - throws exceptions on failure to prevent data corruption.
    /// Returns updated metadata if segments were loaded, otherwise returns input metadata.
    /// </summary>
    private async Task<ActorMetadata> CheckAndLoadSegmentsAsync(int priority, ActorMetadata metadata)
    {
        var (head, tail) = GetOffloadedRange(metadata, priority);
        if (head == null || tail == null)
            return metadata;

        if (!metadata.Queues.TryGetValue(priority, out var queueMeta))
            return metadata;

        int headSegment = queueMeta.HeadSegment;
        int bufferSegments = GetBufferSegments(metadata);

        // Calculate which segments should be loaded
        int maxOffloaded = headSegment + bufferSegments;

        Logger.LogDebug($"[LOAD-CHECK] Actor {Id.GetId()}, Priority {priority}: " +
            $"headSegment={headSegment}, bufferSegments={bufferSegments}, " +
            $"maxOffloaded={maxOffloaded}, " +
            $"offloadedRange=({head}, {tail})");

        // Load segments that are within the buffer zone (from head of offloaded range)
        for (int segmentNum = head.Value; segmentNum <= tail.Value; segmentNum++)
        {
            if (segmentNum <= maxOffloaded)
            {
                Logger.LogDebug($"[LOAD-ELIGIBLE] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                    $"segmentNum ({segmentNum}) <= maxOffloaded ({maxOffloaded}), attempting load");

                // LoadOffloadedSegmentAsync now throws on failure instead of returning null
                metadata = await LoadOffloadedSegmentAsync(priority, segmentNum, metadata);
            }
            else
            {
                // Since segments are contiguous, we can break early
                break;
            }
        }

        // Return updated metadata so caller can use it
        return metadata;
    }

}
