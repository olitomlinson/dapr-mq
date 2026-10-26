using System.Text;
using Dapr.Actors;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// The read-only admin reads behind the worker's GET /internal/admin/* (the admin console). Every
/// read goes through the actor-state API, so it never calls or activates an actor and never takes
/// a turn from Enqueue/Dequeue/Ack; what it returns is a snapshot of committed state.
/// </summary>
public interface IAdminReadService
{
    Task<AdminReadResult<QueueDetail>> GetQueueAsync(string queueId, CancellationToken cancellationToken = default);

    /// <summary>Ready messages, by priority then queue order, resuming after <paramref name="cursor"/>.</summary>
    Task<AdminReadResult<AdminMessagePage>> ListMessagesAsync(string queueId, int? priority, MessageCursor? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>The message with <paramref name="sequence"/> in the segment at <paramref name="location"/>; Gone if it has left it.</summary>
    Task<AdminReadResult<AdminMessage>> GetMessageAsync(string queueId, MessageLocation location, long sequence, CancellationToken cancellationToken = default);

    Task<AdminReadResult<AdminPayload>> GetMessagePayloadAsync(string queueId, MessageLocation location, long sequence, CancellationToken cancellationToken = default);

    /// <summary>Outstanding locks from position <paramref name="offset"/> of the lock index.</summary>
    Task<AdminReadResult<AdminLockPage>> ListLocksAsync(string queueId, int offset, int limit, CancellationToken cancellationToken = default);

    Task<AdminReadResult<AdminLock>> GetLockAsync(string queueId, string lockId, CancellationToken cancellationToken = default);

    Task<AdminReadResult<AdminPayload>> GetLockPayloadAsync(string queueId, string lockId, CancellationToken cancellationToken = default);

    /// <summary>One page of the queue's session directory, by session id, with each session's depth and lease.</summary>
    Task<AdminReadResult<AdminSessionPage>> ListSessionsAsync(string queueId, string? afterSessionId, int limit, CancellationToken cancellationToken = default);

    Task<AdminReadResult<TopicDetail>> GetTopicAsync(string topicId, CancellationToken cancellationToken = default);
}

/// <summary>The actor type names the admin reads address state under.</summary>
public record AdminActorTypes
{
    public string Queue { get; init; } = "QueueActor";
    public string Topic { get; init; } = "TopicActor";
    public string HttpSink { get; init; } = "HttpSinkActor";
}

public class AdminReadService : IAdminReadService
{
    public const int PreviewChars = 1024;
    public const int MaxTextChars = 1024 * 1024;
    public const int MaxPayloadBytes = 1024 * 1024;

    // The bucketed lock index over-approximates (settled ids stay until the sweep), so a page may
    // read past dead ids; this bounds the reads one page can cost.
    private const int MaxLockReadsPerLimit = 4;

    private const string MetadataKey = "metadata";

    private readonly IQueueActorStateReader _reader;
    private readonly AdminActorTypes _types;
    private readonly LockConfig _lockConfig;
    private readonly IObjectStore _objectStore;

