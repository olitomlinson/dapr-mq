namespace DaprMQ.Interfaces;

/// <summary>
/// Request model for Push operation.
/// </summary>
public record PushRequest
{
    /// <summary>
    /// Array of items to push.
    /// </summary>
    public List<PushItem> Items { get; init; } = new();
}

/// <summary>
/// Individual item in a Push request.
/// </summary>
public record PushItem
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

    /// <summary>
    /// Number of items pushed.
    /// </summary>
    public int ItemsPushed { get; init; }

    /// <summary>
    /// Error message if not successful.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request model for Pop operation.
/// </summary>
public record PopRequest
{
    /// <summary>
    /// Number of items to pop (1-100, default: 1).
    /// </summary>
    public int Count { get; init; } = 1;
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

    /// <summary>
    /// Number of items to pop with acknowledgement (1-100, default: 1).
    /// </summary>
    public int Count { get; init; } = 1;

    /// <summary>
    /// Whether to allow competing consumers (multiple parallel locks). Default: false (legacy single-lock behavior).
    /// </summary>
    public bool AllowCompetingConsumers { get; init; } = false;

    /// <summary>
    /// Optional limit on total concurrent locks in the queue. If specified, PopWithAck will return at most (MaxConcurrency - current LockCount) items.
    /// </summary>
    public int? MaxConcurrency { get; init; }
}

/// <summary>
/// Individual item in a Pop response.
/// </summary>
public record PopItem
{
    /// <summary>
    /// The item (as JSON string).
    /// </summary>
    public required string ItemJson { get; init; }

    /// <summary>
    /// Priority of the item.
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>
    /// If the item's payload was offloaded to an object store, an opaque claim token to fetch it
    /// via the object download endpoint. Null for normal inline items. Never a raw BlobReference.
    /// </summary>
    public string? ObjectClaimToken { get; init; }
}

/// <summary>
/// Response model for Pop operation.
/// </summary>
public record PopResponse
{
    /// <summary>
    /// Array of popped items (empty if queue is empty or locked).
    /// </summary>
    public List<PopItem> Items { get; init; } = new();

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
    /// Unix timestamp when lock expires (for locked queues).
    /// </summary>
    public double? LockExpiresAt { get; init; }
}

/// <summary>
/// Individual item in a PopWithAck response.
/// </summary>
public record PopWithAckItem
{
    /// <summary>
    /// The item (as JSON string).
    /// </summary>
    public required string ItemJson { get; init; }

    /// <summary>
    /// Priority of the item.
    /// </summary>
    public required int Priority { get; init; }

    /// <summary>
    /// Lock ID for acknowledgement.
    /// </summary>
    public required string LockId { get; init; }

    /// <summary>
    /// Unix timestamp when lock expires.
    /// </summary>
    public required double LockExpiresAt { get; init; }

    /// <summary>
    /// If the item's payload was offloaded to an object store, an opaque claim token to fetch it
    /// via the object download endpoint. Null for normal inline items. Never a raw BlobReference.
    /// </summary>
    public string? ObjectClaimToken { get; init; }

    /// <summary>
    /// Content type recorded at push-object time, if this item is a blob reference. Null for
    /// normal inline items or if no content type was supplied. Also embedded inside the claim
    /// token itself, but surfaced here too so callers don't need to decode the token to know it.
    /// </summary>
    public string? BlobContentType { get; init; }
}

/// <summary>
/// Response model for PopWithAck operation.
/// </summary>
public record PopWithAckResponse
{
    /// <summary>
    /// Array of locked items (empty if queue is empty or locked).
    /// </summary>
    public List<PopWithAckItem> Items { get; init; } = new();

    /// <summary>
    /// Whether the queue is locked.
    /// </summary>
    public bool Locked { get; init; }

    /// <summary>
    /// Whether the queue is empty.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>
    /// Whether the MaxConcurrency limit has been reached. This is set to true when the queue has reached the maximum number of concurrent locks specified in the PopWithAck request.
    /// </summary>
    public bool MaxConcurrencyReached { get; init; }

    /// <summary>
    /// Status message.
    /// </summary>
    public string? Message { get; init; }

    // Legacy properties for backward compatibility (deprecated)
    /// <summary>
    /// [DEPRECATED] Use Items[0].ItemJson. The popped item (as JSON string), or null if queue is empty or locked.
    /// </summary>
    public string? ItemJson => Items.Count > 0 ? Items[0].ItemJson : null;

    /// <summary>
    /// [DEPRECATED] Use Items[0].Priority. Priority of the popped item (null if no item was popped).
    /// </summary>
    public int? Priority => Items.Count > 0 ? Items[0].Priority : null;

    /// <summary>
    /// [DEPRECATED] Use Items[0].LockId. Lock ID for acknowledgement (if locked).
    /// </summary>
    public string? LockId => Items.Count > 0 ? Items[0].LockId : null;

