namespace DaprMQ.Interfaces;

/// <summary>
/// Configuration for DequeueLocked item-lock expiry and poison-message handling. Locks have no
/// scheduled expiry trigger (no reminder, no timer) - they are swept lazily by whichever operation
/// next touches the actor, so these values bound the work a single operation does rather than a
/// background tick.
/// </summary>
public class LockConfig
{
    /// <summary>
    /// How many times an item may be redelivered by lock/lease expiry before it is routed to the
    /// dead-letter queue instead of being requeued. Matches Azure Service Bus's MaxDeliveryCount
    /// (default 10). Note that because expiry is detected lazily, the count advances per detected
    /// lapse, not per elapsed lock duration - escalation is bounded by consumer activity, not by
    /// wall-clock time.
    /// </summary>
    public required int MaxDeliveryCount { get; init; }

    /// <summary>
    /// Maximum number of lock index entries a single operation's expiry sweep reads, so one Dequeue
    /// can't stall behind a very large backlog. Every entry counts - expired, still live, or already
    /// settled - since each is a state read. Any remainder is picked up by the next operation.
    /// </summary>
    public required int SweepBatchSize { get; init; }

    /// <summary>
    /// Milliseconds a plain queue waits after a sweep that finished within SweepBatchSize before it
    /// sweeps again. Every Dequeue and DequeueLocked sweeps first, so without this a busy queue re-reads
    /// the same due buckets many times a second with nothing to restore. A sweep that used its whole
    /// budget sets no wait: the remainder is swept by the next operation. 0 sweeps on every operation.
    /// </summary>
    public int SweepIntervalMs { get; init; }
}
