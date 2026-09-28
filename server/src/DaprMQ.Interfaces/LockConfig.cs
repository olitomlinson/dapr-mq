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
    /// Maximum number of expired locks a single operation resolves synchronously, so one Dequeue
    /// can't stall behind a very large backlog. Any remainder is picked up by the next operation.
    /// </summary>
    public required int SweepBatchSize { get; init; }
}