    /// <summary>
    /// [DEPRECATED] Use Items[0].LockExpiresAt. Unix timestamp when lock expires.
    /// </summary>
    public double? LockExpiresAt => Items.Count > 0 ? Items[0].LockExpiresAt : null;
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

public record UnsafeUnloadRequest
{
    /// <summary>
    /// Lock ID to extend.
    /// </summary>
    public string Lol { get; init; } = string.Empty;
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
    /// Dead letter queue ID where the item was moved.
    /// </summary>
    public string? DlqId { get; init; }
}

/// <summary>
/// Request model for initializing an HTTP sink actor.
/// </summary>
public record InitializeHttpSinkRequest
{
    /// <summary>
    /// HTTP endpoint URL where messages will be delivered.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Queue actor ID to poll from.
    /// </summary>
    public required string QueueActorId { get; init; }

    /// <summary>
    /// Maximum number of concurrent locks in the queue (1-100).
    /// </summary>
    public required int MaxConcurrency { get; init; }

    /// <summary>
    /// Lock TTL in seconds for PopWithAck operations (1-300).
    /// </summary>
    public required int LockTtlSeconds { get; init; }
}

/// <summary>
/// Request model for scheduling deletion of an offloaded blob on the BlobReaperActor.
/// </summary>
public record ScheduleDeletionRequest
{
    /// <summary>
    /// The blob reference (object store key/URI) to delete.
    /// </summary>
    public required string BlobReference { get; init; }

    /// <summary>
    /// Delay in seconds before attempting deletion.
    /// </summary>
    public required int DelaySeconds { get; init; }
}

/// <summary>
/// Request model for postponing a scheduled deletion on the BlobReaperActor.
/// </summary>
public record PostponeDeletionRequest
{
    /// <summary>
    /// The blob reference (object store key/URI) whose deletion should be postponed.
    /// </summary>
    public required string BlobReference { get; init; }

    /// <summary>
    /// Delay in seconds from now to reschedule deletion to. Only applied if later than the
    /// currently scheduled deletion time.
    /// </summary>
    public required int NewDelaySeconds { get; init; }
}

/// <summary>
/// Request model for publishing items to a topic.
/// </summary>
public record PublishRequest
{
    public List<PushItem> Items { get; init; } = new();
}

/// <summary>
/// Response model for a Publish operation. Async accept, not a delivery receipt.
/// </summary>
public record PublishResponse
{
    public bool Accepted { get; init; }
    public required string PublishId { get; init; }
    public required long Sequence { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request model for subscribing to a topic. Optionally registers a push (HTTP sink) delivery
/// on the subscriber's provisioned queue in the same call - equivalent to calling Subscribe then
/// separately registering a sink via the existing queue sink endpoints.
/// </summary>
public record SubscribeRequest
{
    public required string SubscriberId { get; init; }
    public TopicHttpSinkConfig? HttpSink { get; init; }
}

/// <summary>
/// HTTP sink configuration for a topic subscription. Mirrors InitializeHttpSinkRequest, minus
/// QueueActorId - TopicActor supplies that itself from the subscription it just provisioned.
/// </summary>
public record TopicHttpSinkConfig
{
    public required string Url { get; init; }
    public int MaxConcurrency { get; init; } = 5;
    public int LockTtlSeconds { get; init; } = 30;
}

/// <summary>
/// Response model for a Subscribe operation.
/// </summary>
public record SubscribeResponse
{
    public bool Success { get; init; }
    public required string QueueActorId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request model for unsubscribing from a topic.
/// </summary>
public record UnsubscribeRequest
{
    public required string SubscriberId { get; init; }
}

/// <summary>
/// Response model for an Unsubscribe operation.
/// </summary>
public record UnsubscribeResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Response model listing a topic's current subscribers.
/// </summary>
public record ListSubscribersResponse
{
    public List<string> SubscriberIds { get; init; } = new();
}

/// <summary>
/// Request model for reading a publish's relay status.
/// </summary>
public record GetPublishStatusRequest
{
    public required string PublishId { get; init; }
}

/// <summary>
/// Response model describing a publish's relay status across its target subscribers.
/// </summary>
public record PublishStatusResponse
{
    public bool Found { get; init; }
    public bool Complete { get; init; }
    public List<string> TargetSubscriberIds { get; init; } = new();
    public List<string> DeliveredSubscriberIds { get; init; } = new();
}

/// <summary>
/// Request model for resetting a subscriber's circuit breaker.
/// </summary>
public record ResetCircuitBreakerRequest
{
    public required string SubscriberId { get; init; }
}

/// <summary>
/// Response model for a ResetCircuitBreaker operation.
/// </summary>
public record ResetCircuitBreakerResponse
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request model for reading a subscriber's circuit breaker status.
/// </summary>
public record GetCircuitBreakerStatusRequest
{
    public required string SubscriberId { get; init; }
}

/// <summary>
/// Response model describing a subscriber's circuit breaker status.
/// </summary>
public record CircuitBreakerStatusResponse
{
    public bool Found { get; init; }
    public int ConsecutiveFailures { get; init; }
    public double? FirstFailureAt { get; init; }
    public double? NextRetryAt { get; init; }
    public bool Blacklisted { get; init; }
}
