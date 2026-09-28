using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class QueueActorRaceConditionTests
{
    private Mock<IActorStateManager> CreateMockStateManager()
    {
        var mock = new Mock<IActorStateManager>();
        var stateData = new Dictionary<string, object>();

        mock.Setup(m => m.TryGetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is ActorMetadata metadata)
                {
                    return new ConditionalValue<ActorMetadata>(true, metadata);
                }
                return new ConditionalValue<ActorMetadata>(false, null);
            });

        mock.Setup(m => m.TryGetStateAsync<LockState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is LockState lockState)
                {
                    return new ConditionalValue<LockState>(true, lockState);
                }
                return new ConditionalValue<LockState>(false, null);
            });

        mock.Setup(m => m.TryGetStateAsync<Queue<QueueSegmentItem>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is Queue<QueueSegmentItem> queue)
                {
                    return new ConditionalValue<Queue<QueueSegmentItem>>(true, queue);
                }
                return new ConditionalValue<Queue<QueueSegmentItem>>(false, null);
            });

        // Lock expiry index entries ("locks_exp_{bucket}" / "locks_session").
        mock.Setup(m => m.TryGetStateAsync<List<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is List<string> list)
                {
                    return new ConditionalValue<List<string>>(true, list);
                }
                return new ConditionalValue<List<string>>(false, null);
            });

        mock.Setup(m => m.GetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is ActorMetadata metadata)
                {
                    return metadata;
                }
                return null!;
            });

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

        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private async Task<QueueActor> CreateActorAsync(Mock<IActorStateManager> mockStateManager)
    {
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>()))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object
        };

        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<Interfaces.EnqueueRequest, Interfaces.EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<Interfaces.EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Interfaces.EnqueueResponse { Success = true });

        var mockBlobReaperActorInvoker = new Mock<IBlobReaperActorInvoker>();
        mockBlobReaperActorInvoker.Setup(i => i.InvokeMethodAsync<Interfaces.ScheduleDeletionRequest>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<Interfaces.ScheduleDeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockSessionCoordinatorActorInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        var tokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
        {
            SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
            TokenTtl = TimeSpan.FromMinutes(5)
        });
        var blobReapConfig = new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 };
        var idempotencyConfig = new IdempotencyConfig { TtlSeconds = 86400 };
        var lockConfig = new LockConfig { MaxDeliveryCount = 10, SweepBatchSize = 200 };

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(actorHost, mockInvoker.Object, mockBlobReaperActorInvoker.Object, mockSessionCoordinatorActorInvoker.Object, tokenIssuer, blobReapConfig, idempotencyConfig, lockConfig);

        var stateManagerProperty = typeof(Actor).GetProperty("StateManager");
        stateManagerProperty?.SetValue(actor, mockStateManager.Object);

        var onActivateMethod = typeof(QueueActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return actor;
    }

    [Fact]
    public async Task Sweep_CommitsOnceRegardlessOfHowManyLocksExpire()
    {
        // The per-lock reminders this replaced each woke up and committed on their own, so N expiries
        // meant N separate write batches. The sweep resolves the whole backlog in one commit - which
        // is also what keeps the requeue and the LockCount decrement atomic with each other.
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Enqueue(new EnqueueRequest
        {
            Items = Enumerable.Range(0, 3)
                .Select(i => new EnqueueItem { ItemJson = $"{{\"id\":{i}}}", Priority = 1 })
                .ToList()
        });

        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest
        {
            Count = 3,
            TtlSeconds = 30,
            AllowCompetingConsumers = true
        });
        Assert.Equal(3, dequeueResult.Items.Count);

        // One batch shares one expiry, so all three sit in a single bucket - back-date the whole bucket.
        const int bucketWidth = 5;
        double originalExpiry = dequeueResult.Items[0].LockExpiresAt;
        double expiredAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        long oldBucket = (long)Math.Floor(originalExpiry / bucketWidth) * bucketWidth;
        long newBucket = (long)Math.Floor(expiredAt / bucketWidth) * bucketWidth;

        foreach (var item in dequeueResult.Items)
        {
            var lockState = await mockStateManager.Object.TryGetStateAsync<LockState>($"{item.LockId}-lock");
            await mockStateManager.Object.SetStateAsync($"{item.LockId}-lock", lockState.Value with { ExpiresAt = expiredAt });
        }

        await mockStateManager.Object.RemoveStateAsync($"locks_exp_{oldBucket}");
        await mockStateManager.Object.SetStateAsync(
            $"locks_exp_{newBucket}", dequeueResult.Items.Select(i => i.LockId!).ToList());
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        await mockStateManager.Object.SetStateAsync("metadata", metadata with { LockExpiryBuckets = [newBucket] });

        mockStateManager.Invocations.Clear();

        // Count = 0 so the only commits are the sweep's batch plus Dequeue's own trailing commit.
        await actor.Dequeue(new DequeueRequest { Count = 0 });

        var saveStateCalls = mockStateManager.Invocations
            .Count(i => i.Method.Name == "SaveStateAsync");

        Assert.Equal(2, saveStateCalls);

        // And all three really were resolved in that one batch.
        metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal(0, metadata.LockCount);
        Assert.Equal(3, metadata.Queues[1].Count);
    }
}
