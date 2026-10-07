using System.Text.Json;

namespace DaprMQ.Client;

public record EnqueueItemDto(object Item, int Priority = 1, string? IdempotencyKey = null, string? SessionId = null);

public record EnqueueResult(bool Success, string Message, int ItemsEnqueued, int ItemsDeduplicated);

public record DequeueLockedItemDto(JsonElement Item, int Priority, string LockId, double LockExpiresAt);

public record DequeueLockedResult(IReadOnlyList<DequeueLockedItemDto> Items, bool Locked, string? Message);

public record NackResult(bool DeadLettered, int DeliveryCount, string? DlqId);

/// <summary>
/// Result of <see cref="IDaprMQClient.AcknowledgeBatchAsync"/>: one entry per requested lock, in
/// request order.
/// </summary>
public record AcknowledgeBatchResult(int ItemsAcknowledged, IReadOnlyList<LockAcknowledgeResult> Results);

/// <summary>Outcome of one lock in a batch acknowledge; see <see cref="AcknowledgeOutcomes"/>.</summary>
public record LockAcknowledgeResult(string LockId, string Outcome);

/// <summary>Per-lock outcomes of a batch acknowledge.</summary>
public static class AcknowledgeOutcomes
{
    /// <summary>Settled by this call.</summary>
    public const string Acknowledged = "ACKNOWLEDGED";

    /// <summary>
    /// No such lock: never existed, or already settled. After the client retried a batch whose
    /// outcome was unknown, this can mean the earlier attempt acknowledged it.
    /// </summary>
    public const string LockNotFound = "LOCK_NOT_FOUND";

    /// <summary>Plain queue only: the lock's TTL passed, so the item is returning to the queue.</summary>
    public const string LockExpired = "LOCK_EXPIRED";

    /// <summary>Empty lock id.</summary>
    public const string InvalidLockId = "INVALID_LOCK_ID";
}

public record SessionLease(string SessionId, string LeaseId, double LeaseExpiresAt);

/// <summary>
/// One delivered, locked item from a <see cref="IDaprMQClient.ConsumeSessionAsync"/> stream.
/// There is no LeaseId here - the ConsumeSession wire protocol never exposes one to the client
/// (the server tracks the lease internally and applies it when it calls Acknowledge/DeadLetter
/// on the caller's behalf), so AckAsync/DeadLetterAsync/NackAsync are the only way to resolve this item.
/// </summary>
public sealed class SessionDelivery
{
    public required string SessionId { get; init; }
    public required string LockId { get; init; }
    public required JsonElement Item { get; init; }
    public required int Priority { get; init; }
    public required double LockExpiresAt { get; init; }
    public required Func<CancellationToken, Task> AckAsync { get; init; }
    public required Func<CancellationToken, Task> DeadLetterAsync { get; init; }

    /// <summary>Returns the item to the front of the session for redelivery.</summary>
    public required Func<CancellationToken, Task> NackAsync { get; init; }
}

public record DaprMQClientOptions
{
    public required Uri HttpBaseAddress { get; init; }
    public required string GrpcAddress { get; init; }
    public DaprMQRetryOptions Retry { get; init; } = new();
}

/// <summary>How calls ride out a DaprMQ that can't serve them yet (sdks/testing/RETRIES_AND_READINESS.md).</summary>
public record DaprMQRetryOptions
{
    /// <summary>
    /// How long one call may keep retrying a DaprMQ that can't serve it (also sent to the server as
    /// its retry window). Never cuts a call that was delivered short. Zero turns client retries off.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Give each enqueued item without an IdempotencyKey a fresh one, so an enqueue whose outcome is
    /// unknown is retried safely. Costs the server one extra state write per item.
    /// </summary>
    public bool AutoIdempotencyKeys { get; init; }

    /// <summary>No attempt starts with less left: the server takes ~5 s to report it can't serve.</summary>
    internal TimeSpan MinAttemptWindow { get; init; } = TimeSpan.FromSeconds(6);

    internal TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(100);

    internal TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(2);
}
