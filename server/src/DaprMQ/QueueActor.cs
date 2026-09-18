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
    /// True once this actor, when acting as a per-session queue actor (an id containing
    /// "-session-"), has successfully told the SessionCoordinatorActor about itself via
    /// RegisterSession. Checked on every activation so registration happens at most once per
    /// session actor, ever, rather than once per enqueue - registration failure leaves this false so
    /// it retries on the next activation. Meaningless (stays false) for an ordinary, non-session
    /// queue actor.
    /// </summary>
    public bool HasRegisteredSession { get; init; } = false;

    /// <summary>
    /// Enforcement cache for the currently-active session lease, synced here by
    /// SessionCoordinatorActor via SetSessionLease/ClearSessionLease. Null on a plain (non-session)
    /// queue actor, and null on a session actor whenever no lease is currently held. Checked by the
    /// guard on Dequeue/DequeueLocked/Acknowledge/ExtendLock/DeadLetter.
    /// </summary>
    public string? ActiveSessionLeaseId { get; init; }
    public double? ActiveSessionLeaseExpiresAt { get; init; }
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
public class QueueActor : Actor, IQueueActor, IRemindable
{
    private readonly IQueueActorInvoker _actorInvoker;
    private readonly IBlobReaperActorInvoker _blobReaperActorInvoker;
    private readonly ISessionCoordinatorActorInvoker _sessionCoordinatorActorInvoker;
    private readonly IObjectClaimTokenIssuer _objectClaimTokenIssuer;
    private readonly BlobReapConfig _blobReapConfig;
    private readonly IdempotencyConfig _idempotencyConfig;

    private const int MaxSegmentSize = 100;
    private const int MinLockTtlSeconds = 1;
    private const int MaxLockTtlSeconds = 300;
    private const int LockIdLength = 11;
    private const int MaxIdempotencyKeyLength = 128;

    // Naming convention for a per-session queue actor's id: "{queueId}-session-{sessionId}".
    // A session actor recognizes its own role purely from its own Id - no wire-level field is
    // needed on Enqueue/EnqueueItem for this. Same risk class as the existing "-deadletter"/"-sink"
    // conventions: a queueId that happens to contain this marker would be misread as a session
    // actor. Accepted, not solved, consistent with those existing conventions.
    private const string SessionActorIdMarker = "-session-";