    public AdminReadService(IQueueActorStateReader reader, AdminActorTypes types, LockConfig lockConfig, IObjectStore objectStore)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _lockConfig = lockConfig ?? throw new ArgumentNullException(nameof(lockConfig));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<QueueDetail>> GetQueueAsync(string queueId, CancellationToken cancellationToken = default)
    {
        var metadataTask = ReadQueueMetadataAsync(queueId, cancellationToken);
        var directoryTask = _reader.ReadSessionDirectoryAsync(queueId, cancellationToken);
        var sinkTask = _reader.ReadStateAsync<HttpSinkActorState>(_types.HttpSink, $"{queueId}-sink", HttpSinkActor.StateKey, cancellationToken);
        var isDeadLetter = queueId.EndsWith(QueueActor.DeadLetterActorIdSuffix, StringComparison.Ordinal);
        var deadLetterTask = isDeadLetter
            ? Task.FromResult(new QueueDepth(0, 0))
            : _reader.ReadQueueDepthAsync(new ActorId(queueId + QueueActor.DeadLetterActorIdSuffix), cancellationToken);
        await Task.WhenAll(metadataTask, directoryTask, sinkTask, deadLetterTask);

        var metadata = metadataTask.Result;
        var sessionCount = directoryTask.Result.Count;
        // A queue that only ever had session enqueues has no metadata of its own, only a directory.
        if (metadata == null && sessionCount == 0)
        {
            return AdminReadResult<QueueDetail>.NotFound();
        }

        metadata ??= new ActorMetadata();
        var sink = sinkTask.Result;
        return AdminReadResult<QueueDetail>.Ok(new QueueDetail
        {
            QueueId = queueId,
            Priorities = metadata.Queues.OrderBy(q => q.Key).Select(q => new QueuePriorityDetail
            {
                Priority = q.Key,
                Count = q.Value.Count,
                HeadSegment = q.Value.HeadSegment,
                TailSegment = q.Value.TailSegment,
                HeadOffloadedSegment = q.Value.HeadOffloadedSegment,
                TailOffloadedSegment = q.Value.TailOffloadedSegment
            }).ToList(),
            Ready = metadata.Queues.Values.Sum(q => (long)q.Count),
            Locked = metadata.LockCount,
            NextSequence = metadata.NextSequence,
            SegmentSize = metadata.Config.SegmentSize,
            DedupEnabled = metadata.Config.DedupEnabled,
            ErrorMessage = string.IsNullOrEmpty(metadata.ErrorMessage) ? null : metadata.ErrorMessage,
            SessionLeaseExpiresAt = metadata.ActiveSessionLeaseExpiresAt,
            SessionCount = sessionCount,
            DeadLetterReady = deadLetterTask.Result.Ready,
            DeadLetterLocked = deadLetterTask.Result.Locked,
            MaxDeliveryCount = _lockConfig.MaxDeliveryCount,
            Sink = sink == null ? null : new QueueSinkDetail
            {
                Url = sink.Url,
                MaxConcurrency = sink.MaxConcurrency,
                LockTtlSeconds = sink.LockTtlSeconds,
                CurrentIntervalSeconds = sink.CurrentIntervalSeconds
            }
        });
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<AdminMessagePage>> ListMessagesAsync(
        string queueId, int? priority, MessageCursor? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var metadata = await ReadQueueMetadataAsync(queueId, cancellationToken);
        if (metadata == null)
        {
            return AdminReadResult<AdminMessagePage>.NotFound();
        }

        var items = new List<AdminMessage>(limit);
        MessageCursor? last = null;
        var priorities = metadata.Queues.Keys
            .Where(p => priority == null || p == priority)
            .Where(p => cursor == null || p >= cursor.Value.Priority)
            .Order();

        foreach (var p in priorities)
        {
            var queue = metadata.Queues[p];
            var resume = cursor is { } c && c.Priority == p;
            var after = resume ? cursor!.Value.AfterSequence : long.MinValue;
            var firstSegment = resume ? Math.Max(cursor!.Value.Segment, queue.HeadSegment) : queue.HeadSegment;

            for (var segment = firstSegment; segment <= queue.TailSegment; segment++)
            {
                var segmentItems = await ReadSegmentAsync(queueId, p, segment, cancellationToken);
                for (var index = 0; index < segmentItems.Count; index++)
                {
                    var item = segmentItems[index];
                    if (item.Sequence <= after)
                    {
                        continue;
                    }

                    if (items.Count == limit)
                    {
                        return AdminReadResult<AdminMessagePage>.Ok(new AdminMessagePage { Items = items, NextCursor = last.ToString() });
                    }

                    items.Add(ToMessage(new MessageLocation(p, segment, index), p, item, PreviewChars));
                    last = new MessageCursor(p, segment, item.Sequence);
                }
            }
        }

        return AdminReadResult<AdminMessagePage>.Ok(new AdminMessagePage { Items = items });
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<AdminMessage>> GetMessageAsync(
        string queueId, MessageLocation location, long sequence, CancellationToken cancellationToken = default)
    {
        var found = await FindMessageAsync(queueId, location, sequence, cancellationToken);
        return found.Status == AdminReadStatus.Ok
            ? AdminReadResult<AdminMessage>.Ok(ToMessage(found.Value.Location, location.Priority, found.Value.Item, MaxTextChars))
            : new AdminReadResult<AdminMessage> { Status = found.Status };
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<AdminPayload>> GetMessagePayloadAsync(
        string queueId, MessageLocation location, long sequence, CancellationToken cancellationToken = default)
    {
        var found = await FindMessageAsync(queueId, location, sequence, cancellationToken);
        return found.Status == AdminReadStatus.Ok
            ? await ReadPayloadAsync(found.Value.Item.ItemJson, cancellationToken)
            : new AdminReadResult<AdminPayload> { Status = found.Status };
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<AdminLockPage>> ListLocksAsync(string queueId, int offset, int limit, CancellationToken cancellationToken = default)
    {
        var metadata = await ReadQueueMetadataAsync(queueId, cancellationToken);
        if (metadata == null)
        {
            return AdminReadResult<AdminLockPage>.NotFound();
        }

        var lockIds = await ReadLockIndexAsync(queueId, metadata, cancellationToken);
        var items = new List<AdminLock>(limit);
        var position = offset;
        var readBudget = limit * MaxLockReadsPerLimit;

        while (items.Count < limit && position < lockIds.Count && readBudget > 0)
        {
            var batch = lockIds.Skip(position).Take(Math.Min(limit - items.Count, readBudget)).ToList();
            var locks = await Task.WhenAll(batch.Select(id => ReadLockAsync(queueId, id, cancellationToken)));
            position += batch.Count;
            readBudget -= batch.Count;
            // A settled lock's id stays in its expiry bucket until the sweep, so a missing lock is skipped.
            items.AddRange(locks.Where(l => l != null).Select(l => ToLock(l!, PreviewChars)));
        }

        return AdminReadResult<AdminLockPage>.Ok(new AdminLockPage
        {
            Items = items,
            NextCursor = position < lockIds.Count ? position.ToString(System.Globalization.CultureInfo.InvariantCulture) : null
        });
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<AdminLock>> GetLockAsync(string queueId, string lockId, CancellationToken cancellationToken = default)
    {
        var lockState = await ReadLockAsync(queueId, lockId, cancellationToken);
        return lockState == null
            ? AdminReadResult<AdminLock>.NotFound()
            : AdminReadResult<AdminLock>.Ok(ToLock(lockState, MaxTextChars));
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<AdminPayload>> GetLockPayloadAsync(string queueId, string lockId, CancellationToken cancellationToken = default)
    {
        var lockState = await ReadLockAsync(queueId, lockId, cancellationToken);
        return lockState == null
            ? AdminReadResult<AdminPayload>.NotFound()
            : await ReadPayloadAsync(lockState.ItemJson, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<AdminSessionPage>> ListSessionsAsync(
        string queueId, string? afterSessionId, int limit, CancellationToken cancellationToken = default)
    {
        var directory = await _reader.ReadSessionDirectoryAsync(queueId, cancellationToken);
        var page = directory
            .Where(id => afterSessionId == null || string.CompareOrdinal(id, afterSessionId) > 0)
            .Order(StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();

        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(limit);
        }

        var sessions = await Task.WhenAll(page.Select(async sessionId =>
        {
            var metadata = await ReadQueueMetadataAsync($"{queueId}{QueueActor.SessionActorIdMarker}{sessionId}", cancellationToken);
            return new AdminSession
            {
                SessionId = sessionId,
                Ready = metadata?.Queues.Values.Sum(q => (long)q.Count) ?? 0,
                Locked = metadata?.LockCount ?? 0,
                LeaseExpiresAt = metadata?.ActiveSessionLeaseExpiresAt
            };
        }));

        return AdminReadResult<AdminSessionPage>.Ok(new AdminSessionPage
        {
            Items = sessions.ToList(),
            NextCursor = hasMore ? page[^1] : null
        });
    }

    /// <inheritdoc />
    public async Task<AdminReadResult<TopicDetail>> GetTopicAsync(string topicId, CancellationToken cancellationToken = default)
    {
        var metadata = await _reader.ReadStateAsync<TopicMetadata>(_types.Topic, topicId, TopicActor.MetadataKey, cancellationToken);
        if (metadata == null)
        {
            return AdminReadResult<TopicDetail>.NotFound();
        }

        var subscribers = await Task.WhenAll(metadata.SubscriberIds.Select(async subscriberId =>
        {
            var breaker = await _reader.ReadStateAsync<CircuitBreakerState>(
                _types.Topic, topicId, TopicActor.CircuitKey(subscriberId), cancellationToken);
            var cursor = metadata.DispatchCursors.GetValueOrDefault(subscriberId, metadata.NextSequence);
            return new TopicSubscriberDetail
            {
                SubscriberId = subscriberId,
                QueueId = TopicActor.SubscriberQueueId(topicId, subscriberId),
                DispatchCursor = cursor,
                Lag = Math.Max(0, metadata.NextSequence - cursor),
                CircuitBreaker = breaker == null ? null : new AdminCircuitBreaker
                {
                    ConsecutiveFailures = breaker.ConsecutiveFailures,
                    FirstFailureAt = breaker.FirstFailureAt,
                    NextRetryAt = breaker.NextRetryAt,
                    Blacklisted = breaker.Blacklisted
                }
            };
        }));

        return AdminReadResult<TopicDetail>.Ok(new TopicDetail
        {
            TopicId = topicId,
            Subscribers = subscribers.ToList(),
            NextSequence = metadata.NextSequence,
            OldestSequence = metadata.OldestSequence,
            CurrentGeneration = metadata.CurrentGeneration,
            ItemLogCount = metadata.NextSequence - metadata.OldestSequence,
            PendingPublishCount = metadata.NextPublishSequence - metadata.OldestPendingPublishSequence
        });
    }

    private Task<ActorMetadata?> ReadQueueMetadataAsync(string queueId, CancellationToken cancellationToken) =>
        _reader.ReadStateAsync<ActorMetadata>(_types.Queue, queueId, MetadataKey, cancellationToken);

    private async Task<List<QueueSegmentItem>> ReadSegmentAsync(string queueId, int priority, int segment, CancellationToken cancellationToken) =>
        await _reader.ReadStateAsync<List<QueueSegmentItem>>(_types.Queue, queueId, QueueActor.SegmentKey(priority, segment), cancellationToken)
        ?? [];

    private Task<LockState?> ReadLockAsync(string queueId, string lockId, CancellationToken cancellationToken) =>
        _reader.ReadStateAsync<LockState>(_types.Queue, queueId, QueueActor.LockKey(lockId), cancellationToken);

    /// <summary>
    /// Finds a message by Sequence in the segment it was listed in; the index is tried first. Gone if
    /// it isn't there any more (consumed, locked, or moved by a restore).
    /// </summary>
    private async Task<AdminReadResult<(MessageLocation Location, QueueSegmentItem Item)>> FindMessageAsync(
        string queueId, MessageLocation location, long sequence, CancellationToken cancellationToken)
    {
        if (await ReadQueueMetadataAsync(queueId, cancellationToken) == null)
        {
            return AdminReadResult<(MessageLocation, QueueSegmentItem)>.NotFound();
        }

        var items = await ReadSegmentAsync(queueId, location.Priority, location.Segment, cancellationToken);
        var index = location.Index < items.Count && items[location.Index].Sequence == sequence
            ? location.Index
            : items.FindIndex(i => i.Sequence == sequence);

        return index < 0
            ? AdminReadResult<(MessageLocation, QueueSegmentItem)>.Gone()
            : AdminReadResult<(MessageLocation, QueueSegmentItem)>.Ok((location with { Index = index }, items[index]));
    }

    /// <summary>
    /// Lock ids in index order: the session index on a session actor, else every expiry bucket in
    /// bucket order. The bucketed index may still list settled locks.
    /// </summary>
    private async Task<List<string>> ReadLockIndexAsync(string queueId, ActorMetadata metadata, CancellationToken cancellationToken)
    {
        if (queueId.Contains(QueueActor.SessionActorIdMarker, StringComparison.Ordinal))
        {
            return await _reader.ReadStateAsync<List<string>>(_types.Queue, queueId, QueueActor.SessionLockIndexKey, cancellationToken) ?? [];
        }

        var buckets = await Task.WhenAll(metadata.LockExpiryBuckets.Order().Select(bucket =>
            _reader.ReadStateAsync<List<string>>(_types.Queue, queueId, QueueActor.ExpiryBucketKey(bucket), cancellationToken)));
        return buckets.SelectMany(ids => ids ?? []).Distinct().ToList();
    }

    private async Task<AdminReadResult<AdminPayload>> ReadPayloadAsync(string itemJson, CancellationToken cancellationToken)
    {
        if (!BlobReferenceEnvelope.TryParseEnvelope(itemJson, out var envelope) || envelope == null)
        {
            return AdminReadResult<AdminPayload>.NotFound();
        }

        Stream stream;
        try
        {
            stream = await _objectStore.DownloadAsync(envelope.BlobReference, cancellationToken);
        }
        catch (ObjectNotFoundException)
        {
            return AdminReadResult<AdminPayload>.NotFound();
        }

        await using var _ = stream;
        var buffer = new byte[MaxPayloadBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
        {
            read += n;
        }

        var truncated = read > MaxPayloadBytes;
        var length = Math.Min(read, MaxPayloadBytes);
        return AdminReadResult<AdminPayload>.Ok(new AdminPayload
        {
            ContentType = envelope.ContentType,
            SizeBytes = envelope.Size ?? read,
            ContentBase64 = Convert.ToBase64String(buffer, 0, length),
            Truncated = truncated
        });
    }

    private static AdminMessage ToMessage(MessageLocation location, int priority, QueueSegmentItem item, int maxChars)
    {
        var (text, truncated) = Cap(item.ItemJson, maxChars);
        BlobReferenceEnvelope.TryParseEnvelope(item.ItemJson, out var envelope);
        return new AdminMessage
        {
            Location = location.ToString(),
            Priority = priority,
            Sequence = item.Sequence,
            DeliveryCount = item.DeliveryCount,
            SizeBytes = Encoding.UTF8.GetByteCount(item.ItemJson),
            Text = text,
            TextTruncated = truncated,
            IsBlobReference = envelope != null,
            BlobSize = envelope?.Size,
            BlobContentType = envelope?.ContentType
        };
    }

    private static AdminLock ToLock(LockState lockState, int maxChars)
    {
        var (text, truncated) = Cap(lockState.ItemJson, maxChars);
        BlobReferenceEnvelope.TryParseEnvelope(lockState.ItemJson, out var envelope);
        return new AdminLock
        {
            LockId = lockState.LockId,
            CreatedAt = lockState.CreatedAt,
            ExpiresAt = lockState.ExpiresAt,
            Priority = lockState.Priority,
            Sequence = lockState.Sequence,
            DeliveryCount = lockState.DeliveryCount,
            CompetingConsumerMode = lockState.CompetingConsumerMode,
            SizeBytes = Encoding.UTF8.GetByteCount(lockState.ItemJson),
            Text = text,
            TextTruncated = truncated,
            IsBlobReference = envelope != null,
            BlobSize = envelope?.Size,
            BlobContentType = envelope?.ContentType
        };
    }

    private static (string Text, bool Truncated) Cap(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return (text, false);
        }

        // Don't split a surrogate pair.
        var length = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return (text[..length], true);
    }
}
