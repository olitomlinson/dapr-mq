using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapr.Actors;
using Dapr.Actors.Client;
using Dapr.Actors.Runtime;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using DaprMQ.Configuration;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// Actor metadata containing configuration and queue state.
/// </summary>
public record ActorMetadata
{
    public MetadataConfig Config { get; init; } = new();
    public Dictionary<int, QueueMetadata> Queues { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Configuration settings for the actor.
/// </summary>
public record MetadataConfig
{
    public int SegmentSize { get; init; } = 100;
    public int BufferSegments { get; init; } = 1;
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
/// Lock state for PopWithAck operations.
/// Tracks position of locked item instead of storing the item data.
/// </summary>
public record LockState
{
    public required string LockId { get; init; }
    public required double CreatedAt { get; init; }
    public required double ExpiresAt { get; init; }
    public required int Priority { get; init; }
    public required int HeadSegment { get; init; }
}

/// <summary>
/// Represents a segment queued for deletion with timestamp.
/// </summary>
public record PendingDeletion
{
    /// <summary>
    /// External segment key to delete.
    /// Format: "offloaded_queue_{priority}_seg_{segmentNum}_{actorId}"
    /// </summary>
    public required string SegmentKey { get; init; }

    /// <summary>
    /// Unix timestamp (seconds) when deletion was requested.
    /// </summary>
    public required double RequestedAtUnixSeconds { get; init; }
}

/// <summary>
/// Metadata tracking segments queued for deletion from external storage.
/// Separate from ActorMetadata to isolate cleanup state.
/// </summary>
public record SegmentDeletionMetadata
{
    /// <summary>
    /// List of pending deletions with timestamps.
    /// Using List instead of Queue for efficient filtering by timestamp.
    /// </summary>
    public List<PendingDeletion> PendingDeletions { get; init; } = new();
}

/// <summary>
/// QueueActor - A FIFO queue-based Dapr actor with priority support.
/// Implements segmented storage (100 items per segment) for scalable queue operations.
/// </summary>
public class QueueActor : Actor, IQueueActor
{
    private readonly IActorInvoker _actorInvoker;

    private const int MaxSegmentSize = 100;
    private const int MinLockTtlSeconds = 1;
    private const int MaxLockTtlSeconds = 300;
    private const int DefaultLockTtlSeconds = 30;
    private const int LockIdLength = 11;
    private const string OffloadSegmentCleanupTimerName = "segment-cleanup-timer";
    private const int OffloadSegmentCleanupScanIntervalSeconds = 60;
    private const string OffloadSegmentCleanupMetadataKey = "segment_deletion_metadata";
    private const int OffloadSegmentRetentionSeconds = 60;

    private static bool IsQueueCorrupted(ActorMetadata metadata) =>
        !string.IsNullOrEmpty(metadata.ErrorMessage);

    /// <summary>
    /// Get the segment retention period in seconds from environment variable or default.
    /// Environment variable: SEGMENT_DELETION_RETENTION_SECONDS
    /// </summary>
    private static int GetRetentionSeconds()
    {
        var envVar = Environment.GetEnvironmentVariable("SEGMENT_DELETION_RETENTION_SECONDS");
        if (int.TryParse(envVar, out var seconds) && seconds >= 0)
        {
            return seconds;
        }
        return OffloadSegmentRetentionSeconds;
    }

    /// <summary>
    /// Get the cleanup scan interval in seconds from environment variable or default.
    /// Environment variable: SEGMENT_CLEANUP_SCAN_INTERVAL_SECONDS
    /// </summary>
    private static int GetCleanupScanIntervalSeconds()
    {
        var envVar = Environment.GetEnvironmentVariable("SEGMENT_CLEANUP_SCAN_INTERVAL_SECONDS");
        if (int.TryParse(envVar, out var seconds) && seconds >= 1)
        {
            return seconds;
        }
        return OffloadSegmentCleanupScanIntervalSeconds;
    }

    /// <summary>
    /// Get current Unix timestamp in seconds.
    /// </summary>
    private static double GetUnixTimestampSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public QueueActor(ActorHost host, IActorInvoker actorInvoker) : base(host)
    {
        _actorInvoker = actorInvoker ?? throw new ArgumentNullException(nameof(actorInvoker));
    }

    /// <summary>
    /// Called when the actor is activated. Initializes metadata structure if it doesn't exist.
    /// </summary>
    protected override async Task OnActivateAsync()
    {
        // Initialize metadata structure if it doesn't exist
        var metadataExists = await StateManager.TryGetStateAsync<ActorMetadata>("metadata");
        if (!metadataExists.HasValue)
        {
            var initialMetadata = new ActorMetadata
            {
                Config = new MetadataConfig
                {
                    SegmentSize = MaxSegmentSize,
                    BufferSegments = 1
                },
                Queues = new Dictionary<int, QueueMetadata>()
            };
            await StateManager.SetStateAsync("metadata", initialMetadata);
            await StateManager.SaveStateAsync();
            Logger.LogDebug("Actor activated and metadata initialized");
        }
        else
        {
            Logger.LogDebug("Actor activated with existing metadata");
        }

        // Register cleanup timer (not durable, must re-register on each activation)
        var scanInterval = GetCleanupScanIntervalSeconds();
        await RegisterTimerAsync(
            OffloadSegmentCleanupTimerName,
            nameof(OffloadSegmentCleanupAsync),
            null,
            TimeSpan.FromSeconds(scanInterval),  // Due time (first run)
            TimeSpan.FromSeconds(scanInterval)); // Period (recurring)

        Logger.LogDebug("Actor {ActorId}: Registered cleanup timer ({ScanInterval}s interval)", Id.GetId(), scanInterval);
    }

    /// <summary>
    /// Internal push that stages changes without committing.
    /// Returns true if push succeeded, false otherwise.
    /// </summary>
    private async Task<bool> PushInternal(string itemJson, int priority)
    {
        // Validation
        if (string.IsNullOrEmpty(itemJson))
        {
            Logger.LogWarning("Push failed: ItemJson is empty");
            return false;
        }

        if (priority < 0)
        {
            Logger.LogWarning($"Push failed: priority must be >= 0, got {priority}");
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
        var segment = await StateManager.TryGetStateAsync<Queue<string>>(segmentKey);
        var segmentQueue = segment.HasValue ? segment.Value : new Queue<string>();

        // Check if segment is full BEFORE appending
        if (segmentQueue.Count >= MaxSegmentSize)
        {
            // Allocate new segment
            tailSegment++;
            segmentKey = $"queue_{priority}_seg_{tailSegment}";
            segmentQueue = new Queue<string>();
        }

        // Append item to segment (FIFO)
        segmentQueue.Enqueue(itemJson);

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

        Logger.LogDebug($"Staged push to priority {priority}, count now {count}");

        return true;
    }

    /// <summary>
    /// Push an item to the queue with optional priority.
    /// </summary>
    public async Task<PushResponse> Push(PushRequest request)
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
            // Push and stage changes
            bool success = await PushInternal(request.ItemJson, request.Priority);

            if (!success)
            {
                return new PushResponse { Success = false };
            }

            Logger.LogDebug($"Pushed item to queue at priority {request.Priority}");

            // Check and offload segments if eligible (non-blocking, best-effort)
            await CheckAndOffloadSegmentsAsync(request.Priority, metadata);

            // Commit staged changes atomically (push + any offload changes)
            await StateManager.SaveStateAsync();

            return new PushResponse { Success = true };
        }
        catch (InvalidOperationException)
        {
            // Re-throw corruption errors - queue must be repaired before continuing
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in Push");
            return new PushResponse { Success = false };
        }
    }

    /// <summary>
    /// Pop a single item from the queue (FIFO, lowest priority first).
    /// </summary>
    public async Task<PopResponse> Pop()
    {

        var metadata = await GetMetadataAsync();

        // Check for corrupted state
        if (IsQueueCorrupted(metadata))
        {
            throw new InvalidOperationException($"Queue corrupted: {metadata.ErrorMessage}");
        }

        var (response, priority) = await PopWithPriorityAsync();
        await StateManager.SaveStateAsync();  // Commit the staged changes atomically
        return response with { Priority = priority };
    }

    /// <summary>
    /// Internal Pop method that returns both the response and the priority from which the item was popped.
    /// This is used by PopWithAck to track the original priority for expired lock restoration.
    /// </summary>
    private async Task<(PopResponse response, int priority)> PopWithPriorityAsync()
    {

        var metadata = await GetMetadataAsync();

        try
        {
            // Check if queue is locked
            var lockState = await StateManager.TryGetStateAsync<LockState>("_active_lock");
            if (lockState.HasValue)
            {
                var lockData = lockState.Value;
                double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (now < lockData.ExpiresAt)
                {
                    Logger.LogDebug("Queue is locked, cannot pop");
                    return (new PopResponse
                    {
                        ItemJson = null,
                        Locked = true,
                        IsEmpty = false,
                        Message = "Queue is locked by another operation",
                        LockExpiresAt = lockData.ExpiresAt
                    }, -1);
                }
                else
                {
                    // Lock expired - return items and clear lock
                    await HandleExpiredLockAsync(lockData);
                }
            }

            if (metadata.Queues.Count == 0)
            {
                return (new PopResponse { ItemJson = null, Locked = false, IsEmpty = true }, -1);
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
                var segment = await StateManager.TryGetStateAsync<Queue<string>>(segmentKey);

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

                // Pop single item from front (FIFO)
                var segmentQueue = segment.Value;
                var itemJson = segmentQueue.Dequeue();

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

                        Logger.LogDebug($"Popped item from priority {priority}, count now {count}");

                        // Return item JSON string directly with priority
                        return (new PopResponse { ItemJson = itemJson, Locked = false, IsEmpty = false }, priority);
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

                        Logger.LogDebug($"Popped last item from priority {priority}, queue now empty");

                        // Return item JSON string directly with priority
                        return (new PopResponse { ItemJson = itemJson, Locked = false, IsEmpty = false }, priority);
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

                    Logger.LogDebug($"Popped item from priority {priority}, count now {count}");

                    // Return item JSON string directly with priority
                    return (new PopResponse { ItemJson = itemJson, Locked = false, IsEmpty = false }, priority);
                }
            }

            return (new PopResponse { ItemJson = null, Locked = false, IsEmpty = true }, -1);
        }
        catch (InvalidOperationException)
        {
            // Re-throw corruption errors - queue must be repaired before continuing
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in PopAsync");
            return (new PopResponse { ItemJson = null, Locked = false, IsEmpty = true }, -1);
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
    /// Handles expired lock by removing it from state.
    /// Stages the lock removal but does NOT commit - caller must call SaveStateAsync().
    /// </summary>
    private async Task HandleExpiredLockAsync(LockState lockData)
    {
        // Lock expired - items remain at front of queue automatically (lock-in-place architecture)
        Logger.LogDebug($"Lock {lockData.LockId} expired, items remain at front of queue");
        await StateManager.RemoveStateAsync("_active_lock");
        // Note: SaveStateAsync() NOT called - staged change committed by caller
    }

    /// <summary>
    /// Internal Peek method that returns items without dequeuing, along with their priority and segment.
    /// Used by PopWithAck to implement lock-in-place architecture.
    /// </summary>
    private async Task<(PopResponse response, int priority, int headSegment)> PeekWithPriorityAsync()
    {
        try
        {
            // Check if queue is locked
            var lockState = await StateManager.TryGetStateAsync<LockState>("_active_lock");
            if (lockState.HasValue)
            {
                var lockData = lockState.Value;
                double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (now < lockData.ExpiresAt)
                {
                    Logger.LogDebug("Queue is locked, cannot peek");
                    return (new PopResponse
                    {
                        ItemJson = null,
                        Locked = true,
                        IsEmpty = false,
                        Message = "Queue is locked by another operation",
                        LockExpiresAt = lockData.ExpiresAt
                    }, -1, -1);
                }
                else
                {
                    // Lock expired - return items and clear lock
                    await HandleExpiredLockAsync(lockData);
                }
            }

            var metadata = await GetMetadataAsync();

            if (metadata.Queues.Count == 0)
            {
                return (new PopResponse { ItemJson = null, Locked = false, IsEmpty = true }, -1, -1);
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

                // Get head segment
                string segmentKey = $"queue_{priority}_seg_{headSegment}";
                var segment = await StateManager.TryGetStateAsync<Queue<string>>(segmentKey);

                if (!segment.HasValue || segment.Value.Count == 0)
                {
                    // Defensive: skip empty segment
                    Logger.LogWarning($"Count desync detected for priority {priority}, skipping");
                    continue;
                }

                // Peek single item from front (don't dequeue)
                var segmentQueue = segment.Value;
                var itemJson = segmentQueue.Peek();

                Logger.LogDebug($"Peeked item from priority {priority}, segment {headSegment}");

                // Return item JSON string directly with priority and segment
                return (new PopResponse { ItemJson = itemJson, Locked = false, IsEmpty = false }, priority, headSegment);
            }

            return (new PopResponse { ItemJson = null, Locked = false, IsEmpty = true }, -1, -1);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in PeekWithPriorityAsync");
            return (new PopResponse { ItemJson = null, Locked = false, IsEmpty = true }, -1, -1);
        }
    }

    /// <summary>
    /// Pop items with acknowledgement requirement (creates a lock).
    /// </summary>
    public async Task<PopWithAckResponse> PopWithAck(PopWithAckRequest request)
    {

        var metadata = await GetMetadataAsync();

        // Check for corrupted state
        if (IsQueueCorrupted(metadata))
        {
            throw new InvalidOperationException($"Queue corrupted: {metadata.ErrorMessage}");
        }


        try
        {
            // Get TTL (default 30, clamped to 1-300)
            int ttlSeconds = Math.Max(MinLockTtlSeconds, Math.Min(MaxLockTtlSeconds, request.TtlSeconds));

            // Check if already locked
            var lockState = await StateManager.TryGetStateAsync<LockState>("_active_lock");
            if (lockState.HasValue)
            {
                var existingLock = lockState.Value;
                double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                if (now < existingLock.ExpiresAt)
                {
                    return new PopWithAckResponse
                    {
                        Locked = true,
                        LockExpiresAt = existingLock.ExpiresAt,
                        Message = "Queue is locked by another operation"
                    };
                }
                else
                {
                    // Expired lock - return items first
                    await HandleExpiredLockAsync(existingLock);
                }
            }

            // Peek items (don't dequeue) and track priority and position
            var (peekResult, priority, headSegment) = await PeekWithPriorityAsync();

            if (peekResult.ItemJson == null)
            {
                return new PopWithAckResponse
                {
                    Locked = false,
                    IsEmpty = true,
                    Message = "Queue is empty"
                };
            }

            // Create lock (tracks position, not item data)
            string lockId = GenerateLockId();
            double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double lockExpiresAt = nowUnix + ttlSeconds;

            var lockData = new LockState
            {
                LockId = lockId,
                CreatedAt = nowUnix,
                ExpiresAt = lockExpiresAt,
                Priority = priority,
                HeadSegment = headSegment
            };

            await StateManager.SetStateAsync("_active_lock", lockData);
            await StateManager.SaveStateAsync();

            Logger.LogDebug($"Created lock {lockId} with TTL {ttlSeconds}s, expires at {lockExpiresAt}");

            return new PopWithAckResponse
            {
                ItemJson = peekResult.ItemJson,
                Priority = priority,
                Locked = true,
                IsEmpty = false,
                LockId = lockId,
                LockExpiresAt = lockExpiresAt,
                Message = $"Item locked with ID {lockId}"
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in PopWithAckAsync");
            return new PopWithAckResponse
            {
                ItemJson = null,
                Locked = false,
                IsEmpty = true,
                Message = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Acknowledge popped items using lock ID.
    /// </summary>
    public async Task<AcknowledgeResponse> Acknowledge(AcknowledgeRequest request)
    {
        try
        {
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
            var lockState = await StateManager.TryGetStateAsync<LockState>("_active_lock");
            if (!lockState.HasValue)
            {
                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = "Lock not found",
                    ErrorCode = "LOCK_NOT_FOUND"
                };
            }

            var lockData = lockState.Value;

            // Validate lock ID matches
            if (lockData.LockId != lockId)
            {
                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = "Invalid lock_id",
                    ErrorCode = "INVALID_LOCK_ID"
                };
            }

            // Check if lock expired
            double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (now >= lockData.ExpiresAt)
            {
                // Lock expired - return items to queue
                await HandleExpiredLockAsync(lockData);
                await StateManager.SaveStateAsync();

                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = "Lock has expired",
                    ErrorCode = "LOCK_EXPIRED"
                };
            }

            // Acknowledge - now dequeue the locked item (lock-in-place architecture)
            var metadata = await GetMetadataAsync();

            if (!metadata.Queues.TryGetValue(lockData.Priority, out var queueMeta))
            {
                // Priority queue no longer exists - clear lock and return error
                await StateManager.RemoveStateAsync("_active_lock");
                await StateManager.SaveStateAsync();

                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = "Priority queue no longer exists",
                    ErrorCode = "QUEUE_NOT_FOUND"
                };
            }

            string segmentKey = $"queue_{lockData.Priority}_seg_{lockData.HeadSegment}";
            var segment = await StateManager.TryGetStateAsync<Queue<string>>(segmentKey);

            if (!segment.HasValue || segment.Value.Count == 0)
            {
                // Segment no longer exists or is empty - clear lock and return error
                await StateManager.RemoveStateAsync("_active_lock");
                await StateManager.SaveStateAsync();

                return new AcknowledgeResponse
                {
                    Success = false,
                    Message = "Locked item no longer exists in queue",
                    ErrorCode = "ITEM_NOT_FOUND"
                };
            }

            // Dequeue the single locked item
            var segmentQueue = segment.Value;
            segmentQueue.Dequeue();

            int headSegment = queueMeta.HeadSegment;
            int tailSegment = queueMeta.TailSegment;
            int count = queueMeta.Count - 1;

            // Handle segment cleanup (same logic as PopWithPriorityAsync)
            if (segmentQueue.Count == 0)
            {
                if (headSegment < tailSegment)
                {
                    // More segments exist, move to next
                    await StateManager.RemoveStateAsync(segmentKey);

                    int oldHeadSegment = headSegment;
                    headSegment++;

                    queueMeta = queueMeta with
                    {
                        HeadSegment = headSegment,
                        TailSegment = tailSegment,
                        Count = count
                    };
                    metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [lockData.Priority] = queueMeta } };
                    await SetMetadataAsync(metadata);

                    Logger.LogDebug($"[HEAD-ADVANCE] Actor {Id.GetId()}, Priority {lockData.Priority}: " +
                        $"headSegment advancing from {oldHeadSegment} to {headSegment}, " +
                        $"tailSegment={tailSegment}, count={count}, " +
                        $"offloadedRange=({queueMeta.HeadOffloadedSegment}, {queueMeta.TailOffloadedSegment})");
                }
                else
                {
                    // Last segment empty, queue is now empty
                    await StateManager.RemoveStateAsync(segmentKey);

                    var updatedQueues = new Dictionary<int, QueueMetadata>(metadata.Queues);
                    updatedQueues.Remove(lockData.Priority);
                    metadata = metadata with { Queues = updatedQueues };
                    await SetMetadataAsync(metadata);
                }
            }
            else
            {
                // Segment still has items, save it
                await StateManager.SetStateAsync(segmentKey, segmentQueue);

                queueMeta = queueMeta with
                {
                    HeadSegment = headSegment,
                    TailSegment = tailSegment,
                    Count = count
                };
                metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [lockData.Priority] = queueMeta } };
                await SetMetadataAsync(metadata);
            }

            // Remove lock and commit all changes atomically
            await StateManager.RemoveStateAsync("_active_lock");
            await StateManager.SaveStateAsync();

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
            var lockState = await StateManager.TryGetStateAsync<LockState>("_active_lock");
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

            // Validate lock ID matches
            if (lockData.LockId != lockId)
            {
                return new ExtendLockResponse
                {
                    Success = false,
                    NewExpiresAt = 0,
                    ErrorCode = "INVALID_LOCK_ID",
                    ErrorMessage = "Invalid lock_id"
                };
            }

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
            await StateManager.SetStateAsync("_active_lock", updatedLock);
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

    public async Task<DeadLetterResponse> DeadLetter(DeadLetterRequest request)
    {
        try
        {
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
            var lockState = await StateManager.TryGetStateAsync<LockState>("_active_lock");
            if (!lockState.HasValue)
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "LOCK_NOT_FOUND",
                    Message = "No active lock found"
                };
            }

            var lockData = lockState.Value;

            // Validate lock ID matches
            if (lockData.LockId != lockId)
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "INVALID_LOCK_ID",
                    Message = "Invalid lock_id provided"
                };
            }

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

            // Get the locked item from the segment using Peek()
            string segmentKey = $"queue_{lockData.Priority}_seg_{lockData.HeadSegment}";
            var segment = await StateManager.TryGetStateAsync<Queue<string>>(segmentKey);

            // Get item data without removing it (Peek instead of Dequeue)
            var segmentQueue = segment.Value;
            string itemJson = segmentQueue.Peek();

            // Push to DLQ using actor invoker (enables testing)
            string dlqActorId = $"{Id.GetId()}-deadletter";
            var pushRequest = new PushRequest
            {
                ItemJson = itemJson,
                Priority = lockData.Priority
            };

            var pushResult = await _actorInvoker.InvokeMethodAsync<PushRequest, PushResponse>(
                new ActorId(dlqActorId),
                "Push",
                pushRequest);

            if (!pushResult.Success)
            {
                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "DLQ_PUSH_FAILED",
                    Message = "Failed to push item to dead letter queue"
                };
            }

            // Successfully pushed to DLQ - now remove item from main queue
            // Dequeue the item and update metadata (same pattern as Acknowledge)
            var metadata = await GetMetadataAsync();

            if (!metadata.Queues.TryGetValue(lockData.Priority, out var queueMeta))
            {
                // Priority queue no longer exists - clear lock and return error
                await StateManager.RemoveStateAsync("_active_lock");
                await StateManager.SaveStateAsync();

                return new DeadLetterResponse
                {
                    Status = "ERROR",
                    ErrorCode = "QUEUE_NOT_FOUND",
                    Message = "Priority queue no longer exists"
                };
            }

            // Dequeue the item from the segment
            segmentQueue.Dequeue();

            int headSegment = queueMeta.HeadSegment;
            int tailSegment = queueMeta.TailSegment;
            int count = queueMeta.Count - 1;

            // Handle segment cleanup (same logic as Acknowledge)
            if (segmentQueue.Count == 0)
            {
                if (headSegment < tailSegment)
                {
                    // More segments exist, move to next
                    await StateManager.RemoveStateAsync(segmentKey);
                    headSegment++;

                    queueMeta = queueMeta with
                    {
                        HeadSegment = headSegment,
                        TailSegment = tailSegment,
                        Count = count
                    };
                    metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [lockData.Priority] = queueMeta } };
                    await SetMetadataAsync(metadata);
                }
                else
                {
                    // Last segment is now empty - remove priority queue entirely
                    await StateManager.RemoveStateAsync(segmentKey);
                    var queues = new Dictionary<int, QueueMetadata>(metadata.Queues);
                    queues.Remove(lockData.Priority);
                    metadata = metadata with { Queues = queues };
                    await SetMetadataAsync(metadata);
                }
            }
            else
            {
                // Segment still has items - update segment and metadata
                await StateManager.SetStateAsync(segmentKey, segmentQueue);
                queueMeta = queueMeta with
                {
                    HeadSegment = headSegment,
                    TailSegment = tailSegment,
                    Count = count
                };
                metadata = metadata with { Queues = new Dictionary<int, QueueMetadata>(metadata.Queues) { [lockData.Priority] = queueMeta } };
                await SetMetadataAsync(metadata);
            }

            // Remove lock
            await StateManager.RemoveStateAsync("_active_lock");
            await StateManager.SaveStateAsync();

            return new DeadLetterResponse
            {
                Status = "SUCCESS",
                DlqActorId = dlqActorId,
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
    /// Generate state store key for offloaded segment.
    /// Format: offloaded_queue_{priority}_seg_{segmentNum}_{actorId}
    /// </summary>
    private string GetOffloadedSegmentKey(int priority, int segmentNum)
    {
        return $"offloaded_queue_{priority}_seg_{segmentNum}_{Id.GetId()}";
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
    private async Task<ActorMetadata?> OffloadSegmentAsync(int priority, int segmentNum, Queue<string> segmentData, ActorMetadata metadata)
    {
        try
        {
            string offloadKey = GetOffloadedSegmentKey(priority, segmentNum);

            Logger.LogDebug($"[OFFLOAD-START] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                $"Offloading to key '{offloadKey}'");

            // Serialize segment data to UTF-8 byte array
            byte[] segmentBytes = JsonSerializer.SerializeToUtf8Bytes(segmentData);

            // Save to state store using DaprClient (raw bytes - no double serialization)
            using var client = new DaprClientBuilder().Build();
            await client.SaveByteStateAsync("statestore", offloadKey, segmentBytes);

            // Add to offloaded range in metadata
            var updatedMetadata = AddOffloadedSegment(metadata, priority, segmentNum);

            // Delete from actor state manager
            string segmentKey = $"queue_{priority}_seg_{segmentNum}";
            await StateManager.RemoveStateAsync(segmentKey);

            // Save metadata (staging only - commit happens in caller)
            await SetMetadataAsync(updatedMetadata);

            Logger.LogDebug($"[OFFLOAD-SUCCESS] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                $"Saved to external store, deleting from actor state");

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
    /// Returns updated metadata if successful, null otherwise (logs error).
    /// </summary>
    private async Task<ActorMetadata?> LoadOffloadedSegmentAsync(int priority, int segmentNum, ActorMetadata metadata)
    {
        try
        {
            string offloadKey = GetOffloadedSegmentKey(priority, segmentNum);

            Logger.LogDebug($"[LOAD-START] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                $"Loading from key '{offloadKey}'");

            // Load segment from external state store
            using var client = new DaprClientBuilder().Build();
            var result = await client.GetByteStateAsync("statestore", offloadKey);

            if (result.IsEmpty)
            {
                // Store error in metadata using record 'with' syntax
                string errorMsg = $"CORRUPTED: Segment {segmentNum} priority {priority} missing from external store. " +
                                  $"Offloaded range: {GetOffloadedRange(metadata, priority)}. " +
                                  $"Actor ID: {Id.GetId()}. " +
                                  $"Manual intervention required.";

                var corruptedMetadata = metadata with { ErrorMessage = errorMsg };
                await SetMetadataAsync(corruptedMetadata);
                await StateManager.SaveStateAsync();  // Persist error state immediately

                Logger.LogCritical($"[LOAD-MISSING] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                    $"Segment missing from external store! Offloaded range: {GetOffloadedRange(metadata, priority)}");

                throw new InvalidOperationException(errorMsg);
            }

            // Deserialize from UTF-8 bytes
            var segmentData = JsonSerializer.Deserialize<Queue<string>>(result.Span);

            if (segmentData == null || segmentData.Count == 0)
            {
                // Store error in metadata using record 'with' syntax
                string errorMsg = $"CORRUPTED: Empty data for offloaded segment {segmentNum} priority {priority}. " +
                                  $"Offloaded range: {GetOffloadedRange(metadata, priority)}. " +
                                  $"Actor ID: {Id.GetId()}. " +
                                  $"Manual intervention required.";

                var corruptedMetadata = metadata with { ErrorMessage = errorMsg };
                await SetMetadataAsync(corruptedMetadata);
                await StateManager.SaveStateAsync();  // Persist error state immediately

                Logger.LogCritical($"Queue corrupted: {errorMsg}");
                throw new InvalidOperationException(errorMsg);
            }

            // Save to actor state manager
            string segmentKey = $"queue_{priority}_seg_{segmentNum}";
            await StateManager.SetStateAsync(segmentKey, segmentData);

            // Remove from offloaded range
            var updatedMetadata = RemoveOffloadedSegment(metadata, priority, segmentNum);

            // Queue for async deletion (safer than immediate delete)
            await QueueSegmentForDeletionAsync(offloadKey);

            // Save metadata (staging only - commit happens in caller)
            await SetMetadataAsync(updatedMetadata);

            Logger.LogDebug($"[LOAD-SUCCESS] Actor {Id.GetId()}, Segment {segmentNum}, Priority {priority}: " +
                $"Loaded {segmentData.Count} items, queued for deletion");

            return updatedMetadata;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"Failed to load offloaded segment {segmentNum} for priority {priority} (actor {Id.GetId()})");
            return null;
        }
    }

    /// <summary>
    /// Queue an external segment for deletion with timestamp. Safer than immediate deletion.
    /// The segment will be deleted by the cleanup timer after retention period elapses.
    /// </summary>
    private async Task QueueSegmentForDeletionAsync(string segmentKey)
    {
        try
        {
            // Load current deletion metadata
            var deletionMetadata = await StateManager.TryGetStateAsync<SegmentDeletionMetadata>(OffloadSegmentCleanupMetadataKey);
            var currentMetadata = deletionMetadata.HasValue
                ? deletionMetadata.Value
                : new SegmentDeletionMetadata();

            // Check if already queued (avoid duplicates)
            var alreadyQueued = currentMetadata.PendingDeletions.Any(pd => pd.SegmentKey == segmentKey);

            if (!alreadyQueued)
            {
                var updatedList = new List<PendingDeletion>(currentMetadata.PendingDeletions);
                var pendingDeletion = new PendingDeletion
                {
                    SegmentKey = segmentKey,
                    RequestedAtUnixSeconds = GetUnixTimestampSeconds()
                };
                updatedList.Add(pendingDeletion);

                var updatedMetadata = currentMetadata with { PendingDeletions = updatedList };
                await StateManager.SetStateAsync(OffloadSegmentCleanupMetadataKey, updatedMetadata);

                var retentionSeconds = GetRetentionSeconds();
                Logger.LogDebug($"Actor {Id.GetId()}: Queued '{segmentKey}' for deletion " +
                    $"(list size: {updatedList.Count}, retention: {retentionSeconds}s)");
            }
            else
            {
                Logger.LogDebug($"Actor {Id.GetId()}: Segment '{segmentKey}' already queued for deletion");
            }

            // Note: State will be saved by caller's SaveStateAsync
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"Failed to queue segment '{segmentKey}' for deletion");
            // Don't throw - deletion queue is best-effort
        }
    }

    /// <summary>
    /// Actor Timer callback: Process pending segment deletions from external storage.
    /// Runs every 60 seconds. Only deletes segments after retention period has elapsed.
    /// Handles failures gracefully (retries transient errors, removes already-deleted segments).
    /// </summary>
    private async Task OffloadSegmentCleanupAsync()
    {
        try
        {
            // Load deletion metadata
            var deletionMetadata = await StateManager.TryGetStateAsync<SegmentDeletionMetadata>(OffloadSegmentCleanupMetadataKey);
            if (!deletionMetadata.HasValue || deletionMetadata.Value.PendingDeletions.Count == 0)
            {
                Logger.LogDebug($"Actor {Id.GetId()}: No pending deletions");
                return;
            }

            var retentionSeconds = GetRetentionSeconds();
            var currentTimestamp = GetUnixTimestampSeconds();

            var pendingList = deletionMetadata.Value.PendingDeletions;
            var originalCount = pendingList.Count;

            // Separate eligible (past retention) from not ready
            var eligibleDeletions = pendingList
                .Where(pd => (currentTimestamp - pd.RequestedAtUnixSeconds) >= retentionSeconds)
                .ToList();
            var notReadyDeletions = pendingList
                .Where(pd => (currentTimestamp - pd.RequestedAtUnixSeconds) < retentionSeconds)
                .ToList();

            var successCount = 0;
            var alreadyDeletedCount = 0;
            var failedCount = 0;
            var notReadyCount = notReadyDeletions.Count;

            Logger.LogInformation($"Actor {Id.GetId()}: Processing {originalCount} pending deletion(s) " +
                $"(retention: {retentionSeconds}s, eligible: {eligibleDeletions.Count}, not ready: {notReadyCount})");

            using var client = new DaprClientBuilder().Build();
            var failedDeletions = new List<PendingDeletion>();

            // Process eligible deletions
            foreach (var pendingDeletion in eligibleDeletions)
            {
                var elapsedSeconds = currentTimestamp - pendingDeletion.RequestedAtUnixSeconds;

                try
                {
                    await client.DeleteStateAsync("statestore", pendingDeletion.SegmentKey);
                    successCount++;
                    Logger.LogDebug($"Actor {Id.GetId()}: Deleted '{pendingDeletion.SegmentKey}' " +
                        $"from state store (aged {elapsedSeconds:F0}s)");
                }
                catch (Exception ex)
                {
                    // Check if segment was already deleted (not found error)
                    if (IsAlreadyDeletedError(ex))
                    {
                        alreadyDeletedCount++;
                        Logger.LogDebug($"Actor {Id.GetId()}: Segment '{pendingDeletion.SegmentKey}' " +
                            $"already deleted, removing from list");
                        // Don't re-add - segment is gone
                    }
                    else
                    {
                        // Transient error - keep in list for retry
                        failedCount++;
                        failedDeletions.Add(pendingDeletion);
                        Logger.LogWarning(ex, $"Actor {Id.GetId()}: Failed to delete '{pendingDeletion.SegmentKey}', " +
                            $"keeping in list for retry");
                    }
                }
            }

            // Log not-ready segments (debug only, don't log all in production)
            if (notReadyCount > 0 && notReadyCount <= 5)
            {
                foreach (var notReady in notReadyDeletions.Take(5))
                {
                    var elapsedSeconds = currentTimestamp - notReady.RequestedAtUnixSeconds;
                    var remainingSeconds = retentionSeconds - elapsedSeconds;
                    Logger.LogDebug($"Actor {Id.GetId()}: Segment '{notReady.SegmentKey}' " +
                        $"not ready for deletion ({remainingSeconds:F0}s remaining)");
                }
            }

            // Build updated list: not ready + failed (for retry)
            var updatedList = new List<PendingDeletion>(notReadyDeletions.Count + failedDeletions.Count);
            updatedList.AddRange(notReadyDeletions);
            updatedList.AddRange(failedDeletions);

            // Update deletion metadata with remaining list
            var updatedMetadata = deletionMetadata.Value with { PendingDeletions = updatedList };
            await StateManager.SetStateAsync(OffloadSegmentCleanupMetadataKey, updatedMetadata);
            await StateManager.SaveStateAsync();

            Logger.LogInformation($"Actor {Id.GetId()}: Cleanup complete - " +
                $"Success: {successCount}, Already deleted: {alreadyDeletedCount}, " +
                $"Failed: {failedCount}, Not ready: {notReadyCount}, Remaining: {updatedList.Count}");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"Actor {Id.GetId()}: Critical error in cleanup timer callback");
            // Timer will retry on next interval
        }
    }

    /// <summary>
    /// Check if exception indicates segment was already deleted (not found).
    /// </summary>
    private static bool IsAlreadyDeletedError(Exception ex)
    {
        // Dapr returns RpcException with StatusCode.NotFound for missing keys
        // Check exception message for "not found" or similar indicators
        var message = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return message.Contains("not found") ||
               message.Contains("does not exist") ||
               message.Contains("404");
    }

    /// <summary>
    /// Check and offload eligible segments for a priority queue.
    /// Called after Push. Non-blocking - failures are logged but don't throw.
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
                var segment = await StateManager.TryGetStateAsync<Queue<string>>(segmentKey);

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
    /// Called before Pop. Blocking - throws exceptions on failure to prevent data corruption.
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