    private static bool IsQueueCorrupted(ActorMetadata metadata) =>
        !string.IsNullOrEmpty(metadata.ErrorMessage);

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
        IdempotencyConfig idempotencyConfig) : base(host)
    {
        _actorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
        _blobReaperActorInvoker = blobReaperActorInvoker ?? throw new ArgumentNullException(nameof(blobReaperActorInvoker));
        _sessionCoordinatorActorInvoker = sessionCoordinatorActorInvoker ?? throw new ArgumentNullException(nameof(sessionCoordinatorActorInvoker));
        _objectClaimTokenIssuer = objectClaimTokenIssuer ?? throw new ArgumentNullException(nameof(objectClaimTokenIssuer));
        _blobReapConfig = blobReapConfig ?? throw new ArgumentNullException(nameof(blobReapConfig));
        _idempotencyConfig = idempotencyConfig ?? throw new ArgumentNullException(nameof(idempotencyConfig));
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

        await RegisterAsSessionActorIfNeededAsync(metadata);
    }

    /// <summary>
    /// If this actor's own id identifies it as a per-session queue actor
    /// ("{queueId}-session-{sessionId}") and it hasn't already registered itself, tells the
    /// SessionCoordinatorActor for this queue about this session via RegisterSession - once,
    /// ever, not on every enqueue. Best-effort: a failure is logged and left for the next activation
    /// to retry, it never fails activation itself.
    /// </summary>
    private async Task RegisterAsSessionActorIfNeededAsync(ActorMetadata metadata)
    {
        if (metadata.HasRegisteredSession)
        {
            return;
        }

        string ownId = Id.GetId();
        int markerIndex = ownId.IndexOf(SessionActorIdMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            return; // not a session actor
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
                await SetMetadataAsync(metadata with { HasRegisteredSession = true });
                await StateManager.SaveStateAsync();
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
            ItemJson = itemJson
        };
        segmentQueue.Enqueue(segmentItem);

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

                // Check and offload segments for this priority (non-blocking, best-effort)
                await CheckAndOffloadSegmentsAsync(priority, metadata);
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
            var (response, priority, itemJson) = await DequeueWithPriorityAsync();

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
    /// - itemJson: The JSON string of the dequeued item (null if none)
    /// </summary>
    /// <param name="skipLockCheck">If true, skip the lock check (used for competing consumers)</param>
    private async Task<(DequeueResponse response, int priority, string? itemJson)> DequeueWithPriorityAsync(bool skipLockCheck = false)
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
                var itemJson = segmentItem.ItemJson;

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
                        return (new DequeueResponse { Locked = false, IsEmpty = false }, priority, itemJson);
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
                        return (new DequeueResponse { Locked = false, IsEmpty = false }, priority, itemJson);
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
                    return (new DequeueResponse { Locked = false, IsEmpty = false }, priority, itemJson);
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
    /// Reminder callback for auto-expiring locks.
    /// Implements IRemindable interface.
    /// </summary>
    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        try
        {
            if (reminderName.StartsWith("lock-"))
            {
                string lockId = reminderName[5..];
                Logger.LogDebug("Reminder fired for lock {LockId}, re-queueing item", lockId);

                // Retrieve lock state to get item and priority
                var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");

                if (lockState.HasValue)
                {
                    // Re-queue the item at original priority using EnqueueInternal (stages without saving)
                    try
                    {
                        bool success = await EnqueueInternal(lockState.Value.ItemJson, lockState.Value.Priority);

                        if (!success)
                        {
                            Logger.LogError(
                                "Failed to re-queue expired lock {LockId}. Item: {ItemJson}",
                                lockId,
                                lockState.Value.ItemJson);
                            // Keep lock state - don't clean up on enqueue failure
                            return;
                        }

                        Logger.LogInformation(
                            "Re-queued expired lock {LockId} at priority {Priority}",
                            lockId,
                            lockState.Value.Priority);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex,
                            "Exception re-queueing expired lock {LockId}. Item: {ItemJson}",
                            lockId,
                            lockState.Value.ItemJson);
                        // Keep lock state - don't clean up on exception
                        return;
                    }

                    // Stage lock removal and metadata update (only after successful re-queue)
                    await StateManager.RemoveStateAsync($"{lockId}-lock");

                    // Decrement lock counter
                    var metadata = await GetMetadataAsync();
                    await SetMetadataAsync(metadata with { LockCount = metadata.LockCount - 1 });

                    // Single atomic save: enqueue + lock cleanup + metadata update
                    await StateManager.SaveStateAsync();

                    Logger.LogDebug("Lock {LockId} auto-expired and cleaned up", lockId);
                }
                else
                {
                    // Lock already acknowledged - reminder not unregistered. No action needed.
                    Logger.LogDebug("Lock {LockId} already acknowledged, skipping cleanup", lockId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in ReceiveReminderAsync for reminder {ReminderName}", reminderName);
        }
    }

    /// <summary>
    /// Dequeue items with acknowledgement requirement (creates a lock).
    /// </summary>
    public async Task<DequeueLockedResponse> DequeueLocked(DequeueLockedRequest request)
    {

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
            double lockExpiresAt = nowUnix + ttlSeconds;

            for (int i = 0; i < count; i++)
            {
                // Dequeue item (removes from queue) and store in lock
                // Skip lock check to allow parallel locks
                var (dequeueResult, priority, itemJson) = await DequeueWithPriorityAsync(skipLockCheck: true);

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
                    CompetingConsumerMode = request.AllowCompetingConsumers
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
                    BlobContentType = isItemBlobRef ? blobEnvelope!.ContentType : null
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
            await SetMetadataAsync(metadata);
            await StateManager.SaveStateAsync();

            // Register reminders for auto-expiry (gracefully degrades if scheduler unavailable)
            foreach (var lockId in newLockIds)
            {
                try
                {
                    await RegisterReminderAsync(
                        $"lock-{lockId}",
                        null,
                        TimeSpan.FromSeconds(ttlSeconds),
                        TimeSpan.FromMilliseconds(-1)); // -1 means fire once
                    Logger.LogDebug("Registered reminder for lock {LockId} with TTL {TtlSeconds}s", lockId, ttlSeconds);
                }
                catch (Exception ex)
                {
                    // Scheduler service not available - lock expiry will rely on manual checks
                    Logger.LogDebug(ex, "Reminder registration failed for lock {LockId} (scheduler unavailable)", lockId);
                }
            }

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
    /// Acknowledge dequeued items using lock ID.
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

            // Validate lock_id
            if (string.IsNullOrEmpty(request.LockId))
            {
                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = "lock_id cannot be empty",
                    ErrorCode = "INVALID_LOCK_ID"
                };
            }

            string lockId = request.LockId;

            // Get lock state
            var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
            if (!lockState.HasValue)
            {
                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = "Lock not found",
                    ErrorCode = "LOCK_NOT_FOUND"
                };
            }

            // Note: Item already dequeued during DequeueLocked - just remove lock state
            // Remove lock and decrement counter
            await StateManager.RemoveStateAsync($"{lockId}-lock");

            await SetMetadataAsync(metadata with { LockCount = metadata.LockCount - 1 });

            await StateManager.SaveStateAsync();

            // Unregister reminder (best effort - may not exist if scheduler unavailable)
            try
            {
                await UnregisterReminderAsync($"lock-{lockId}");
                Logger.LogDebug("Unregistered reminder for lock {LockId}", lockId);
            }
            catch (Exception ex)
            {
                // Reminder might not exist or scheduler unavailable - this is OK
                Logger.LogDebug(ex, "Failed to unregister reminder for lock {LockId}", lockId);
            }

            // Acknowledge is the finalization point for DequeueLocked items - if the item's payload
            // was offloaded to an object store, schedule deletion of the underlying blob.
            await ScheduleBlobReapingIfNeeded(lockState.Value.ItemJson);

            Logger.LogDebug($"Acknowledged lock {lockId}, 1 item processed");

            return new AcknowledgeResponse
            {
                Success = true,
                Message = "Successfully acknowledged 1 item",
                ItemsAcknowledged = 1
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
            await StateManager.SaveStateAsync();

            // Update reminder with new TTL (best effort)
            try
            {
                await UnregisterReminderAsync($"lock-{lockId}");
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to unregister old reminder for lock {LockId}", lockId);
            }

            try
            {
                double newTtlSeconds = newExpiresAt - now;
                if (newTtlSeconds > 0)
                {
                    await RegisterReminderAsync(
                        $"lock-{lockId}",
                        null,
                        TimeSpan.FromSeconds(newTtlSeconds),
                        TimeSpan.FromMilliseconds(-1)); // -1 means fire once
                    Logger.LogDebug("Updated reminder for lock {LockId} with new TTL", lockId);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Failed to register updated reminder for lock {LockId}", lockId);
            }

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

            // Get the locked item from lock state (item already dequeued during DequeueLocked)
            string itemJson = lockData.ItemJson;

            // Enqueue to DLQ using actor invoker (enables testing)
            string dlqActorId = $"{Id.GetId()}-deadletter";
            var enqueueRequest = new EnqueueRequest
            {
                Items = new List<EnqueueItem>
                {
                    new EnqueueItem
                    {
                        ItemJson = itemJson,
                        Priority = lockData.Priority
                    }
                }
            };

            var enqueueResult = await _actorInvoker.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                new ActorId(dlqActorId),
                "Enqueue",
                enqueueRequest);

            if (!enqueueResult.Success)
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "DLQ_ENQUEUE_FAILED",
                    Message = "Failed to enqueue item to dead letter queue"
                };
            }

            // Successfully enqueued to DLQ - item already removed from main queue during DequeueLocked
            // Remove lock and decrement counter
            await StateManager.RemoveStateAsync($"{lockId}-lock");

            await SetMetadataAsync(metadata with { LockCount = metadata.LockCount - 1 });

            await StateManager.SaveStateAsync();

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
