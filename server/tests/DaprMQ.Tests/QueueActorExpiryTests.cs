using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Message expiry in QueueActor (proposals/message-expiry.md): an item waiting in the queue past its
/// ExpiryAt is never delivered, and is removed when a dequeue, lock sweep, nack or session lease lapse
/// reaches it. A locked item belongs to its consumer until it would go back into the queue.
/// </summary>
public class QueueActorExpiryTests
{
    private const int BucketWidthSeconds = 5;

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static Mock<IActorStateManager> CreateMockStateManager(Dictionary<string, object> stateData)
    {
        var mock = new Mock<IActorStateManager>();

        mock.Setup(m => m.TryGetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is ActorMetadata m2
                    ? new ConditionalValue<ActorMetadata>(true, m2)
                    : new ConditionalValue<ActorMetadata>(false, null));

        mock.Setup(m => m.TryGetStateAsync<LockState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is LockState l
                    ? new ConditionalValue<LockState>(true, l)
                    : new ConditionalValue<LockState>(false, null));

        mock.Setup(m => m.TryGetStateAsync<Queue<QueueSegmentItem>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is Queue<QueueSegmentItem> q
                    ? new ConditionalValue<Queue<QueueSegmentItem>>(true, q)
                    : new ConditionalValue<Queue<QueueSegmentItem>>(false, null));

        mock.Setup(m => m.TryGetStateAsync<List<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is List<string> s
                    ? new ConditionalValue<List<string>>(true, s)
                    : new ConditionalValue<List<string>>(false, null));

        mock.Setup(m => m.TryGetStateAsync<IdempotencyMarker>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConditionalValue<IdempotencyMarker>(false, null));

        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken ct) =>
            {
                stateData[key] = value;
                return Task.CompletedTask;
            });

        mock.Setup(m => m.RemoveStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken ct) =>
            {
                stateData.Remove(key);
                return Task.CompletedTask;
            });

        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return mock;
    }

    private sealed record Harness(
        QueueActor Actor,
        Dictionary<string, object> State,
        Mock<IActorStateManager> StateManager,
        Mock<IQueueActorInvoker> Invoker,
        Mock<IBlobReaperActorInvoker> BlobReaper);

    private static async Task<Harness> CreateActorAsync(
        string actorId = "test-queue",
        ExpiryConfig? expiryConfig = null,
        LockConfig? lockConfig = null)
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);

        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true });

        var blobReaper = new Mock<IBlobReaperActorInvoker>();

        var actorHost = ActorHost.CreateForTest<QueueActor>(new ActorTestOptions { ActorId = new ActorId(actorId) });
        var actor = new QueueActor(
            actorHost,
            mockInvoker.Object,
            blobReaper.Object,
            new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
            {
                SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
                TokenTtl = TimeSpan.FromMinutes(5)
            }),
            new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 },
            new IdempotencyConfig { TtlSeconds = 86400 },
            lockConfig ?? new LockConfig { MaxDeliveryCount = 10, SweepBatchSize = 200 },
            Mock.Of<ICatalogRegistrar>(),
            Mock.Of<ISessionActivityReporter>(),
            new SessionDirectoryConfig(),
            expiryConfig ?? new ExpiryConfig());

        typeof(Actor).GetProperty("StateManager")!.SetValue(actor, mockStateManager.Object);

        var onActivate = typeof(QueueActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)onActivate.Invoke(actor, null)!;

        return new Harness(actor, stateData, mockStateManager, mockInvoker, blobReaper);
    }

    private static EnqueueItem Item(int n, double? expiryAt, int? expiryTtlSeconds = 3600) =>
        new() { ItemJson = $"{{\"n\":{n}}}", Priority = 1, ExpiryAt = expiryAt, ExpiryTtlSeconds = expiryTtlSeconds };

    private static EnqueueItem Expired(int n) => Item(n, Now() - 10);
    private static EnqueueItem Live(int n) => Item(n, Now() + 3600);

    private static async Task EnqueueAsync(QueueActor actor, params EnqueueItem[] items)
    {
        var result = await actor.Enqueue(new EnqueueRequest { Items = items.ToList() });
        Assert.True(result.Success);
    }

    private static int QueuedCount(Dictionary<string, object> state) =>
        ((ActorMetadata)state["metadata"]).Queues.Values.Sum(q => q.Count);

    private static int LockCount(Dictionary<string, object> state) => ((ActorMetadata)state["metadata"]).LockCount;

    private static long BucketFor(double expiresAt) =>
        (long)Math.Floor(expiresAt / BucketWidthSeconds) * BucketWidthSeconds;

    /// <summary>Plants a lapsed lock, indexed and counted, holding an item with the given ExpiryAt.</summary>
    private static void SeedLapsedLock(Dictionary<string, object> state, string lockId, double? itemExpiryAt, int deliveryCount = 0, long sequence = 0)
    {
        double lockExpiresAt = Now() - 1;
        state[$"{lockId}-lock"] = new LockState
        {
            LockId = lockId,
            CreatedAt = lockExpiresAt - 30,
            ExpiresAt = lockExpiresAt,
            Priority = 1,
            HeadSegment = 0,
            ItemJson = "{\"n\":1}",
            CompetingConsumerMode = true,
            DeliveryCount = deliveryCount,
            Sequence = sequence,
            ExpiryAt = itemExpiryAt,
            ExpiryTtlSeconds = 3600
        };

        long bucket = BucketFor(lockExpiresAt);
        state[$"locks_exp_{bucket}"] = new List<string> { lockId };
        var metadata = (ActorMetadata)state["metadata"];
        state["metadata"] = metadata with { LockCount = metadata.LockCount + 1, LockExpiryBuckets = [bucket] };
    }

    // ---- Dequeue / DequeueLocked ----

    [Fact]
    public async Task Dequeue_SkipsAndRemovesExpiredItemsAtTheHead_InOneSave()
    {
        var h = await CreateActorAsync();
        await EnqueueAsync(h.Actor, Expired(1), Expired(2), Live(3));
        h.StateManager.Invocations.Clear();

        var result = await h.Actor.Dequeue(new DequeueRequest());

        Assert.Equal("{\"n\":3}", Assert.Single(result.Items).ItemJson);
        Assert.False(result.ExpiredMore);
        Assert.Equal(0, QueuedCount(h.State));
        h.StateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dequeue_OnlyExpiredItems_ReturnsEmpty()
    {
        var h = await CreateActorAsync();
        await EnqueueAsync(h.Actor, Expired(1), Expired(2));

        var result = await h.Actor.Dequeue(new DequeueRequest());

        Assert.True(result.IsEmpty);
        Assert.False(result.ExpiredMore);
        Assert.Equal(0, QueuedCount(h.State));
    }

    [Fact]
    public async Task Dequeue_SkipsExpiredItemsBetweenLiveOnes()
    {
        var h = await CreateActorAsync();
        await EnqueueAsync(h.Actor, Live(1), Expired(2), Live(3));

        var result = await h.Actor.Dequeue(new DequeueRequest { Count = 2 });

        Assert.Equal(["{\"n\":1}", "{\"n\":3}"], result.Items.Select(i => i.ItemJson));
    }

    [Fact]
    public async Task Dequeue_RemovesAtMostExpirySweepBatchSize_ThenReturnsExpiredMore()
    {
        var h = await CreateActorAsync(expiryConfig: new ExpiryConfig { ExpirySweepBatchSize = 3 });
        await EnqueueAsync(h.Actor, Expired(1), Expired(2), Expired(3), Expired(4), Expired(5), Live(6));
        h.StateManager.Invocations.Clear();

        var first = await h.Actor.Dequeue(new DequeueRequest());

        Assert.True(first.ExpiredMore);
        Assert.Empty(first.Items);
        Assert.False(first.IsEmpty);
        Assert.False(first.Locked);
        Assert.Equal(3, QueuedCount(h.State));
        h.StateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);

        var second = await h.Actor.Dequeue(new DequeueRequest());

        Assert.Equal("{\"n\":6}", Assert.Single(second.Items).ItemJson);
        Assert.False(second.ExpiredMore);
    }

    [Fact]
    public async Task Dequeue_BatchLimitReachedAfterALiveItem_ReturnsItWithoutExpiredMore()
    {
        var h = await CreateActorAsync(expiryConfig: new ExpiryConfig { ExpirySweepBatchSize = 2 });
        await EnqueueAsync(h.Actor, Live(1), Expired(2), Expired(3), Live(4));

        var result = await h.Actor.Dequeue(new DequeueRequest { Count = 2 });

        Assert.Equal("{\"n\":1}", Assert.Single(result.Items).ItemJson);
        Assert.False(result.ExpiredMore);
        Assert.Equal(1, QueuedCount(h.State));
    }

    [Fact]
    public async Task Dequeue_ItemWithNoExpiryAt_NeverExpires()
    {
        var h = await CreateActorAsync();
        await EnqueueAsync(h.Actor, Item(1, expiryAt: null, expiryTtlSeconds: null));

        var result = await h.Actor.Dequeue(new DequeueRequest());

        var item = Assert.Single(result.Items);
        Assert.Null(item.ExpiryAt);
        Assert.Null(item.ExpiryTtlSeconds);
    }

    [Fact]
    public async Task Dequeue_CarriesTheItemsExpiry()
    {
        var h = await CreateActorAsync();
        double expiryAt = Now() + 3600;
        await EnqueueAsync(h.Actor, Item(1, expiryAt, 3600));

        var item = Assert.Single((await h.Actor.Dequeue(new DequeueRequest())).Items);

        Assert.Equal(expiryAt, item.ExpiryAt);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
        Assert.Null(item.OriginalExpiryTtlSeconds);
    }

    [Fact]
    public async Task Dequeue_DroppingAnExpiredBlobItem_SchedulesItsBlobReap()
    {
        var h = await CreateActorAsync();
        var blobItem = Expired(1) with { ItemJson = BlobReferenceEnvelope.Build("q/obj", 10, "text/plain") };
        await EnqueueAsync(h.Actor, blobItem);

        await h.Actor.Dequeue(new DequeueRequest());

        h.BlobReaper.Verify(r => r.InvokeMethodAsync(
            It.IsAny<ActorId>(), It.IsAny<string>(),
            It.Is<ScheduleDeletionRequest>(d => d.BlobReference == "q/obj"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DequeueLocked_SkipsExpiredItems_AndCarriesTheExpiryIntoTheLock()
    {
        var h = await CreateActorAsync();
        double expiryAt = Now() + 3600;
        await EnqueueAsync(h.Actor, Expired(1), Item(2, expiryAt, 3600));

        var result = await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true });

        var item = Assert.Single(result.Items);
        Assert.Equal("{\"n\":2}", item.ItemJson);
        Assert.Equal(expiryAt, item.ExpiryAt);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
        var lockState = (LockState)h.State[$"{item.LockId}-lock"];
        Assert.Equal(expiryAt, lockState.ExpiryAt);
        Assert.Equal(3600, lockState.ExpiryTtlSeconds);
        Assert.Equal(1, LockCount(h.State));
    }

    [Fact]
    public async Task DequeueLocked_RemovesAtMostExpirySweepBatchSize_ThenReturnsExpiredMore()
    {
        var h = await CreateActorAsync(expiryConfig: new ExpiryConfig { ExpirySweepBatchSize = 2 });
        await EnqueueAsync(h.Actor, Expired(1), Expired(2), Expired(3), Live(4));
        h.StateManager.Invocations.Clear();

        var first = await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true });

        Assert.True(first.ExpiredMore);
        Assert.Empty(first.Items);
        Assert.False(first.IsEmpty);
        Assert.Equal(2, QueuedCount(h.State));
        Assert.Equal(0, LockCount(h.State));
        h.StateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);

        var second = await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true });

        Assert.Equal("{\"n\":4}", Assert.Single(second.Items).ItemJson);
    }

    [Fact]
    public async Task DequeueLocked_OnlyExpiredItems_ReturnsEmptyAndCommitsTheRemoval()
    {
        var h = await CreateActorAsync();
        await EnqueueAsync(h.Actor, Expired(1));
        h.StateManager.Invocations.Clear();

        var result = await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true });

        Assert.True(result.IsEmpty);
        Assert.False(result.ExpiredMore);
        Assert.Equal(0, QueuedCount(h.State));
        h.StateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Going back into the queue ----

    [Fact]
    public async Task LockSweep_DropsAnExpiredItemInsteadOfRestoringIt()
    {
        var h = await CreateActorAsync();
        SeedLapsedLock(h.State, "lockaaaaaaa", itemExpiryAt: Now() - 10);

        var result = await h.Actor.Dequeue(new DequeueRequest());

        Assert.True(result.IsEmpty);
        Assert.False(h.State.ContainsKey("lockaaaaaaa-lock"));
        Assert.Equal(0, LockCount(h.State));
        Assert.Equal(0, QueuedCount(h.State));
    }

    [Fact]
    public async Task LockSweep_ExpiredItemPastMaxDeliveryCount_IsDroppedNotDeadLettered()
    {
        var h = await CreateActorAsync(lockConfig: new LockConfig { MaxDeliveryCount = 1, SweepBatchSize = 200 });
        SeedLapsedLock(h.State, "lockaaaaaaa", itemExpiryAt: Now() - 10, deliveryCount: 1);

        await h.Actor.Dequeue(new DequeueRequest());

        Assert.False(h.State.ContainsKey("lockaaaaaaa-lock"));
        Assert.Equal(0, LockCount(h.State));
        h.Invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LockSweep_RestoresALiveItemWithItsExpiry()
    {
        var h = await CreateActorAsync();
        double expiryAt = Now() + 3600;
        SeedLapsedLock(h.State, "lockaaaaaaa", itemExpiryAt: expiryAt);

        var item = Assert.Single((await h.Actor.Dequeue(new DequeueRequest())).Items);

        Assert.Equal(expiryAt, item.ExpiryAt);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
    }

    [Fact]
    public async Task Nack_DropsAnExpiredItemInsteadOfRestoringIt_WithoutCountingADelivery()
    {
        var h = await CreateActorAsync();
        await EnqueueAsync(h.Actor, Item(1, Now() + 3600));
        var locked = Assert.Single((await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true })).Items);
        var lockKey = $"{locked.LockId}-lock";
        h.State[lockKey] = ((LockState)h.State[lockKey]) with { ExpiryAt = Now() - 1 };

        var result = await h.Actor.Nack(new NackRequest { LockId = locked.LockId });

        Assert.True(result.Success);
        Assert.False(result.DeadLettered);
        Assert.Equal(0, result.DeliveryCount);
        Assert.False(h.State.ContainsKey(lockKey));
        Assert.Equal(0, LockCount(h.State));
        Assert.Equal(0, QueuedCount(h.State));
    }

    [Fact]
    public async Task Nack_ExpiredItemPastMaxDeliveryCount_IsDroppedNotDeadLettered()
    {
        var h = await CreateActorAsync(lockConfig: new LockConfig { MaxDeliveryCount = 0, SweepBatchSize = 200 });
        await EnqueueAsync(h.Actor, Item(1, Now() + 3600));
        var locked = Assert.Single((await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true })).Items);
        var lockKey = $"{locked.LockId}-lock";
        h.State[lockKey] = ((LockState)h.State[lockKey]) with { ExpiryAt = Now() - 1 };

        var result = await h.Actor.Nack(new NackRequest { LockId = locked.LockId });

        Assert.True(result.Success);
        Assert.False(result.DeadLettered);
        Assert.False(h.State.ContainsKey(lockKey));
        h.Invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nack_RestoresALiveItemWithItsExpiry()
    {
        var h = await CreateActorAsync();
        double expiryAt = Now() + 3600;
        await EnqueueAsync(h.Actor, Item(1, expiryAt));
        var locked = Assert.Single((await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true })).Items);

        await h.Actor.Nack(new NackRequest { LockId = locked.LockId });

        var item = Assert.Single((await h.Actor.Dequeue(new DequeueRequest())).Items);
        Assert.Equal(expiryAt, item.ExpiryAt);
    }

    [Fact]
    public async Task Acknowledge_AfterTheItemExpires_Succeeds()
    {
        var h = await CreateActorAsync();
        await EnqueueAsync(h.Actor, Item(1, Now() + 3600));
        var locked = Assert.Single((await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true })).Items);
        var lockKey = $"{locked.LockId}-lock";
        h.State[lockKey] = ((LockState)h.State[lockKey]) with { ExpiryAt = Now() - 1 };

        var result = await h.Actor.Acknowledge(new AcknowledgeRequest { LockId = locked.LockId });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task SessionLeaseLapse_DropsExpiredItemsAndRestoresLiveOnes()
    {
        var h = await CreateActorAsync(actorId: "orders-session-s1");
        h.State["lockexpired-lock"] = new LockState
        {
            LockId = "lockexpired", CreatedAt = Now() - 60, ExpiresAt = Now() - 30, Priority = 1, HeadSegment = 0,
            ItemJson = "{\"n\":1}", CompetingConsumerMode = false, Sequence = 0, ExpiryAt = Now() - 10, ExpiryTtlSeconds = 3600
        };
        h.State["lockalivexx-lock"] = new LockState
        {
            LockId = "lockalivexx", CreatedAt = Now() - 60, ExpiresAt = Now() - 30, Priority = 1, HeadSegment = 0,
            ItemJson = "{\"n\":2}", CompetingConsumerMode = false, Sequence = 1, ExpiryAt = Now() + 3600, ExpiryTtlSeconds = 3600
        };
        h.State["locks_session"] = new List<string> { "lockexpired", "lockalivexx" };
        var metadata = (ActorMetadata)h.State["metadata"];
        h.State["metadata"] = metadata with { LockCount = 2, ActiveSessionLeaseId = "lease", ActiveSessionLeaseExpiresAt = Now() - 1 };

        await h.Actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease2", ExpiresAt = Now() + 60 });

        Assert.False(h.State.ContainsKey("lockexpired-lock"));
        Assert.False(h.State.ContainsKey("lockalivexx-lock"));
        Assert.Equal(0, LockCount(h.State));
        Assert.Equal(1, QueuedCount(h.State));
    }

    // ---- Dead-letter expiry ----

    private const int DeadLetterExpiryTtlSeconds = 2_419_200;

    private static List<EnqueueRequest> CaptureDeadLetters(
        Harness h, List<string>? events = null, bool succeed = true, string deadLetterId = "test-queue-deadletter")
    {
        var captured = new List<EnqueueRequest>();
        h.Invoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((id, _, req, _) =>
            {
                Assert.Equal(deadLetterId, id.GetId());
                captured.Add(req);
                events?.Add("dead-letter");
            })
            .ReturnsAsync(new EnqueueResponse { Success = succeed });
        if (events != null)
        {
            h.StateManager.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>()))
                .Callback(() => events.Add("save"))
                .Returns(Task.CompletedTask);
        }
        return captured;
    }

    private static void AssertDeadLetterExpiry(EnqueueItem item, int? originalExpiryTtlSeconds)
    {
        Assert.Equal(DeadLetterExpiryTtlSeconds, item.ExpiryTtlSeconds);
        Assert.InRange(item.ExpiryAt!.Value, Now() + DeadLetterExpiryTtlSeconds - 5, Now() + DeadLetterExpiryTtlSeconds + 5);
        Assert.Equal(originalExpiryTtlSeconds, item.OriginalExpiryTtlSeconds);
    }

    private static async Task<DequeueLockedItem> LockOneAsync(Harness h, EnqueueItem item)
    {
        await EnqueueAsync(h.Actor, item);
        return Assert.Single((await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true })).Items);
    }

    [Fact]
    public async Task DeadLetter_ReplacesTheExpiryWithTheDeadLetterPeriod_AndKeepsTheOriginalTimeToLive()
    {
        var h = await CreateActorAsync();
        var captured = CaptureDeadLetters(h);
        var locked = await LockOneAsync(h, Item(1, Now() + 3600, 3600));

        await h.Actor.DeadLetter(new DeadLetterRequest { LockId = locked.LockId });

        AssertDeadLetterExpiry(Assert.Single(Assert.Single(captured).Items), originalExpiryTtlSeconds: 3600);
    }

    [Fact]
    public async Task DeadLetter_ItemWithNoExpiry_GetsTheDeadLetterPeriod_AndNoOriginal()
    {
        var h = await CreateActorAsync();
        var captured = CaptureDeadLetters(h);
        var locked = await LockOneAsync(h, Item(1, expiryAt: null, expiryTtlSeconds: null));

        await h.Actor.DeadLetter(new DeadLetterRequest { LockId = locked.LockId });

        AssertDeadLetterExpiry(Assert.Single(Assert.Single(captured).Items), originalExpiryTtlSeconds: null);
    }

    [Fact]
    public async Task NackPastMaxDeliveryCount_DeadLettersWithTheDeadLetterExpiry()
    {
        var h = await CreateActorAsync(lockConfig: new LockConfig { MaxDeliveryCount = 0, SweepBatchSize = 200 });
        var captured = CaptureDeadLetters(h);
        var locked = await LockOneAsync(h, Item(1, Now() + 3600, 3600));

        var result = await h.Actor.Nack(new NackRequest { LockId = locked.LockId });

        Assert.True(result.DeadLettered);
        AssertDeadLetterExpiry(Assert.Single(Assert.Single(captured).Items), originalExpiryTtlSeconds: 3600);
    }

    [Fact]
    public async Task LockSweepPastMaxDeliveryCount_DeadLettersWithTheDeadLetterExpiry()
    {
        var h = await CreateActorAsync(lockConfig: new LockConfig { MaxDeliveryCount = 1, SweepBatchSize = 200 });
        var captured = CaptureDeadLetters(h);
        SeedLapsedLock(h.State, "lockaaaaaaa", itemExpiryAt: Now() + 3600, deliveryCount: 1);

        await h.Actor.Dequeue(new DequeueRequest());

        AssertDeadLetterExpiry(Assert.Single(Assert.Single(captured).Items), originalExpiryTtlSeconds: 3600);
        Assert.False(h.State.ContainsKey("lockaaaaaaa-lock"));
    }

    // ---- Dead-letter queues never dead-letter ----

    [Theory]
    [InlineData("test-queue-deadletter")]
    [InlineData("orders-session-s1-deadletter")]
    public async Task InADeadLetterQueue_DeadLetter_IsNotAllowed_AndTheLockIsLeft(string actorId)
    {
        var h = await CreateActorAsync(actorId: actorId);
        var locked = await LockOneAsync(h, Live(1));

        var result = await h.Actor.DeadLetter(new DeadLetterRequest { LockId = locked.LockId });

        Assert.Equal("ERROR", result.Status);
        Assert.Equal("DEADLETTER_NOT_ALLOWED", result.ErrorCode);
        Assert.True(h.State.ContainsKey($"{locked.LockId}-lock"));
        Assert.Equal(1, LockCount(h.State));
        h.Invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InADeadLetterQueue_NackPastMaxDeliveryCount_RestoresTheItem()
    {
        var h = await CreateActorAsync(actorId: "test-queue-deadletter", lockConfig: new LockConfig { MaxDeliveryCount = 0, SweepBatchSize = 200 });
        var locked = await LockOneAsync(h, Live(1));

        var result = await h.Actor.Nack(new NackRequest { LockId = locked.LockId });

        Assert.True(result.Success);
        Assert.False(result.DeadLettered);
        Assert.Equal(1, QueuedCount(h.State));
        h.Invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InADeadLetterQueue_LockSweepPastMaxDeliveryCount_RestoresTheItem()
    {
        var h = await CreateActorAsync(actorId: "test-queue-deadletter", lockConfig: new LockConfig { MaxDeliveryCount = 1, SweepBatchSize = 200 });
        SeedLapsedLock(h.State, "lockaaaaaaa", itemExpiryAt: Now() + 3600, deliveryCount: 1);

        var result = await h.Actor.Dequeue(new DequeueRequest());

        Assert.Single(result.Items);
        Assert.Equal(0, LockCount(h.State));
        h.Invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InASessionDeadLetterQueue_LeaseLapsePastMaxDeliveryCount_RestoresTheItem()
    {
        var h = await CreateActorAsync(actorId: "orders-session-s1-deadletter", lockConfig: new LockConfig { MaxDeliveryCount = 0, SweepBatchSize = 200 });
        h.State["lockalivexx-lock"] = new LockState
        {
            LockId = "lockalivexx", CreatedAt = Now() - 60, ExpiresAt = Now() - 30, Priority = 1, HeadSegment = 0,
            ItemJson = "{\"n\":1}", CompetingConsumerMode = false, ExpiryAt = Now() + 3600, ExpiryTtlSeconds = 3600
        };
        h.State["locks_session"] = new List<string> { "lockalivexx" };
        var metadata = (ActorMetadata)h.State["metadata"];
        h.State["metadata"] = metadata with { LockCount = 1, ActiveSessionLeaseId = "lease", ActiveSessionLeaseExpiresAt = Now() - 1 };

        await h.Actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease2", ExpiresAt = Now() + 60 });

        Assert.Equal(1, QueuedCount(h.State));
        Assert.Equal(0, LockCount(h.State));
        h.Invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InADeadLetterQueue_ExpiredItemsAreDropped_EvenWithDeadLetterOnExpiry()
    {
        var h = await CreateActorAsync(actorId: "test-queue-deadletter", expiryConfig: new ExpiryConfig { DeadLetterOnExpiry = true });
        await EnqueueAsync(h.Actor, Expired(1));

        var result = await h.Actor.Dequeue(new DequeueRequest());

        Assert.True(result.IsEmpty);
        Assert.Equal(0, QueuedCount(h.State));
        h.Invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Dead-letter on expiry ----

    private static ExpiryConfig DeadLetterOnExpiry => new() { DeadLetterOnExpiry = true };

    [Fact]
    public async Task DeadLetterOnExpiry_Dequeue_DeadLettersExpiredItemsInOneCall_BeforeTheCommit()
    {
        var h = await CreateActorAsync(expiryConfig: DeadLetterOnExpiry);
        await EnqueueAsync(h.Actor, Expired(1), Expired(2), Live(3));
        var events = new List<string>();
        var captured = CaptureDeadLetters(h, events);

        var result = await h.Actor.Dequeue(new DequeueRequest());

        Assert.Equal("{\"n\":3}", Assert.Single(result.Items).ItemJson);
        var request = Assert.Single(captured);
        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], request.Items.Select(i => i.ItemJson));
        AssertDeadLetterExpiry(request.Items[0], originalExpiryTtlSeconds: 3600);
        Assert.Equal(["dead-letter", "save"], events);
    }

    [Fact]
    public async Task DeadLetterOnExpiry_DequeueLocked_DeadLettersExpiredItems()
    {
        var h = await CreateActorAsync(expiryConfig: DeadLetterOnExpiry);
        await EnqueueAsync(h.Actor, Expired(1), Live(2));
        var captured = CaptureDeadLetters(h);

        var result = await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true });

        Assert.Equal("{\"n\":2}", Assert.Single(result.Items).ItemJson);
        Assert.Equal("{\"n\":1}", Assert.Single(Assert.Single(captured).Items).ItemJson);
    }

    [Fact]
    public async Task DeadLetterOnExpiry_FailedDeadLetter_DiscardsTheTurn_AndReturnsUnavailable()
    {
        var h = await CreateActorAsync(expiryConfig: DeadLetterOnExpiry);
        await EnqueueAsync(h.Actor, Expired(1), Live(2));
        CaptureDeadLetters(h, succeed: false);
        h.StateManager.Invocations.Clear();

        var result = await h.Actor.Dequeue(new DequeueRequest());

        Assert.Equal("UNAVAILABLE", result.ErrorCode);
        Assert.Empty(result.Items);
        h.StateManager.Verify(m => m.ClearCacheAsync(It.IsAny<CancellationToken>()), Times.Once);
        h.StateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeadLetterOnExpiry_DequeueLocked_FailedDeadLetter_DiscardsTheTurn_AndReturnsUnavailable()
    {
        var h = await CreateActorAsync(expiryConfig: DeadLetterOnExpiry);
        await EnqueueAsync(h.Actor, Expired(1), Live(2));
        CaptureDeadLetters(h, succeed: false);
        h.StateManager.Invocations.Clear();

        var result = await h.Actor.DequeueLocked(new DequeueLockedRequest { AllowCompetingConsumers = true });

        Assert.Equal("UNAVAILABLE", result.ErrorCode);
        Assert.Empty(result.Items);
        h.StateManager.Verify(m => m.ClearCacheAsync(It.IsAny<CancellationToken>()), Times.Once);
        h.StateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeadLetterOnExpiry_LockSweep_DeadLettersAnExpiredItem()
    {
        var h = await CreateActorAsync(expiryConfig: DeadLetterOnExpiry);
        var captured = CaptureDeadLetters(h);
        SeedLapsedLock(h.State, "lockaaaaaaa", itemExpiryAt: Now() - 10);

        await h.Actor.Dequeue(new DequeueRequest());

        AssertDeadLetterExpiry(Assert.Single(Assert.Single(captured).Items), originalExpiryTtlSeconds: 3600);
        Assert.False(h.State.ContainsKey("lockaaaaaaa-lock"));
        Assert.Equal(0, LockCount(h.State));
    }

    [Fact]
    public async Task DeadLetterOnExpiry_LockSweep_FailedDeadLetter_LeavesTheLockForALaterSweep()
    {
        var h = await CreateActorAsync(expiryConfig: DeadLetterOnExpiry);
        CaptureDeadLetters(h, succeed: false);
        SeedLapsedLock(h.State, "lockaaaaaaa", itemExpiryAt: Now() - 10);

        await h.Actor.Dequeue(new DequeueRequest());

        Assert.True(h.State.ContainsKey("lockaaaaaaa-lock"));
        Assert.Equal(1, LockCount(h.State));
    }

    [Fact]
    public async Task DeadLetterOnExpiry_Nack_DeadLettersAnExpiredItem()
    {
        var h = await CreateActorAsync(expiryConfig: DeadLetterOnExpiry);
        var captured = CaptureDeadLetters(h);
        var locked = await LockOneAsync(h, Item(1, Now() + 3600, 3600));
        var lockKey = $"{locked.LockId}-lock";
        h.State[lockKey] = ((LockState)h.State[lockKey]) with { ExpiryAt = Now() - 1 };

        var result = await h.Actor.Nack(new NackRequest { LockId = locked.LockId });

        Assert.True(result.Success);
        Assert.True(result.DeadLettered);
        Assert.Equal(0, result.DeliveryCount);
        AssertDeadLetterExpiry(Assert.Single(Assert.Single(captured).Items), originalExpiryTtlSeconds: 3600);
        Assert.False(h.State.ContainsKey(lockKey));
    }

    [Fact]
    public async Task DeadLetterOnExpiry_SessionLeaseLapse_DeadLettersExpiredItems_AndRestoresLiveOnes()
    {
        var h = await CreateActorAsync(actorId: "orders-session-s1", expiryConfig: DeadLetterOnExpiry);
        var captured = CaptureDeadLetters(h, deadLetterId: "orders-session-s1-deadletter");
        h.State["lockexpired-lock"] = new LockState
        {
            LockId = "lockexpired", CreatedAt = Now() - 60, ExpiresAt = Now() - 30, Priority = 1, HeadSegment = 0,
            ItemJson = "{\"n\":1}", CompetingConsumerMode = false, Sequence = 0, ExpiryAt = Now() - 10, ExpiryTtlSeconds = 3600
        };
        h.State["lockalivexx-lock"] = new LockState
        {
            LockId = "lockalivexx", CreatedAt = Now() - 60, ExpiresAt = Now() - 30, Priority = 1, HeadSegment = 0,
            ItemJson = "{\"n\":2}", CompetingConsumerMode = false, Sequence = 1, ExpiryAt = Now() + 3600, ExpiryTtlSeconds = 3600
        };
        h.State["locks_session"] = new List<string> { "lockexpired", "lockalivexx" };
        var metadata = (ActorMetadata)h.State["metadata"];
        h.State["metadata"] = metadata with { LockCount = 2, ActiveSessionLeaseId = "lease", ActiveSessionLeaseExpiresAt = Now() - 1 };

        await h.Actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease2", ExpiresAt = Now() + 60 });

        Assert.Equal("{\"n\":1}", Assert.Single(Assert.Single(captured).Items).ItemJson);
        Assert.False(h.State.ContainsKey("lockexpired-lock"));
        Assert.False(h.State.ContainsKey("locks_session"));
        Assert.Equal(0, LockCount(h.State));
        Assert.Equal(1, QueuedCount(h.State));
    }
}
