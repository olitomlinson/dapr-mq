using System.Text.Json;

namespace DaprMQ.Client;

public record EnqueueItemDto(object Item, int Priority = 1, string? IdempotencyKey = null, string? SessionId = null);

public record EnqueueResult(bool Success, string Message, int ItemsEnqueued, int ItemsDeduplicated);

public record DequeueLockedItemDto(JsonElement Item, int Priority, string LockId, double LockExpiresAt);

public record DequeueLockedResult(IReadOnlyList<DequeueLockedItemDto> Items, bool Locked, string? Message);

public record SessionLease(string SessionId, string LeaseId, double LeaseExpiresAt);

/// <summary>
/// One delivered, locked item from a <see cref="IDaprMQClient.ConsumeSessionAsync"/> stream.
/// There is no LeaseId here - the ConsumeSession wire protocol never exposes one to the client
/// (the server tracks the lease internally and applies it when it calls Acknowledge/DeadLetter
/// on the caller's behalf), so AckAsync/DeadLetterAsync are the only way to resolve this item.
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
}

public record DaprMQClientOptions
{
    public required Uri HttpBaseAddress { get; init; }
    public required string GrpcAddress { get; init; }
}
