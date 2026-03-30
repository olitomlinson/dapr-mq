using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Interface for the QueueActor - a FIFO queue-based Dapr actor with priority support.
/// </summary>
public interface IQueueActor : IActor
{
    /// <summary>
    /// Push an item to the queue with optional priority.
    /// </summary>
    /// <param name="request">Push request containing item JSON and priority</param>
    /// <returns>Push response with success status</returns>
    Task<PushResponse> Push(PushRequest request);

    /// <summary>
    /// Pop one or more items from the queue (FIFO, lowest priority first).
    /// </summary>
    /// <param name="request">Pop request containing count (default: 1, max: 100)</param>
    /// <returns>Pop response with items as JSON strings</returns>
    Task<PopResponse> Pop(PopRequest request);

    /// <summary>
    /// Pop items with acknowledgement requirement (creates a lock).
    /// </summary>
    /// <param name="request">PopWithAck request containing TTL seconds</param>
    /// <returns>PopWithAck response with items, lock info, and status</returns>
    Task<PopWithAckResponse> PopWithAck(PopWithAckRequest request);

    /// <summary>
    /// Acknowledge popped items using lock ID.
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
}
