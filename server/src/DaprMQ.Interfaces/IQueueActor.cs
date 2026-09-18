using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Interface for the QueueActor - a FIFO queue-based Dapr actor with priority support.
/// </summary>
public interface IQueueActor : IActor
{
    /// <summary>
    /// Enqueue an item to the queue with optional priority.
    /// </summary>
    /// <param name="request">Enqueue request containing item JSON and priority</param>
    /// <returns>Enqueue response with success status</returns>
    Task<EnqueueResponse> Enqueue(EnqueueRequest request);

    /// <summary>
    /// Configures whether this queue honors IdempotencyKey dedup on Enqueue.
    /// </summary>
    Task<ConfigureDedupResponse> ConfigureDedup(ConfigureDedupRequest request);

    Task TestUnsafeUnload(UnsafeUnloadRequest request);

    /// <summary>
    /// Dequeue one or more items from the queue (FIFO, lowest priority first).
    /// </summary>
    /// <param name="request">Dequeue request containing count (default: 1, max: 1000)</param>
    /// <returns>Dequeue response with items as JSON strings</returns>
    Task<DequeueResponse> Dequeue(DequeueRequest request);

    /// <summary>
    /// Dequeue items with acknowledgement requirement (creates a lock).
    /// </summary>
    /// <param name="request">DequeueLocked request containing TTL seconds</param>
    /// <returns>DequeueLocked response with items, lock info, and status</returns>
    Task<DequeueLockedResponse> DequeueLocked(DequeueLockedRequest request);

    /// <summary>
    /// Acknowledge dequeued items using lock ID.
    /// </summary>
    /// <param name="request">Acknowledge request containing lock_id</param>
    /// <returns>Acknowledge response with success status and details</returns>
    Task<AcknowledgeResponse> Acknowledge(AcknowledgeRequest request);

    /// <summary>
    /// Extend an existing lock by adding additional TTL seconds.
    /// </summary>
    /// <param name="request">ExtendLock request containing lock_id and additional_ttl_seconds</param>
    /// <returns>ExtendLock response with success status and new expiration time</returns>
    Task<ExtendLockResponse> ExtendLock(ExtendLockRequest request);

    /// <summary>
    /// Move a locked item to the dead letter queue and void the lock.
    /// </summary>
    /// <param name="request">DeadLetter request containing lock_id</param>
    /// <returns>DeadLetter response with status and DLQ actor ID</returns>
    Task<DeadLetterResponse> DeadLetter(DeadLetterRequest request);

    /// <summary>
    /// Called by this actor's SessionCoordinatorActor, when acting as a per-session queue actor,
    /// to sync the enforcement cache used to gate Dequeue/DequeueLocked/Acknowledge/ExtendLock/DeadLetter
    /// to the current lease holder. Internal, actor-to-actor only - never exposed via
    /// QueueController/gRPC.
    /// </summary>
    Task<SetSessionLeaseResponse> SetSessionLease(SetSessionLeaseRequest request);

    /// <summary>
    /// Called by this actor's SessionCoordinatorActor, best-effort, when a lease on this session
    /// is explicitly released. Internal, actor-to-actor only - never exposed via
    /// QueueController/gRPC.
    /// </summary>
    Task<ClearSessionLeaseResponse> ClearSessionLease();
}
