using System.Globalization;

namespace DaprMQ.Interfaces;

// Models for the worker-internal, read-only admin reads (GET /internal/admin/*) behind the admin
// console. Every read is a snapshot of committed actor state, taken without calling the actor.
// Timestamps are unix seconds, as stored.

/// <summary>Outcome of an admin read: Ok with a value, NotFound, or Gone (consumed/moved since listed).</summary>
public enum AdminReadStatus
{
    Ok,
    NotFound,
    Gone
}

public record AdminReadResult<T>
{
    public AdminReadStatus Status { get; init; }
    public T? Value { get; init; }

    public static AdminReadResult<T> Ok(T value) => new() { Status = AdminReadStatus.Ok, Value = value };
    public static AdminReadResult<T> NotFound() => new() { Status = AdminReadStatus.NotFound };
    public static AdminReadResult<T> Gone() => new() { Status = AdminReadStatus.Gone };
}

public record QueuePriorityDetail
{
    public int Priority { get; init; }
    public int Count { get; init; }
    public int HeadSegment { get; init; }
    public int TailSegment { get; init; }
    public int? HeadOffloadedSegment { get; init; }
    public int? TailOffloadedSegment { get; init; }
}

public record QueueSinkDetail
{
    public string Url { get; init; } = string.Empty;
    public int MaxConcurrency { get; init; }
    public int LockTtlSeconds { get; init; }
    public int CurrentIntervalSeconds { get; init; }
}

/// <summary>
/// One queue's configuration and counts. The session lease id is never exposed - it authorises
/// settling the session's locks - only when it expires.
/// </summary>
public record QueueDetail
{
    public required string QueueId { get; init; }
    public List<QueuePriorityDetail> Priorities { get; init; } = [];
    public long Ready { get; init; }
    public int Locked { get; init; }
    public long NextSequence { get; init; }
    public int SegmentSize { get; init; }
    public bool DedupEnabled { get; init; }

    /// <summary>Set when the queue is marked corrupt; the actor rejects operations until repaired.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Set on a per-session actor ({queueId}-session-{sessionId}) while a lease is recorded.</summary>
    public double? SessionLeaseExpiresAt { get; init; }

    /// <summary>Sessions in the queue's SessionCoordinatorActor directory.</summary>
    public int SessionCount { get; init; }

    public long DeadLetterReady { get; init; }
    public long DeadLetterLocked { get; init; }

    /// <summary>The global DAPRMQ_MAX_DELIVERY_COUNT.</summary>
    public int MaxDeliveryCount { get; init; }

    /// <summary>The queue's HTTP sink ({queueId}-sink), if one is initialised.</summary>
    public QueueSinkDetail? Sink { get; init; }
}

/// <summary>
/// A ready message. In a page, Text is a preview (up to 1 KB); fetched by location it is the whole
/// item, up to 1 MB. A blob-reference item's Text is its envelope; the payload is fetched separately.
/// </summary>
public record AdminMessage
{
    public required string Location { get; init; }
    public int Priority { get; init; }
    public long Sequence { get; init; }
    public int DeliveryCount { get; init; }
    public long SizeBytes { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool TextTruncated { get; init; }
    public bool IsBlobReference { get; init; }
    public long? BlobSize { get; init; }
    public string? BlobContentType { get; init; }
}

public record AdminMessagePage
{
    public List<AdminMessage> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

/// <summary>A locked message. Text follows the same preview/whole rule as <see cref="AdminMessage"/>.</summary>
public record AdminLock
{
    public required string LockId { get; init; }
    public double CreatedAt { get; init; }
    public double ExpiresAt { get; init; }
    public int Priority { get; init; }
    public long Sequence { get; init; }
    public int DeliveryCount { get; init; }
    public bool CompetingConsumerMode { get; init; }
    public long SizeBytes { get; init; }
    public string Text { get; init; } = string.Empty;
    public bool TextTruncated { get; init; }
    public bool IsBlobReference { get; init; }
    public long? BlobSize { get; init; }
    public string? BlobContentType { get; init; }
}

public record AdminLockPage
{
    public List<AdminLock> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

public record AdminSession
{
    public required string SessionId { get; init; }
    public long Ready { get; init; }
    public int Locked { get; init; }
    public double? LeaseExpiresAt { get; init; }
}

public record AdminSessionPage
{
    public List<AdminSession> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

/// <summary>A subscriber's circuit breaker; absent on a subscriber that is healthy.</summary>
public record AdminCircuitBreaker
{
    public int ConsecutiveFailures { get; init; }
    public double? FirstFailureAt { get; init; }
    public double? NextRetryAt { get; init; }
    public bool Blacklisted { get; init; }
}

public record TopicSubscriberDetail
{
    public required string SubscriberId { get; init; }

    /// <summary>The subscription queue, {topicId}-sub-{subscriberId}.</summary>
    public required string QueueId { get; init; }

    public long DispatchCursor { get; init; }

    /// <summary>Items published but not yet dispatched to this subscriber (NextSequence - DispatchCursor).</summary>
    public long Lag { get; init; }

    public AdminCircuitBreaker? CircuitBreaker { get; init; }
}

public record TopicDetail
{
    public required string TopicId { get; init; }
    public List<TopicSubscriberDetail> Subscribers { get; init; } = [];
    public long NextSequence { get; init; }
    public long OldestSequence { get; init; }
    public long CurrentGeneration { get; init; }

    /// <summary>Items still held in the shared item log (NextSequence - OldestSequence).</summary>
    public long ItemLogCount { get; init; }

    /// <summary>Publishes still being tracked for status (NextPublishSequence - OldestPendingPublishSequence).</summary>
    public long PendingPublishCount { get; init; }
}

/// <summary>An offloaded payload, up to 1 MB, base64-encoded since it may be binary.</summary>
public record AdminPayload
{
    public string? ContentType { get; init; }
    public long SizeBytes { get; init; }
    public string ContentBase64 { get; init; } = string.Empty;
    public bool Truncated { get; init; }
}

/// <summary>
/// Where a ready message sat when it was listed: "{priority}:{segment}:{index}". The index is only a
/// hint - dequeues shift items within the head segment - so a message is found in its segment by
/// Sequence. Segment numbers may be negative.
/// </summary>
public readonly record struct MessageLocation(int Priority, int Segment, int Index)
{
    public override string ToString() => FormattableString.Invariant($"{Priority}:{Segment}:{Index}");

    public static bool TryParse(string? value, out MessageLocation location)
    {
        location = default;
        var parts = value?.Split(':');
        if (parts is not { Length: 3 }
            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var priority)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var segment)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return false;
        }

        location = new MessageLocation(priority, segment, index);
        return true;
    }
}

/// <summary>
/// Resumes a message page after the last item returned: "{priority}:{segment}:{afterSequence}".
/// Keyed by Sequence rather than index, since items are in Sequence order within a priority and
/// indexes shift as the head segment is consumed.
/// </summary>
public readonly record struct MessageCursor(int Priority, int Segment, long AfterSequence)
{
    public override string ToString() => FormattableString.Invariant($"{Priority}:{Segment}:{AfterSequence}");

    public static bool TryParse(string? value, out MessageCursor cursor)
    {
        cursor = default;
        var parts = value?.Split(':');
        if (parts is not { Length: 3 }
            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var priority)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var segment)
            || !long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var after))
        {
            return false;
        }

        cursor = new MessageCursor(priority, segment, after);
        return true;
    }
}
