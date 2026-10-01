using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Covers the lock index and the lazy expiry sweep that replaced the per-lock "lock-{lockId}"
/// reminders. The index is what makes outstanding locks enumerable at all - without it a lock is
/// reachable only via an id a caller already holds, so a lost reminder stranded the item forever.
/// </summary>
public class QueueActorLockExpiryTests
{
    private const int BucketWidthSeconds = 5;

    private static long BucketFor(double expiresAt) =>
        (long)Math.Floor(expiresAt / BucketWidthSeconds) * BucketWidthSeconds;

    private static string BucketKey(double expiresAt) => ExpiryBucketKey(BucketFor(expiresAt));

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
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is IdempotencyMarker im
                    ? new ConditionalValue<IdempotencyMarker>(true, im)
                    : new ConditionalValue<IdempotencyMarker>(false, null));

        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken ct) =>
            {
                stateData[key] = value;
                return Task.CompletedTask;
            });

        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, TimeSpan ttl, CancellationToken ct) =>
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

    private static async Task<(QueueActor actor, Dictionary<string, object> state)> CreateActorAsync(
        string actorId = "test-queue",
        LockConfig? lockConfig = null)
    {
        var (actor, state, _) = await CreateActorWithInvokerAsync(actorId, lockConfig);
        return (actor, state);
    }

    private static async Task<(QueueActor actor, Dictionary<string, object> state, Mock<IQueueActorInvoker> invoker)>
        CreateActorWithInvokerAsync(string actorId = "test-queue", LockConfig? lockConfig = null)
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);

        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>())).Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object,
            ActorId = new ActorId(actorId)
        };

        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true });

        var mockBlobReaperActorInvoker = new Mock<IBlobReaperActorInvoker>();
        mockBlobReaperActorInvoker.Setup(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ScheduleDeletionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockSessionCoordinatorActorInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockSessionCoordinatorActorInvoker.Setup(i => i.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<RegisterSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegisterSessionResponse { Success = true });

        var tokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
        {
            SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
            TokenTtl = TimeSpan.FromMinutes(5)
        });

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(
            actorHost,
            mockInvoker.Object,
            mockBlobReaperActorInvoker.Object,
            mockSessionCoordinatorActorInvoker.Object,
            tokenIssuer,
            new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 },
            new IdempotencyConfig { TtlSeconds = 86400 },
            lockConfig ?? new LockConfig { MaxDeliveryCount = 10, SweepBatchSize = 200 });

        typeof(Actor).GetProperty("StateManager")!.SetValue(actor, mockStateManager.Object);

        var onActivate = typeof(QueueActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)onActivate.Invoke(actor, null)!;

        return (actor, stateData, mockInvoker);
    }

    private static async Task EnqueueAsync(QueueActor actor, params string[] items)
    {
        foreach (var item in items)
        {
            await actor.Enqueue(new EnqueueRequest
            {
                Items = [new EnqueueItem { ItemJson = item, Priority = 1 }]
            });
        }
    }

    /// <summary>
    /// Plants a lock with an exact ExpiresAt, plus its index entry and LockCount, so expiry timing is
    /// deterministic instead of depending on wall-clock TTL granularity.
    /// </summary>
    private static void SeedLock(
        Dictionary<string, object> state, string lockId, double expiresAt, string itemJson, int priority = 1)
    {
        state[$"{lockId}-lock"] = new LockState
        {
            LockId = lockId,
            CreatedAt = expiresAt - 30,
            ExpiresAt = expiresAt,
            Priority = priority,
            HeadSegment = 0,
            ItemJson = itemJson,
            CompetingConsumerMode = true
        };

        long bucket = BucketFor(expiresAt);
        string key = ExpiryBucketKey(bucket);
        var ids = state.TryGetValue(key, out var existing) ? (List<string>)existing : new List<string>();
        ids.Add(lockId);
        state[key] = ids;

        var metadata = (ActorMetadata)state["metadata"];
        var buckets = new List<long>(metadata.LockExpiryBuckets);
        if (!buckets.Contains(bucket))
        {
            buckets.Add(bucket);
            buckets.Sort();
        }
        state["metadata"] = metadata with { LockCount = metadata.LockCount + 1, LockExpiryBuckets = buckets };
    }

    private static string ExpiryBucketKey(long bucket) => $"locks_exp_{bucket}";

    /// <summary>
    /// Backdates an existing lock so it reads as lapsed, moving its index entry into the now-due
    /// bucket as well - the sweep looks at buckets first, so rewriting ExpiresAt alone would leave it
    /// filed under a bucket that isn't due yet and it would never be examined.
    /// </summary>
    private static void ForceExpire(Dictionary<string, object> state, string lockId)
    {
        var lockState = (LockState)state[$"{lockId}-lock"];
        double expiresAt = Now() - 1;

        long oldBucket = BucketFor(lockState.ExpiresAt);
        if (state.TryGetValue(ExpiryBucketKey(oldBucket), out var oldIds))
        {
            var ids = (List<string>)oldIds;
            ids.Remove(lockId);
            if (ids.Count == 0)
            {
                state.Remove(ExpiryBucketKey(oldBucket));
            }
        }

        state[$"{lockId}-lock"] = lockState with { ExpiresAt = expiresAt };

        long newBucket = BucketFor(expiresAt);
        var newIds = state.TryGetValue(ExpiryBucketKey(newBucket), out var existing)
            ? (List<string>)existing
            : new List<string>();
        newIds.Add(lockId);
        state[ExpiryBucketKey(newBucket)] = newIds;

        var metadata = (ActorMetadata)state["metadata"];
        var buckets = metadata.LockExpiryBuckets.Where(b => b != oldBucket || state.ContainsKey(ExpiryBucketKey(b))).ToList();
        if (!buckets.Contains(newBucket))
        {
            buckets.Add(newBucket);
            buckets.Sort();
        }
        state["metadata"] = metadata with { LockExpiryBuckets = buckets };
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Sweep_RequeuesExpiredLock_AndClearsItsIndexEntry()
    {
        var (actor, state) = await CreateActorAsync();
        SeedLock(state, "lockaaaaaaa", Now() - 10, "{\"id\":\"A\"}");
        var bucketKey = ExpiryBucketKey(BucketFor(Now() - 10));

        // Dequeue drives the lazy sweep: the expired lock's item must come back before serving.
        var result = await actor.Dequeue(new DequeueRequest());

        Assert.Single(result.Items);
        Assert.Contains("\"id\":\"A\"", result.Items[0].ItemJson);
        Assert.False(state.ContainsKey("lockaaaaaaa-lock"));
        Assert.False(state.ContainsKey(bucketKey));

        var metadata = (ActorMetadata)state["metadata"];
        Assert.Equal(0, metadata.LockCount);
        Assert.Empty(metadata.LockExpiryBuckets);
    }

    [Fact]
    public async Task Sweep_RestoresAnExpiredLocksItemToItsOriginalPosition()
    {
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"n\":1}", "{\"n\":2}", "{\"n\":3}");

        // Take the head item and let its lock lapse unacked. It was ahead of 2 and 3 when it was
        // locked, so it belongs ahead of them again - not on the tail behind them.
        var locked = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30, AllowCompetingConsumers = true });
        ForceExpire(state, locked.Items[0].LockId);

        Assert.Equal([1, 2, 3], await DrainAsync(actor, 3));
    }

    [Fact]
    public async Task Sweep_BacklogLargerThanTheBudget_StillRestoresInOrder()
    {
        // The case a plain front-insert cannot handle. With a budget of 2 the five expired locks
        // drain over three sweeps; each sweep restores below the previous one's head, so anything
        // that places by arrival rather than by Sequence hands back 5,4,3,2,1 or similar.
        var (actor, state) = await CreateActorAsync(
            lockConfig: new LockConfig { MaxDeliveryCount = 10, SweepBatchSize = 2 });
        await EnqueueAsync(actor, "{\"n\":1}", "{\"n\":2}", "{\"n\":3}", "{\"n\":4}", "{\"n\":5}");

        var locked = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 5,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        Assert.Equal(5, locked.Items.Count);

        foreach (var item in locked.Items)
        {
            ForceExpire(state, item.LockId);
        }

        Assert.Equal([1, 2, 3, 4, 5], await DrainAsync(actor, 5));
    }

    [Fact]
    public async Task Sweep_LocksExpiringInSeparateSweeps_RestoreInOrder()
    {
        // The same inversion by a different route: the *later* items lapse and are restored first,
        // so the earlier ones have to merge in ahead of items already back in the queue.
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"n\":1}", "{\"n\":2}", "{\"n\":3}", "{\"n\":4}");

        var locked = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 4,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        ForceExpire(state, locked.Items[2].LockId);
        ForceExpire(state, locked.Items[3].LockId);
        await actor.Dequeue(new DequeueRequest { Count = 0 }); // drives one sweep

        ForceExpire(state, locked.Items[0].LockId);
        ForceExpire(state, locked.Items[1].LockId);

        Assert.Equal([1, 2, 3, 4], await DrainAsync(actor, 4));
    }

    [Fact]
    public async Task Sweep_RestoreStaysWithinItsOwnPriority()
    {
        var (actor, state) = await CreateActorAsync();
        await actor.Enqueue(new EnqueueRequest
        {
            Items =
            [
                new EnqueueItem { ItemJson = "{\"n\":1}", Priority = 0 },
                new EnqueueItem { ItemJson = "{\"n\":2}", Priority = 0 },
                new EnqueueItem { ItemJson = "{\"n\":3}", Priority = 1 },
                new EnqueueItem { ItemJson = "{\"n\":4}", Priority = 1 }
            ]
        });

        // One lock per priority, both lapsed. Each item must land back in its own lane, and the
        // fast lane must still be served first.
        var locked = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 1,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        ForceExpire(state, locked.Items[0].LockId);
        await actor.Dequeue(new DequeueRequest { Count = 0 });

        Assert.Equal([1, 2, 3, 4], await DrainAsync(actor, 4));
    }

    [Fact]
    public async Task Sweep_RestoreLargerThanTheHeadSegment_SpillsBelowIt()
    {
        // Locking a whole segment's worth drains segment 0 and advances the head to segment 1, so
        // the restore has 100 items to merge into a head that is already full. It has to allocate
        // below the head rather than overflow MaxSegmentSize.
        var (actor, state) = await CreateActorAsync();
        await actor.Enqueue(new EnqueueRequest
        {
            Items = Enumerable.Range(1, 200)
                .Select(n => new EnqueueItem { ItemJson = $"{{\"n\":{n}}}", Priority = 1 })
                .ToList()
        });

        var locked = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 100,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        Assert.Equal(100, locked.Items.Count);
        Assert.Equal(1, ((ActorMetadata)state["metadata"]).Queues[1].HeadSegment);

        foreach (var item in locked.Items)
        {
            ForceExpire(state, item.LockId);
        }

        await actor.Dequeue(new DequeueRequest { Count = 0 }); // drives the sweep

        var queueMeta = ((ActorMetadata)state["metadata"]).Queues[1];
        Assert.Equal(0, queueMeta.HeadSegment);
        Assert.Equal(200, queueMeta.Count);
        Assert.Equal(100, ((Queue<QueueSegmentItem>)state["queue_1_seg_0"]).Count);
        Assert.Equal(100, ((Queue<QueueSegmentItem>)state["queue_1_seg_1"]).Count);

        Assert.Equal(Enumerable.Range(1, 200), await DrainAsync(actor, 200));
        Assert.Empty(((ActorMetadata)state["metadata"]).Queues);
    }

    /// <summary>
    /// Drains <paramref name="count"/> items and returns their "n" values in the order served. The
    /// first Dequeue also drives the lazy sweep, so callers just ForceExpire and drain.
    /// </summary>
    private static async Task<List<int>> DrainAsync(QueueActor actor, int count)
    {
        var served = new List<int>();
        for (int attempt = 0; served.Count < count && attempt < count * 4; attempt++)
        {
            // Each call sweeps before it serves, and a sweep capped by SweepBatchSize leaves locks
            // outstanding - so the early calls legitimately come back Locked. Keep going.
            var next = await actor.Dequeue(new DequeueRequest());
            if (next.Items.Count == 0)
            {
                Assert.True(next.Locked, $"expected {count} items, ran dry after {served.Count}");
                continue;
            }

            served.Add(int.Parse(next.Items[0].ItemJson.Split(':')[1].TrimEnd('}')));
        }

        Assert.Equal(count, served.Count);
        return served;
    }

    [Fact]
    public async Task Sweep_PartiallyDueBucket_ExpiresLapsedLocksAndLeavesTheRest()
    {
        var (actor, state) = await CreateActorAsync();

        // Index both under the same (lapsed) bucket, then push B's real expiry into the future - the
        // shape ExtendLock produces, and the reason a due bucket can still hold live locks.
        double lapsed = Now() - 10;
        SeedLock(state, "lockaaaaaaa", lapsed, "{\"id\":\"A\"}");
        SeedLock(state, "lockbbbbbbb", lapsed, "{\"id\":\"B\"}");
        state["lockbbbbbbb-lock"] = ((LockState)state["lockbbbbbbb-lock"]) with { ExpiresAt = Now() + 600 };

        // B's lock is still live, so the legacy single-lock gate blocks the read itself - but the
        // sweep still ran, which is what this test is about.
        var result = await actor.Dequeue(new DequeueRequest());
        Assert.True(result.Locked);

        Assert.False(state.ContainsKey("lockaaaaaaa-lock"));
        Assert.True(state.ContainsKey("lockbbbbbbb-lock"));

        var remaining = (List<string>)state[ExpiryBucketKey(BucketFor(lapsed))];
        Assert.Equal(["lockbbbbbbb"], remaining);

        var metadata = (ActorMetadata)state["metadata"];
        Assert.Equal(1, metadata.LockCount);
        Assert.Equal(1, metadata.Queues[1].Count); // A's item came back
    }

    [Fact]
    public async Task Sweep_LockExpiringNow_IsResolvedByTheVeryNextOperation()
    {
        // The floor-bucketing guarantee: a bucket is due from its earliest possible expiry, so a
        // just-lapsed lock is never held back by the bucket width.
        var (actor, state) = await CreateActorAsync();
        SeedLock(state, "lockaaaaaaa", Now(), "{\"id\":\"A\"}");

        var result = await actor.Dequeue(new DequeueRequest());

        Assert.Single(result.Items);
        Assert.Contains("\"id\":\"A\"", result.Items[0].ItemJson);
    }

    [Fact]
    public async Task Sweep_StopsAtSweepBatchSize()
    {
        var (actor, state) = await CreateActorAsync(
            lockConfig: new LockConfig { MaxDeliveryCount = 10, SweepBatchSize = 2 });

        for (int i = 0; i < 5; i++)
        {
            SeedLock(state, $"lock{i}aaaaaa", Now() - 10, $"{{\"id\":{i}}}");
        }

        await actor.Dequeue(new DequeueRequest());

        // Two expired per operation: one requeued item was served, one remains queued, three locked.
        Assert.Equal(3, ((ActorMetadata)state["metadata"]).LockCount);
    }

    [Fact]
    public async Task Dequeue_NonCompetingMode_ServesOnceEveryLockHasExpired()
    {
        // Regression guard: the sweep has to run before the LockCount>0 gate, or a queue whose locks
        // are all dead reports Locked forever and never recovers.
        var (actor, state) = await CreateActorAsync();
        SeedLock(state, "lockaaaaaaa", Now() - 10, "{\"id\":\"A\"}");

        var result = await actor.Dequeue(new DequeueRequest());

        Assert.False(result.Locked);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Sweep_IncrementsDeliveryCount_AndItSurvivesTheLockRoundTrip()
    {
        var (actor, state) = await CreateActorAsync();
        SeedLock(state, "lockaaaaaaa", Now() - 10, "{\"id\":\"A\"}");

        // First expiry: 0 -> 1, requeued to the tail.
        await actor.Dequeue(new DequeueRequest { Count = 0 });

        var segment = (Queue<QueueSegmentItem>)state["queue_1_seg_0"];
        Assert.Equal(1, segment.Peek().DeliveryCount);

        // Lock it again and expire it again: the count must carry through lock state, not reset.
        var locked = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30, AllowCompetingConsumers = true });
        var lockId = locked.Items[0].LockId;
        Assert.Equal(1, ((LockState)state[$"{lockId}-lock"]).DeliveryCount);

        ForceExpire(state, lockId);
        await actor.Dequeue(new DequeueRequest { Count = 0 });

        segment = (Queue<QueueSegmentItem>)state["queue_1_seg_0"];
        Assert.Equal(2, segment.Peek().DeliveryCount);
    }

    [Fact]
    public async Task Sweep_RoutesToDeadLetterOnceMaxDeliveryCountIsExceeded()
    {
        var (actor, state, invoker) = await CreateActorWithInvokerAsync(
            lockConfig: new LockConfig { MaxDeliveryCount = 2, SweepBatchSize = 200 });

        SeedLock(state, "lockaaaaaaa", Now() - 10, "{\"id\":\"A\"}");
        state["lockaaaaaaa-lock"] = ((LockState)state["lockaaaaaaa-lock"]) with { DeliveryCount = 2 };

        await actor.Dequeue(new DequeueRequest { Count = 0 });

        // Exceeding the max routes the item out instead of requeueing it, so the queue stays empty.
        invoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.Is<ActorId>(id => id.GetId() == "test-queue-deadletter"),
            "Enqueue",
            It.IsAny<EnqueueRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.False(state.ContainsKey("lockaaaaaaa-lock"));
        var metadata = (ActorMetadata)state["metadata"];
        Assert.Equal(0, metadata.LockCount);
        Assert.False(metadata.Queues.ContainsKey(1));
    }

    [Fact]
    public async Task DequeueLocked_IndexesWholeBatchInOneExpiryBucket()
    {
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"id\":\"A\"}", "{\"id\":\"B\"}", "{\"id\":\"C\"}");

        var result = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 3,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        Assert.Equal(3, result.Items.Count);

        // All three share one expiry, so they share one bucket - one index key, one write.
        var bucketKey = BucketKey(result.Items[0].LockExpiresAt);
        Assert.True(state.ContainsKey(bucketKey), $"expected index key {bucketKey}");
        var indexed = Assert.IsType<List<string>>(state[bucketKey]);
        Assert.Equal(result.Items.Select(i => i.LockId).ToList(), indexed);

        var metadata = Assert.IsType<ActorMetadata>(state["metadata"]);
        Assert.Equal([BucketFor(result.Items[0].LockExpiresAt)], metadata.LockExpiryBuckets);
    }

    [Fact]
    public async Task Acknowledge_LeavesTheBucketedIndexAlone_AndTheSweepDiscardsTheStaleEntry()
    {
        // Settling is the hot path and a bucket can hold every lock from one bulk dequeue, so ack must
        // not rewrite that list - it would be O(n) per ack and O(n^2) over a batch. The index is
        // allowed to over-approximate; the sweep is what reconciles it.
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"id\":\"A\"}", "{\"id\":\"B\"}");

        var result = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 2,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        var bucketKey = BucketKey(result.Items[0].LockExpiresAt);

        await actor.Acknowledge(new AcknowledgeRequest { LockId = result.Items[0].LockId });
        await actor.Acknowledge(new AcknowledgeRequest { LockId = result.Items[1].LockId });

        // Both ids are still filed, but both locks are gone and LockCount is accurate.
        Assert.Equal(2, Assert.IsType<List<string>>(state[bucketKey]).Count);
        Assert.False(state.ContainsKey($"{result.Items[0].LockId}-lock"));
        Assert.Equal(0, ((ActorMetadata)state["metadata"]).LockCount);

        // Once the bucket comes due the sweep finds no live locks behind it and drops it.
        var metadata = (ActorMetadata)state["metadata"];
        state["metadata"] = metadata with { LockExpiryBuckets = [BucketFor(Now() - 10)] };
        state[ExpiryBucketKey(BucketFor(Now() - 10))] = (List<string>)state[bucketKey];

        await actor.Dequeue(new DequeueRequest { Count = 0 });

        Assert.Empty(((ActorMetadata)state["metadata"]).LockExpiryBuckets);
        Assert.Equal(0, ((ActorMetadata)state["metadata"]).LockCount);
    }

    [Fact]
    public async Task Acknowledge_OnSessionActor_PrunesTheSessionIndex()
    {
        // The session index has no bucket to age out, so under a long lease it would grow by one
        // entry per message ever delivered. Its length is prefetch-bounded, so pruning is cheap here.
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}", "{\"id\":\"B\"}");

        var result = await LeaseAndLockAsync(actor, "lease-1", Now() + 600, 2);

        await actor.Acknowledge(new AcknowledgeRequest { LockId = result.Items[0].LockId, LeaseId = "lease-1" });
        Assert.Equal([result.Items[1].LockId], Assert.IsType<List<string>>(state["locks_session"]));

        await actor.Acknowledge(new AcknowledgeRequest { LockId = result.Items[1].LockId, LeaseId = "lease-1" });
        Assert.False(state.ContainsKey("locks_session"));
    }

    [Fact]
    public async Task ExtendLock_MovesLockIdToTheNewExpiryBucket()
    {
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        var result = await actor.DequeueLocked(new DequeueLockedRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        var lockId = result.Items[0].LockId;
        var originalBucketKey = BucketKey(result.Items[0].LockExpiresAt);

        var extended = await actor.ExtendLock(new ExtendLockRequest
        {
            LockId = lockId,
            AdditionalTtlSeconds = 60
        });

        Assert.True(extended.Success);

        var newBucketKey = BucketKey(extended.NewExpiresAt);
        Assert.NotEqual(originalBucketKey, newBucketKey);
        Assert.Equal([lockId], Assert.IsType<List<string>>(state[newBucketKey]));

        // The old entry is left behind rather than rewritten out; the sweep keeps a not-yet-expired
        // id as a survivor, so the duplicate is harmless and costs one string instead of an O(n) pass.
        var metadata = Assert.IsType<ActorMetadata>(state["metadata"]);
        Assert.Contains(BucketFor(extended.NewExpiresAt), metadata.LockExpiryBuckets);
    }

    [Fact]
    public async Task DeadLetter_RemovesLockIdFromBucket()
    {
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        var result = await actor.DequeueLocked(new DequeueLockedRequest
        {
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        var bucketKey = BucketKey(result.Items[0].LockExpiresAt);

        var dead = await actor.DeadLetter(new DeadLetterRequest { LockId = result.Items[0].LockId });

        Assert.Equal("SUCCESS", dead.Status);
        // Same over-approximation contract as Acknowledge: the lock goes, the index entry ages out.
        Assert.False(state.ContainsKey($"{result.Items[0].LockId}-lock"));
        Assert.Equal(0, ((ActorMetadata)state["metadata"]).LockCount);
    }

    [Fact]
    public async Task Acknowledge_OnPlainActor_RejectsAnExpiredLock()
    {
        // Consistent with ExtendLock and DeadLetter, which both already refuse an expired lock.
        // Acknowledging one would silently discard an item that is about to be redelivered.
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        var locked = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30, AllowCompetingConsumers = true });
        var lockId = locked.Items[0].LockId;
        state[$"{lockId}-lock"] = ((LockState)state[$"{lockId}-lock"]) with { ExpiresAt = Now() - 1 };

        var ack = await actor.Acknowledge(new AcknowledgeRequest { LockId = lockId });

        Assert.False(ack.Success);
        Assert.Equal("LOCK_EXPIRED", ack.ErrorCode);
    }

    [Fact]
    public async Task DequeueLocked_ComputesMaxConcurrencyCapacityAfterSweeping()
    {
        // Capacity sized against dead locks would refuse a consumer that should be allowed through.
        var (actor, state) = await CreateActorAsync();
        await EnqueueAsync(actor, "{\"id\":\"A\"}");
        SeedLock(state, "lockaaaaaaa", Now() - 10, "{\"id\":\"B\"}");

        var result = await actor.DequeueLocked(new DequeueLockedRequest
        {
            TtlSeconds = 30,
            MaxConcurrency = 1,
            AllowCompetingConsumers = true
        });

        Assert.False(result.MaxConcurrencyReached);
        Assert.Single(result.Items);
    }

    private const string SessionActorId = "orders-session-order-42";

    private static async Task<DequeueLockedResponse> LeaseAndLockAsync(
        QueueActor actor, string leaseId, double leaseExpiresAt, int count)
    {
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = leaseId, ExpiresAt = leaseExpiresAt });
        return await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = count,
            TtlSeconds = 30,
            LeaseId = leaseId,
            AllowCompetingConsumers = true
        });
    }

    /// <summary>Expires the synced lease without going through the coordinator.</summary>
    private static void LapseLease(Dictionary<string, object> state)
    {
        var metadata = (ActorMetadata)state["metadata"];
        state["metadata"] = metadata with { ActiveSessionLeaseExpiresAt = Now() - 1 };
    }

    [Fact]
    public async Task DequeueLocked_OnSessionActor_ReportsLockExpiryAsTheLeaseExpiry()
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        double leaseExpiresAt = Now() + 45;
        var result = await LeaseAndLockAsync(actor, "lease-1", leaseExpiresAt, 1);

        // The lease is the authority, so the per-item expiry just mirrors it for the client's benefit
        // rather than describing an independent TTL.
        Assert.Equal(leaseExpiresAt, result.Items[0].LockExpiresAt);
    }

    [Fact]
    public async Task Acknowledge_OnSessionActor_SucceedsAfterTheLeaseIsRenewedPastTheLockExpiry()
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        var result = await LeaseAndLockAsync(actor, "lease-1", Now() + 2, 1);
        var lockId = result.Items[0].LockId;

        // Renewal pushes the lease out; the lock's own stale ExpiresAt must not be consulted, or a
        // long-running handler would lose a message it still legitimately holds.
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-1", ExpiresAt = Now() + 600 });
        state[$"{lockId}-lock"] = ((LockState)state[$"{lockId}-lock"]) with { ExpiresAt = Now() - 1 };

        var ack = await actor.Acknowledge(new AcknowledgeRequest { LockId = lockId, LeaseId = "lease-1" });

        Assert.True(ack.Success);
    }

    [Fact]
    public async Task LapsedSessionLease_BulkRequeuesLocksToTheFrontInOrder()
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}", "{\"id\":\"B\"}", "{\"id\":\"C\"}");

        // Consumer takes A and B, then dies without acking.
        var locked = await LeaseAndLockAsync(actor, "lease-1", Now() + 60, 2);
        Assert.Equal(2, locked.Items.Count);

        LapseLease(state);

        // Reclaiming the session activates the actor; the sweep restores A and B ahead of C so the
        // session's FIFO order survives the consumer's death.
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-2", ExpiresAt = Now() + 60 });

        var served = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var next = await actor.Dequeue(new DequeueRequest { LeaseId = "lease-2" });
            served.Add(next.Items[0].ItemJson);
        }

        Assert.Collection(served,
            j => Assert.Contains("\"id\":\"A\"", j),
            j => Assert.Contains("\"id\":\"B\"", j),
            j => Assert.Contains("\"id\":\"C\"", j));

        var metadata = (ActorMetadata)state["metadata"];
        Assert.Equal(0, metadata.LockCount);
        Assert.False(state.ContainsKey("locks_session"));
    }

    [Fact]
    public async Task LapsedSessionLease_IncrementsDeliveryCountOnTheRestoredItems()
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        await LeaseAndLockAsync(actor, "lease-1", Now() + 60, 1);
        LapseLease(state);

        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-2", ExpiresAt = Now() + 60 });

        var locked = await actor.DequeueLocked(new DequeueLockedRequest
        {
            TtlSeconds = 30,
            LeaseId = "lease-2",
            AllowCompetingConsumers = true
        });
        Assert.Equal(1, ((LockState)state[$"{locked.Items[0].LockId}-lock"]).DeliveryCount);
    }

    [Fact]
    public async Task LapsedSessionLease_RestoresMoreThanOneSegmentInOrder()
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);

        // 150 items spans two segments, so the restore has to allocate a descending run and still
        // hand them back in the original order.
        var items = Enumerable.Range(0, 150).Select(i => $"{{\"n\":{i}}}").ToArray();
        await actor.Enqueue(new EnqueueRequest
        {
            Items = items.Select(i => new EnqueueItem { ItemJson = i, Priority = 1 }).ToList()
        });

        await LeaseAndLockAsync(actor, "lease-1", Now() + 60, 150);
        LapseLease(state);
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-2", ExpiresAt = Now() + 60 });

        var metadata = (ActorMetadata)state["metadata"];
        Assert.Equal(150, metadata.Queues[1].Count);
        Assert.True(metadata.Queues[1].HeadSegment <= metadata.Queues[1].TailSegment);

        var served = new List<string>();
        for (int i = 0; i < 150; i++)
        {
            var next = await actor.Dequeue(new DequeueRequest { LeaseId = "lease-2" });
            served.Add(next.Items[0].ItemJson);
        }

        Assert.Equal(items, served);
    }

    [Fact]
    public async Task LapsedSessionLease_DrainedPriority_RestoresIntoTheLiveRange()
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        // Locking everything drains the priority, removing its metadata entry entirely - the common
        // case for a session consumer, and the one where there is no live head to count back from.
        await LeaseAndLockAsync(actor, "lease-1", Now() + 60, 1);
        Assert.False(((ActorMetadata)state["metadata"]).Queues.ContainsKey(1));

        LapseLease(state);
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-2", ExpiresAt = Now() + 60 });

        // Nothing is left to merge into, so the restore reuses the vacated head slot rather than
        // allocating below it - no stray empty segment, and the pointers stay consistent.
        var queueMeta = ((ActorMetadata)state["metadata"]).Queues[1];
        Assert.Equal(0, queueMeta.HeadSegment);
        Assert.Equal(0, queueMeta.TailSegment);
        Assert.True(state.ContainsKey("queue_1_seg_0"));

        // A later enqueue appends after the restored item and rolls upward from there.
        await EnqueueAsync(actor, "{\"id\":\"B\"}");
        var first = await actor.Dequeue(new DequeueRequest { LeaseId = "lease-2" });
        Assert.Contains("\"id\":\"A\"", first.Items[0].ItemJson);
        var second = await actor.Dequeue(new DequeueRequest { LeaseId = "lease-2" });
        Assert.Contains("\"id\":\"B\"", second.Items[0].ItemJson);
    }

    [Theory]
    [InlineData("lease-1")]
    [InlineData("wrong-lease")]
    [InlineData(null)]
    public async Task LapsedSessionLease_WithOutstandingLocks_KeepsRefusingEveryCaller(string? leaseId)
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        await LeaseAndLockAsync(actor, "lease-1", Now() + 60, 1);
        LapseLease(state);

        // The first call after the lapse reclaims A. Reclaiming must not strip the lease, or the
        // guard has nothing to enforce and lets this and every later caller through.
        for (int i = 0; i < 3; i++)
        {
            var locked = await actor.DequeueLocked(new DequeueLockedRequest
            {
                TtlSeconds = 30,
                LeaseId = leaseId,
                AllowCompetingConsumers = true
            });
            Assert.Equal("SESSION_LEASE_EXPIRED", locked.ErrorCode);

            var plain = await actor.Dequeue(new DequeueRequest { LeaseId = leaseId });
            Assert.Equal("SESSION_LEASE_EXPIRED", plain.ErrorCode);
        }
    }

    [Fact]
    public async Task LapsedSessionLease_PolledRepeatedly_ReclaimsOnlyOnce()
    {
        var (actor, state) = await CreateActorAsync(actorId: SessionActorId);
        await EnqueueAsync(actor, "{\"id\":\"A\"}");

        await LeaseAndLockAsync(actor, "lease-1", Now() + 60, 1);
        LapseLease(state);

        for (int i = 0; i < 3; i++)
        {
            await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30, LeaseId = "lease-1", AllowCompetingConsumers = true });
        }

        // One lapse is one failed delivery, however many times the old holder polled after it.
        var next = await LeaseAndLockAsync(actor, "lease-2", Now() + 60, 1);
        Assert.Equal(1, ((LockState)state[$"{next.Items[0].LockId}-lock"]).DeliveryCount);
    }

    [Fact]
    public async Task DequeueLocked_OnSessionActor_IndexesIntoTheSessionKey()
    {
        var (actor, state) = await CreateActorAsync(actorId: "orders-session-order-42");
        await EnqueueAsync(actor, "{\"id\":\"A\"}", "{\"id\":\"B\"}");

        var result = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 2,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });

        // Session locks live and die with the lease, so they are not bucketed by expiry at all.
        Assert.Equal(result.Items.Select(i => i.LockId).ToList(), Assert.IsType<List<string>>(state["locks_session"]));
        var metadata = Assert.IsType<ActorMetadata>(state["metadata"]);
        Assert.Empty(metadata.LockExpiryBuckets);
    }
}
