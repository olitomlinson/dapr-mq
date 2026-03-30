using System.Security.Cryptography;
using System.Text;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
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
    public int LockCount { get; init; } = 0;
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
/// Queue segment item containing item data and optional sink configuration.
/// </summary>
public record QueueSegmentItem
{
    public required string ItemJson { get; init; }
    public SinkConfig? Sink { get; init; }
}

/// <summary>
/// Lock state for PopWithAck operations.
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
    public SinkConfig? Sink { get; init; }  // Optional sink configuration
}

/// <summary>
/// QueueActor - A FIFO queue-based Dapr actor with priority support.
/// Implements segmented storage (100 items per segment) for scalable queue operations.
/// </summary>
public class QueueActor : Actor, IQueueActor, IRemindable
{
    private readonly IQueueActorInvoker _actorInvoker;

    private const int MaxSegmentSize = 100;
    private const int MinLockTtlSeconds = 1;
    private const int MaxLockTtlSeconds = 300;
    private const int LockIdLength = 11;

    private static bool IsQueueCorrupted(ActorMetadata metadata) =>
        !string.IsNullOrEmpty(metadata.ErrorMessage);

    public QueueActor(ActorHost host, IQueueActorInvoker queueActorInvoker) : base(host)
    {
        _actorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
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
                Queues = new Dictionary<int, QueueMetadata>(),
                LockCount = 0
            };
            await StateManager.SetStateAsync("metadata", initialMetadata);
            await StateManager.SaveStateAsync();
            Logger.LogDebug("Actor activated and metadata initialized");
        }
        else
        {
            Logger.LogDebug("Actor activated with existing metadata");
        }
    }

    /// <summary>
    /// Internal push that stages changes without committing.
    /// Returns true if push succeeded, false otherwise.
    /// </summary>
    private async Task<bool> PushInternal(string itemJson, int priority, SinkConfig? sink = null)
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
            Sink = sink
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

        Logger.LogDebug($"Staged push to priority {priority}, count now {count}");

        return true;
    }

    /// <summary>
    /// Push items to the queue with optional priority per item.
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
            // Validate items array
            if (request.Items == null || request.Items.Count == 0)
            {
                Logger.LogWarning("Push failed: Items array is empty or null");
                return new PushResponse
                {
                    Success = false,
                    ItemsPushed = 0,
                    ErrorMessage = "Items array cannot be empty"
                };
            }

            if (request.Items.Count > 1000)
            {
                Logger.LogWarning($"Push failed: Items count {request.Items.Count} exceeds maximum of 1000");
                return new PushResponse
                {
                    Success = false,
                    ItemsPushed = 0,
                    ErrorMessage = "Maximum 1000 items per push"
                };
            }

            // Validate all items before processing (fail fast)
            foreach (var item in request.Items)
            {
                if (string.IsNullOrEmpty(item.ItemJson))
                {
                    Logger.LogWarning("Push failed: Item JSON is empty");
                    return new PushResponse
                    {
                        Success = false,
                        ItemsPushed = 0,
                        ErrorMessage = "Item JSON cannot be empty"
                    };
                }

                if (item.Priority < 0)
                {
                    Logger.LogWarning($"Push failed: Priority {item.Priority} must be >= 0");
                    return new PushResponse
                    {
                        Success = false,
                        ItemsPushed = 0,
                        ErrorMessage = $"Priority must be >= 0, got {item.Priority}"
                    };
                }
            }

            // Group by priority and process in order
            var groupedItems = request.Items
                .GroupBy(item => item.Priority)
                .OrderBy(g => g.Key);

            int totalPushed = 0;
            var processedPriorities = new HashSet<int>();

            // Process each priority group
            foreach (var group in groupedItems)
            {
                int priority = group.Key;
                processedPriorities.Add(priority);

                foreach (var item in group)
                {
                    // Push and stage changes (reuse existing PushInternal)
                    bool success = await PushInternal(item.ItemJson, priority, item.Sink);

                    if (!success)
                    {
                        // All-or-nothing: if any item fails, rollback not needed
                        // because SaveStateAsync hasn't been called yet
                        Logger.LogError($"Push failed for item at priority {priority}");
                        return new PushResponse
                        {
                            Success = false,
                            ItemsPushed = 0,
                            ErrorMessage = "Failed to push item"
                        };
                    }

                    totalPushed++;
                }

                // Check and offload segments for this priority (non-blocking, best-effort)
                await CheckAndOffloadSegmentsAsync(priority, metadata);
            }

            // Commit all staged changes atomically (all items across all priorities)
            await StateManager.SaveStateAsync();

            Logger.LogDebug($"Pushed {totalPushed} items across {processedPriorities.Count} priorities");

            return new PushResponse
            {
                Success = true,
                ItemsPushed = totalPushed
            };
        }
        catch (InvalidOperationException)
        {
            // Re-throw corruption errors - queue must be repaired before continuing
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in Push");
            return new PushResponse
            {
                Success = false,
                ItemsPushed = 0,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Pop one or more items from the queue (FIFO, lowest priority first).
    /// </summary>
    public async Task<PopResponse> Pop(PopRequest request)
    {
        var metadata = await GetMetadataAsync();

        // Check for corrupted state
        if (IsQueueCorrupted(metadata))
        {
            throw new InvalidOperationException($"Queue corrupted: {metadata.ErrorMessage}");
        }

        // Validate count parameter
        if (request.Count < 0 || request.Count > 100)
        {
            return new PopResponse
            {
                Items = new List<PopItem>(),
                IsEmpty = false,
                Locked = false,
                Message = "Count must be between 0 and 100"
            };
        }

        var items = new List<PopItem>();

        // Pop up to Count items
        for (int i = 0; i < request.Count; i++)
        {
            var (response, priority, itemJson, _) = await PopWithPriorityAsync();

            // If locked, return what we have so far with lock info
            if (response.Locked)
            {
                await StateManager.SaveStateAsync();
                return new PopResponse
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
                return new PopResponse
                {
                    Items = items,
                    Locked = false,
                    IsEmpty = items.Count == 0  // Only mark empty if we didn't pop anything
                };
            }

            // Add the popped item
            items.Add(new PopItem
            {
                ItemJson = itemJson!,
                Priority = priority
            });
        }

        // Commit all changes atomically
        await StateManager.SaveStateAsync();

        return new PopResponse
        {
            Items = items,
            Locked = false,
            IsEmpty = false
        };
    }

    /// <summary>
    /// Internal Pop method that returns item JSON, priority, and response metadata.
    /// This is used by PopWithAck to track the original priority for expired lock restoration.
    /// Returns a tuple where:
    /// - response: Contains only metadata (Locked, IsEmpty, Message, LockExpiresAt)
    /// - priority: The priority level the item was popped from
    /// - itemJson: The JSON string of the popped item (null if none)
    /// </summary>
    /// <param name="skipLockCheck">If true, skip the lock check (used for competing consumers)</param>
    private async Task<(PopResponse response, int priority, string? itemJson, SinkConfig? sink)> PopWithPriorityAsync(bool skipLockCheck = false)
    {

        var metadata = await GetMetadataAsync();

        try
        {
            // Check if queue is locked (any active lock blocks Pop)
            if (!skipLockCheck)
            {
                if (metadata.LockCount > 0)
                {
                    Logger.LogDebug("Queue is locked, cannot pop");
                    return (new PopResponse
                    {
                        Locked = true,
                        IsEmpty = false,
                        Message = "Queue is locked by another operation"
                    }, -1, null, null);
                }
            }

            if (metadata.Queues.Count == 0)
            {
                return (new PopResponse { Locked = false, IsEmpty = true }, -1, null, null);
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

                // Pop single item from front (FIFO)
                var segmentQueue = segment.Value;
                var segmentItem = segmentQueue.Dequeue();
                var itemJson = segmentItem.ItemJson;
                var sink = segmentItem.Sink;

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
                        return (new PopResponse { Locked = false, IsEmpty = false }, priority, itemJson, sink);
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
                        return (new PopResponse { Locked = false, IsEmpty = false }, priority, itemJson, sink);
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
                    return (new PopResponse { Locked = false, IsEmpty = false }, priority, itemJson, sink);
                }
            }

            return (new PopResponse { Locked = false, IsEmpty = true }, -1, null, null);
        }
        catch (InvalidOperationException)
        {
            // Re-throw corruption errors - queue must be repaired before continuing
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in PopAsync");
            return (new PopResponse { Locked = false, IsEmpty = true }, -1, null, null);
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
                    // Re-queue the item at original priority using PushInternal (stages without saving)
                    try
                    {
                        bool success = await PushInternal(lockState.Value.ItemJson, lockState.Value.Priority, lockState.Value.Sink);

                        if (!success)
                        {
                            Logger.LogError(
                                "Failed to re-queue expired lock {LockId}. Item: {ItemJson}",
                                lockId,
                                lockState.Value.ItemJson);
                            // Keep lock state - don't clean up on push failure
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

                    // Single atomic save: push + lock cleanup + metadata update
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

            // Get count (default 1, clamped to 1-100)
            int count = Math.Max(1, Math.Min(100, request.Count));

            // Apply MaxConcurrency limit if specified
            if (request.MaxConcurrency.HasValue)
            {
                int availableCapacity = Math.Max(0, request.MaxConcurrency.Value - metadata.LockCount);
                count = Math.Min(count, availableCapacity);

                if (count == 0)
                {
                    return new PopWithAckResponse
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
                return new PopWithAckResponse
                {
                    Locked = true,
                    Message = "Queue is locked by another operation"
                };
            }

            // Competing consumer mode: proceed regardless of existing locks

            // Pop multiple items and create locks
            var lockedItems = new List<PopWithAckItem>();
            var newLockIds = new List<string>();
            double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double lockExpiresAt = nowUnix + ttlSeconds;

            for (int i = 0; i < count; i++)
            {
                // Dequeue item (removes from queue) and store in lock
                // Skip lock check to allow parallel locks
                var (popResult, priority, itemJson, sink) = await PopWithPriorityAsync(skipLockCheck: true);

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
                    Sink = sink
                };

                await StateManager.SetStateAsync($"{lockId}-lock", lockData);

                // Track lock ID for reminder registration
                newLockIds.Add(lockId);

                // Add to response items
                lockedItems.Add(new PopWithAckItem
                {
                    ItemJson = itemJson,
                    Priority = priority,
                    LockId = lockId,
                    LockExpiresAt = lockExpiresAt,
                    Sink = sink
                });

                Logger.LogDebug("Created lock {LockId} ({Index}/{Count}) with TTL {TtlSeconds}s", lockId, i + 1, count, ttlSeconds);
            }

            // Check if we got any items
            if (lockedItems.Count == 0)
            {
                return new PopWithAckResponse
                {
                    Locked = false,
                    IsEmpty = true,
                    Message = "Queue is empty"
                };
            }

            // Re-fetch metadata to get staged updates from pop loop (count decrements, headSegment advancements)
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

            return new PopWithAckResponse
            {
                Items = lockedItems,
                Locked = false,
                IsEmpty = false,
                Message = $"Locked {lockedItems.Count} item(s)"
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in PopWithAckAsync");
            return new PopWithAckResponse
            {
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

            // Note: Item already dequeued during PopWithAck - just remove lock state
            // Remove lock and decrement counter
            await StateManager.RemoveStateAsync($"{lockId}-lock");

            var metadata = await GetMetadataAsync();
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

            // Get the locked item from lock state (item already dequeued during PopWithAck)
            string itemJson = lockData.ItemJson;

            // Push to DLQ using actor invoker (enables testing)
            string dlqActorId = $"{Id.GetId()}-deadletter";
            var pushRequest = new PushRequest
            {
                Items = new List<PushItem>
                {
                    new PushItem
                    {
                        ItemJson = itemJson,
                        Priority = lockData.Priority
                    }
                }
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

            // Successfully pushed to DLQ - item already removed from main queue during PopWithAck
            // Remove lock and decrement counter
            await StateManager.RemoveStateAsync($"{lockId}-lock");

            var metadata = await GetMetadataAsync();
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
