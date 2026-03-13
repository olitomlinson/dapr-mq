namespace DaprMQ.Interfaces;

/// <summary>
/// Request model for Push operation.
/// </summary>
public record PushRequest
{
    /// <summary>
    /// The item to push (as JSON string).
    /// </summary>
    public string ItemJson { get; init; } = string.Empty;

    /// <summary>
    /// Priority level (0 = highest priority, default: 1).
    /// </summary>
    public int Priority { get; init; } = 1;
}

/// <summary>
/// Response model for Push operation.
/// </summary>
public record PushResponse
{
    /// <summary>
    /// Whether the push was successful.
    /// </summary>
    public bool Success { get; init; }
}

/// <summary>
/// Request model for PopWithAck operation.
/// </summary>
public record PopWithAckRequest
{
    /// <summary>
    /// Lock TTL in seconds (1-300).
    /// </summary>
    public int TtlSeconds { get; init; } = 30;
}

/// <summary>
/// Response model for Pop operation.
/// </summary>
public record PopResponse
{
    /// <summary>
    /// The popped item (as JSON string), or null if queue is empty or locked.
    /// </summary>
    public string? ItemJson { get; init; }

    /// <summary>
    /// Priority of the popped item (null if no item was popped).
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>
    /// Whether the queue is locked.
    /// </summary>
    public bool Locked { get; init; }

    /// <summary>
    /// Whether the queue is empty.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Unix timestamp when lock expires.
    /// </summary>
    public double? LockExpiresAt { get; init; }
}

/// <summary>
/// Response model for PopWithAck operation.
/// </summary>
public record PopWithAckResponse
{
    /// <summary>
    /// The popped item (as JSON string), or null if queue is empty or locked.
    /// </summary>
    public string? ItemJson { get; init; }

    /// <summary>
    /// Priority of the popped item (null if no item was popped).
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>
    /// Whether the queue is locked.
    /// </summary>
    public bool Locked { get; init; }

    /// <summary>
    /// Whether the queue is empty.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>
    /// Lock ID for acknowledgement (if locked).
    /// </summary>
    public string? LockId { get; init; }

    /// <summary>
    /// Unix timestamp when lock expires.
    /// </summary>
    public double? LockExpiresAt { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Request model for Acknowledge operation.
/// </summary>
public record AcknowledgeRequest
{
    /// <summary>
    /// Lock ID to acknowledge.
    /// </summary>
    public string LockId { get; init; } = string.Empty;
}

/// <summary>
/// Response model for Acknowledge operation.
/// </summary>
public record AcknowledgeResponse
{
    /// <summary>
    /// Whether the acknowledgement was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Number of items acknowledged.
    /// </summary>
    public int ItemsAcknowledged { get; init; }

    /// <summary>
    /// Error code (if not successful).
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Request model for ExtendLock operation.
/// </summary>
public record ExtendLockRequest
{
    /// <summary>
    /// Lock ID to extend.
    /// </summary>
    public string LockId { get; init; } = string.Empty;

    /// <summary>
    /// Additional TTL in seconds to add to the lock.
    /// </summary>
    public int AdditionalTtlSeconds { get; init; } = 30;
}

/// <summary>
/// Response model for ExtendLock operation.
/// </summary>
public record ExtendLockResponse
{
    /// <summary>
    /// Whether the lock extension was successful.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// New expiration timestamp (Unix seconds) after extension.
    /// </summary>
    public double NewExpiresAt { get; init; }

    /// <summary>
    /// Error code (if not successful).
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Error message (if not successful).
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request model for DeadLetter operation.
/// </summary>
public record DeadLetterRequest
{
    /// <summary>
    /// Lock ID of the item to move to dead letter queue.
    /// </summary>
    public string LockId { get; init; } = string.Empty;
}

/// <summary>
/// Response model for DeadLetter operation.
/// </summary>
public record DeadLetterResponse
{
    /// <summary>
    /// Status of the operation ("SUCCESS" or "ERROR").
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Error code (if Status is "ERROR").
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Dead letter queue actor ID where the item was moved.
    /// </summary>
    public string? DlqActorId { get; init; }
}
