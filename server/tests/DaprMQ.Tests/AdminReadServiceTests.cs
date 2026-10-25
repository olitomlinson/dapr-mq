using System.Text;
using Dapr.Actors;
using DaprMQ.Interfaces;
using Moq;

namespace DaprMQ.Tests;

public class AdminReadServiceTests
{
    /// <summary>Committed actor state, keyed by (actor type, actor id, key), as the actor-state API returns it.</summary>
    private sealed class FakeStateReader : IQueueActorStateReader
    {
        public Dictionary<(string Type, string Id, string Key), object> State { get; } = new();
        public List<(string Type, string Id, string Key)> Reads { get; } = [];

        public Task<T?> ReadStateAsync<T>(string actorType, string actorId, string key, CancellationToken cancellationToken = default) where T : class
        {
            lock (Reads)
            {
                Reads.Add((actorType, actorId, key));
            }
            return Task.FromResult(State.TryGetValue((actorType, actorId, key), out var value) ? (T)value : null);
        }

        public Task<QueueDepth> ReadQueueDepthAsync(ActorId actorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(State.TryGetValue(("QueueActor", actorId.GetId(), "metadata"), out var value)
                ? new QueueDepth(((ActorMetadata)value).Queues.Values.Sum(q => (long)q.Count), ((ActorMetadata)value).LockCount)
                : new QueueDepth(0, 0));

        public Task<IReadOnlyCollection<string>> ReadSessionDirectoryAsync(string queueId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(
                State.TryGetValue(("SessionCoordinatorActor", queueId, "sessions"), out var value)
                    ? (string[])value
                    : []);

        public Task<DirectorySummary> ReadDirectorySummaryAsync(string queueId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DirectorySummary());

        public Task<SessionActorState> ReadSessionStateAsync(ActorId actorId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private readonly FakeStateReader _reader = new();
    private readonly Mock<IObjectStore> _objectStore = new();

    private AdminReadService CreateService() =>
        new(_reader, new AdminActorTypes(), new LockConfig { MaxDeliveryCount = 10, SweepBatchSize = 200 }, _objectStore.Object);

    private void Queue(string queueId, ActorMetadata metadata) => _reader.State[("QueueActor", queueId, "metadata")] = metadata;

    private void Segment(string queueId, int priority, int segment, params QueueSegmentItem[] items) =>
        _reader.State[("QueueActor", queueId, $"queue_{priority}_seg_{segment}")] = items.ToList();

    private static QueueSegmentItem Item(long sequence, string json = "{}", int deliveryCount = 0) =>
        new() { Sequence = sequence, ItemJson = json, DeliveryCount = deliveryCount };

    private static ActorMetadata Meta(params (int Priority, int Head, int Tail, int Count)[] queues) => new()
    {
        Queues = queues.ToDictionary(q => q.Priority, q => new QueueMetadata { HeadSegment = q.Head, TailSegment = q.Tail, Count = q.Count })
    };

    private void Lock(string queueId, string lockId, long sequence, string json = "{}") =>
        _reader.State[("QueueActor", queueId, $"{lockId}-lock")] = new LockState
        {
            LockId = lockId, CreatedAt = 1, ExpiresAt = 2, Priority = 1, HeadSegment = 0, ItemJson = json,
            CompetingConsumerMode = true, Sequence = sequence, DeliveryCount = 3
        };

    // --- queue detail ---

    [Fact]
    public async Task Queue_Unknown_IsNotFound()
    {
        var result = await CreateService().GetQueueAsync("missing");

        Assert.Equal(AdminReadStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Queue_ReportsCountsConfigDlqAndSink_ButNotTheLeaseId()
    {
        Queue("orders", Meta((0, 0, 0, 2), (1, 3, 5, 250)) with
        {
            LockCount = 4, NextSequence = 900, ErrorMessage = "CORRUPTED: x", ActiveSessionLeaseId = "secret", ActiveSessionLeaseExpiresAt = 123
        });
        Queue("orders-deadletter", Meta((1, 0, 0, 7)) with { LockCount = 1 });
        _reader.State[("HttpSinkActor", "orders-sink", "sink-state")] = new HttpSinkActorState
        {
            Url = "http://svc", QueueActorId = "orders", MaxConcurrency = 8, LockTtlSeconds = 30, CurrentIntervalSeconds = 2
        };

        var detail = (await CreateService().GetQueueAsync("orders")).Value!;

        Assert.Equal([0, 1], detail.Priorities.Select(p => p.Priority));
        Assert.Equal(252, detail.Ready);
        Assert.Equal(4, detail.Locked);
        Assert.Equal(900, detail.NextSequence);
        Assert.Equal("CORRUPTED: x", detail.ErrorMessage);
        Assert.Equal(123, detail.SessionLeaseExpiresAt);
        Assert.Equal(7, detail.DeadLetterReady);
        Assert.Equal(1, detail.DeadLetterLocked);
        Assert.Equal(10, detail.MaxDeliveryCount);
        Assert.Equal(new QueueSinkDetail { Url = "http://svc", MaxConcurrency = 8, LockTtlSeconds = 30, CurrentIntervalSeconds = 2 }, detail.Sink);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(detail));
    }

    [Fact]
    public async Task Queue_SessionOnly_IsFoundThroughItsDirectory()
    {
        _reader.State[("SessionCoordinatorActor", "orders", "sessions")] = new[] { "a", "b" };

        var result = await CreateService().GetQueueAsync("orders");

        Assert.Equal(AdminReadStatus.Ok, result.Status);
        Assert.Equal(2, result.Value!.SessionCount);
        Assert.Equal(0, result.Value.Ready);
    }

    // --- message browse ---

    [Fact]
    public async Task Messages_WalkSegmentsHeadToTail_ByPriority()
    {
        Queue("orders", Meta((0, 0, 0, 1), (1, 4, 5, 3)));
        Segment("orders", 0, 0, Item(9));
        Segment("orders", 1, 4, Item(2), Item(3));
        Segment("orders", 1, 5, Item(5));

        var page = (await CreateService().ListMessagesAsync("orders", null, null, 10)).Value!;

        Assert.Equal([9L, 2L, 3L, 5L], page.Items.Select(m => m.Sequence));
        Assert.Equal(["0:0:0", "1:4:0", "1:4:1", "1:5:0"], page.Items.Select(m => m.Location));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Messages_PageResumesAfterTheLastSequence_EvenIfTheHeadWasConsumed()
    {
        Queue("orders", Meta((1, 0, 1, 4)));
        Segment("orders", 1, 0, Item(1), Item(2), Item(3));
        Segment("orders", 1, 1, Item(4));

        var first = (await CreateService().ListMessagesAsync("orders", null, null, 2)).Value!;
        // Item 1 is dequeued between pages, shifting every index in the head segment.
        Segment("orders", 1, 0, Item(2), Item(3));
        MessageCursor.TryParse(first.NextCursor, out var cursor);
        var second = (await CreateService().ListMessagesAsync("orders", null, cursor, 2)).Value!;

        Assert.Equal([1L, 2L], first.Items.Select(m => m.Sequence));
        Assert.Equal("1:0:2", first.NextCursor);
        Assert.Equal([3L, 4L], second.Items.Select(m => m.Sequence));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Messages_CursorBehindTheHead_StartsAtTheHead()
    {
        Queue("orders", Meta((1, 3, 3, 1)));
        Segment("orders", 1, 3, Item(301));

        var page = (await CreateService().ListMessagesAsync("orders", null, new MessageCursor(1, 1, 150), 10)).Value!;

        Assert.Equal([301L], page.Items.Select(m => m.Sequence));
        Assert.DoesNotContain(_reader.Reads, r => r.Key == "queue_1_seg_1");
    }

    [Fact]
    public async Task Messages_FilterByPriority()
    {
        Queue("orders", Meta((0, 0, 0, 1), (1, 0, 0, 1)));
        Segment("orders", 0, 0, Item(1));
        Segment("orders", 1, 0, Item(2));

        var page = (await CreateService().ListMessagesAsync("orders", 1, null, 10)).Value!;

        Assert.Equal([2L], page.Items.Select(m => m.Sequence));
    }

    [Fact]
    public async Task Messages_ExactlyFullPage_HasNoCursor()
    {
        Queue("orders", Meta((1, 0, 0, 2)));
        Segment("orders", 1, 0, Item(1), Item(2));

        var page = (await CreateService().ListMessagesAsync("orders", null, null, 2)).Value!;

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Messages_PreviewIsCappedAndBlobReferencesFlagged()
    {
        var big = "{\"x\":\"" + new string('a', 5000) + "\"}";
        var blob = BlobReferenceEnvelope.Build("ref-1", size: 4096, contentType: "application/octet-stream");
        Queue("orders", Meta((1, 0, 0, 2)));
        Segment("orders", 1, 0, Item(1, big), Item(2, blob));

        var page = (await CreateService().ListMessagesAsync("orders", null, null, 10)).Value!;

        Assert.Equal(AdminReadService.PreviewChars, page.Items[0].Text.Length);
        Assert.True(page.Items[0].TextTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(big), page.Items[0].SizeBytes);
        Assert.False(page.Items[0].IsBlobReference);
        Assert.True(page.Items[1].IsBlobReference);
        Assert.Equal(4096, page.Items[1].BlobSize);
    }

    [Fact]
    public async Task Messages_UnknownQueue_IsNotFound()
    {
        Assert.Equal(AdminReadStatus.NotFound, (await CreateService().ListMessagesAsync("missing", null, null, 10)).Status);
    }

    // --- single message ---

    [Fact]
    public async Task Message_FoundBySequenceWithinItsSegment_WhenTheIndexShifted()
    {
        Queue("orders", Meta((1, 0, 0, 2)));
        Segment("orders", 1, 0, Item(5, "{\"n\":5}"), Item(6, "{\"n\":6}"));

        var result = await CreateService().GetMessageAsync("orders", new MessageLocation(1, 0, 3), 6);

        Assert.Equal("{\"n\":6}", result.Value!.Text);
        Assert.Equal("1:0:1", result.Value.Location);
    }

    [Fact]
    public async Task Message_NoLongerInItsSegment_IsGone()
    {
        Queue("orders", Meta((1, 0, 0, 1)));
        Segment("orders", 1, 0, Item(6));

        var result = await CreateService().GetMessageAsync("orders", new MessageLocation(1, 0, 0), 5);

        Assert.Equal(AdminReadStatus.Gone, result.Status);
    }

    [Fact]
    public async Task MessagePayload_DownloadsTheBlob()
    {
        Queue("orders", Meta((1, 0, 0, 1)));
        Segment("orders", 1, 0, Item(1, BlobReferenceEnvelope.Build("ref-1", size: 3, contentType: "text/plain")));
        _objectStore.Setup(s => s.DownloadAsync("ref-1", It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream([1, 2, 3]));

        var payload = (await CreateService().GetMessagePayloadAsync("orders", new MessageLocation(1, 0, 0), 1)).Value!;

        Assert.Equal([1, 2, 3], Convert.FromBase64String(payload.ContentBase64));
        Assert.Equal("text/plain", payload.ContentType);
        Assert.False(payload.Truncated);
    }

    [Fact]
    public async Task MessagePayload_IsCappedAt1Mb()
    {
        Queue("orders", Meta((1, 0, 0, 1)));
        Segment("orders", 1, 0, Item(1, BlobReferenceEnvelope.Build("ref-1")));
        _objectStore.Setup(s => s.DownloadAsync("ref-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[AdminReadService.MaxPayloadBytes + 10]));

        var payload = (await CreateService().GetMessagePayloadAsync("orders", new MessageLocation(1, 0, 0), 1)).Value!;

        Assert.True(payload.Truncated);
        Assert.Equal(AdminReadService.MaxPayloadBytes, Convert.FromBase64String(payload.ContentBase64).Length);
    }

    [Fact]
    public async Task MessagePayload_NotABlob_IsNotFound()
    {
        Queue("orders", Meta((1, 0, 0, 1)));
        Segment("orders", 1, 0, Item(1));

        var result = await CreateService().GetMessagePayloadAsync("orders", new MessageLocation(1, 0, 0), 1);

        Assert.Equal(AdminReadStatus.NotFound, result.Status);
        _objectStore.VerifyNoOtherCalls();
    }

    // --- locks ---

    [Fact]
    public async Task Locks_WalkBucketsInOrder_SkippingSettledIds()
    {
        Queue("orders", Meta() with { LockCount = 2, LockExpiryBuckets = [20, 10] });
        _reader.State[("QueueActor", "orders", "locks_exp_10")] = new List<string> { "lockA", "settled" };
        _reader.State[("QueueActor", "orders", "locks_exp_20")] = new List<string> { "lockB" };
        Lock("orders", "lockA", 1);
        Lock("orders", "lockB", 2);

        var page = (await CreateService().ListLocksAsync("orders", 0, 10)).Value!;

        Assert.Equal(["lockA", "lockB"], page.Items.Select(l => l.LockId));
        Assert.Equal(3, page.Items[0].DeliveryCount);
        Assert.True(page.Items[0].CompetingConsumerMode);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Locks_PageByIndexOffset()
    {
        Queue("orders", Meta() with { LockExpiryBuckets = [10] });
        _reader.State[("QueueActor", "orders", "locks_exp_10")] = new List<string> { "l1", "l2", "l3" };
        Lock("orders", "l1", 1);
        Lock("orders", "l2", 2);
        Lock("orders", "l3", 3);

        var first = (await CreateService().ListLocksAsync("orders", 0, 2)).Value!;
        var second = (await CreateService().ListLocksAsync("orders", int.Parse(first.NextCursor!), 2)).Value!;

        Assert.Equal(["l1", "l2"], first.Items.Select(l => l.LockId));
        Assert.Equal(["l3"], second.Items.Select(l => l.LockId));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Locks_DeadIdsAreBoundedPerPage()
    {
        var ids = Enumerable.Range(0, 100).Select(i => $"dead{i}").ToList();
        Queue("orders", Meta() with { LockExpiryBuckets = [10] });
        _reader.State[("QueueActor", "orders", "locks_exp_10")] = ids;

        var page = (await CreateService().ListLocksAsync("orders", 0, 5)).Value!;

        Assert.Empty(page.Items);
        Assert.Equal("20", page.NextCursor);
        Assert.Equal(20, _reader.Reads.Count(r => r.Key.EndsWith("-lock")));
    }

    [Fact]
    public async Task Locks_SessionActor_UsesTheSessionIndex()
    {
        Queue("orders-session-a", Meta() with { LockCount = 1 });
        _reader.State[("QueueActor", "orders-session-a", "locks_session")] = new List<string> { "s1" };
        Lock("orders-session-a", "s1", 1);

        var page = (await CreateService().ListLocksAsync("orders-session-a", 0, 10)).Value!;

        Assert.Equal(["s1"], page.Items.Select(l => l.LockId));
    }

    [Fact]
    public async Task Lock_ReturnsTheWholeItem_OrNotFound()
    {
        var json = "{\"x\":\"" + new string('a', 5000) + "\"}";
        Lock("orders", "lockA", 1, json);

        var found = await CreateService().GetLockAsync("orders", "lockA");
        var missing = await CreateService().GetLockAsync("orders", "nope");

        Assert.Equal(json, found.Value!.Text);
        Assert.False(found.Value.TextTruncated);
        Assert.Equal(AdminReadStatus.NotFound, missing.Status);
    }

    // --- sessions ---

    [Fact]
    public async Task Sessions_PageTheDirectoryById_WithEachSessionsDepth()
    {
        _reader.State[("SessionCoordinatorActor", "orders", "sessions")] = new[] { "c", "a", "b" };
        Queue("orders-session-a", Meta((1, 0, 0, 2)) with { LockCount = 1, ActiveSessionLeaseExpiresAt = 50 });

        var first = (await CreateService().ListSessionsAsync("orders", null, 2)).Value!;
        var second = (await CreateService().ListSessionsAsync("orders", first.NextCursor, 2)).Value!;

        Assert.Equal(["a", "b"], first.Items.Select(s => s.SessionId));
        Assert.Equal(new AdminSession { SessionId = "a", Ready = 2, Locked = 1, LeaseExpiresAt = 50 }, first.Items[0]);
        Assert.Equal(new AdminSession { SessionId = "b" }, first.Items[1]);
        Assert.Equal("b", first.NextCursor);
        Assert.Equal(["c"], second.Items.Select(s => s.SessionId));
        Assert.Null(second.NextCursor);
        // Only the sessions on each page are read, never the whole directory's actors.
        Assert.Equal(3, _reader.Reads.Count(r => r.Id.StartsWith("orders-session-")));
    }

    // --- topics ---

    [Fact]
    public async Task Topic_ReportsLagBreakerAndLogCounts()
    {
        _reader.State[("TopicActor", "news", "metadata")] = new TopicMetadata
        {
            SubscriberIds = ["audit", "search"],
            NextSequence = 150,
            OldestSequence = 100,
            CurrentGeneration = 4,
            DispatchCursors = new() { ["audit"] = 150, ["search"] = 30 },
            NextPublishSequence = 12,
            OldestPendingPublishSequence = 9
        };
        _reader.State[("TopicActor", "news", "circuit_search")] = new CircuitBreakerState
        {
            SubscriberId = "search", ConsecutiveFailures = 5, NextRetryAt = 99
        };

        var detail = (await CreateService().GetTopicAsync("news")).Value!;

        var audit = detail.Subscribers.Single(s => s.SubscriberId == "audit");
        var search = detail.Subscribers.Single(s => s.SubscriberId == "search");
        Assert.Equal("news-sub-audit", audit.QueueId);
        Assert.Equal(0, audit.Lag);
        Assert.Null(audit.CircuitBreaker);
        Assert.Equal(120, search.Lag);
        Assert.Equal(5, search.CircuitBreaker!.ConsecutiveFailures);
        Assert.Equal(4, detail.CurrentGeneration);
        Assert.Equal(50, detail.ItemLogCount);
        Assert.Equal(3, detail.PendingPublishCount);
    }

    [Fact]
    public async Task Topic_Unknown_IsNotFound()
    {
        Assert.Equal(AdminReadStatus.NotFound, (await CreateService().GetTopicAsync("missing")).Status);
    }
}
