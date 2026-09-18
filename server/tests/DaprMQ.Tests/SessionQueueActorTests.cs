using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class SessionQueueActorTests
{
    private (Mock<IActorStateManager> mock, Dictionary<string, object> stateData) CreateMockStateManager()
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

        mock.Setup(m => m.TryGetStateAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is string stringValue)
                {
                    return new ConditionalValue<string>(true, stringValue);
                }
                return new ConditionalValue<string>(false, null);
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

        mock.Setup(m => m.TryGetStateAsync<List<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is List<string> list)
                {
                    return new ConditionalValue<List<string>>(true, list);
                }
                return new ConditionalValue<List<string>>(false, null);
            });

        mock.Setup(m => m.TryGetStateAsync<IdempotencyMarker>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is IdempotencyMarker marker)
                {
                    return new ConditionalValue<IdempotencyMarker>(true, marker);
                }
                return new ConditionalValue<IdempotencyMarker>(false, null);
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

        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (mock, stateData);
    }

    /// <summary>
    /// Creates and activates a QueueActor with the given actor id (default: a plain, non-session
    /// id). RegisterSession calls the mocked ISessionCoordinatorActorInvoker makes
    /// (self-registration, fired from OnActivateAsync when the id identifies this as a session
    /// actor) resolve via registerSessionResponse/registerSessionThrows.
    /// </summary>
    private async Task<(QueueActor actor, Mock<ISessionCoordinatorActorInvoker> mockSessionCoordinatorInvoker)> CreateActorAsync(
        Mock<IActorStateManager> mockStateManager,
        string actorId = "test-queue",
        RegisterSessionResponse? registerSessionResponse = null,
        Exception? registerSessionThrows = null)
    {
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>()))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object,
            ActorId = new ActorId(actorId)
        };

        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true });

        var mockSessionCoordinatorInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        var registerSetup = mockSessionCoordinatorInvoker.Setup(i => i.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<RegisterSessionRequest>(),
                It.IsAny<CancellationToken>()));
        if (registerSessionThrows != null)
        {
            registerSetup.ThrowsAsync(registerSessionThrows);
        }
        else
        {
            registerSetup.ReturnsAsync(registerSessionResponse ?? new RegisterSessionResponse { Success = true });
        }

        var mockBlobReaperActorInvoker = new Mock<IBlobReaperActorInvoker>();
        mockBlobReaperActorInvoker.Setup(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ScheduleDeletionRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
        {
            SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
            TokenTtl = TimeSpan.FromMinutes(5)
        });
        var blobReapConfig = new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 };
        var idempotencyConfig = new IdempotencyConfig { TtlSeconds = 86400 };

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(
            actorHost,
            mockInvoker.Object,
            mockBlobReaperActorInvoker.Object,
            mockSessionCoordinatorInvoker.Object,
            tokenIssuer,
            blobReapConfig,
            idempotencyConfig);

        var stateManagerProperty = typeof(Actor).GetProperty("StateManager");
        stateManagerProperty?.SetValue(actor, mockStateManager.Object);

        var onActivateMethod = typeof(QueueActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return (actor, mockSessionCoordinatorInvoker);
    }

    [Fact]
    public async Task Activation_WithSessionActorId_RegistersWithSessionCoordinatorActor()
    {
        // Arrange / Act
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, mockSessionCoordinatorInvoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");

        // Assert - self-registered exactly once, against the SessionCoordinatorActor for this
        // queue (same id as the queue, different actor type), with the parsed-out session id.
        mockSessionCoordinatorInvoker.Verify(i => i.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
            It.Is<ActorId>(id => id.GetId() == "orders"),
            "RegisterSession",
            It.Is<RegisterSessionRequest>(r => r.SessionId == "order-42"),
            It.IsAny<CancellationToken>()), Times.Once());

        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.True(metadata.HasRegisteredSession);
    }

    [Fact]
    public async Task Activation_WithPlainActorId_DoesNotAttemptRegistration()
    {
        // Regression: an ordinary queue actor (no "-session-" in its id) never calls RegisterSession.
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, mockSessionCoordinatorInvoker) = await CreateActorAsync(mockStateManager, actorId: "orders");

        mockSessionCoordinatorInvoker.Verify(i => i.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
            It.IsAny<ActorId>(),
            It.IsAny<string>(),
            It.IsAny<RegisterSessionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never());

        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.False(metadata.HasRegisteredSession);
    }

    [Fact]
    public async Task Activation_WhenAlreadyRegistered_DoesNotRegisterAgain()
    {
        // Simulates a session actor being reactivated after idle timeout, having already
        // registered successfully on a prior activation - registration must not repeat on
        // every activation, only ever once.
        var (mockStateManager, stateData) = CreateMockStateManager();
        stateData["metadata"] = new ActorMetadata { HasRegisteredSession = true };

        var (actor, mockSessionCoordinatorInvoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");

        mockSessionCoordinatorInvoker.Verify(i => i.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
            It.IsAny<ActorId>(),
            It.IsAny<string>(),
            It.IsAny<RegisterSessionRequest>(),
            It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Activation_WhenRegistrationThrows_LeavesHasRegisteredSessionFalseAndDoesNotFailActivation()
    {
        // Best-effort: a failed self-registration must not block activation (and thus not block
        // Enqueue/Dequeue on this session actor) - it just retries on the next activation.
        var (mockStateManager, _) = CreateMockStateManager();

        var (actor, _) = await CreateActorAsync(
            mockStateManager,
            actorId: "orders-session-order-42",
            registerSessionThrows: new InvalidOperationException("session coordinator unreachable"));

        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.False(metadata.HasRegisteredSession);

        // Activation succeeded despite the failure - the actor is otherwise usable.
        var enqueueResult = await actor.Enqueue(new EnqueueRequest
        {
            Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } }
        });
        Assert.True(enqueueResult.Success);
    }

    [Fact]
    public async Task Activation_WhenRegistrationRejected_LeavesHasRegisteredSessionFalse()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(
            mockStateManager,
            actorId: "orders-session-order-42",
            registerSessionResponse: new RegisterSessionResponse { Success = false });

        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.False(metadata.HasRegisteredSession);
    }

    [Fact]
    public async Task SetSessionLease_StoresLeaseIdAndExpiry()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");

        var result = await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-1", ExpiresAt = 12345 });

        Assert.True(result.Success);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal("lease-1", metadata.ActiveSessionLeaseId);
        Assert.Equal(12345, metadata.ActiveSessionLeaseExpiresAt);
    }

    [Fact]
    public async Task SetSessionLease_CalledAgain_OverwritesPreviousLease()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");

        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-1", ExpiresAt = 100 });
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-2", ExpiresAt = 200 });

        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Equal("lease-2", metadata.ActiveSessionLeaseId);
        Assert.Equal(200, metadata.ActiveSessionLeaseExpiresAt);
    }

    [Fact]
    public async Task ClearSessionLease_NullsOutActiveLease()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = "lease-1", ExpiresAt = 12345 });

        var result = await actor.ClearSessionLease();

        Assert.True(result.Success);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Null(metadata.ActiveSessionLeaseId);
        Assert.Null(metadata.ActiveSessionLeaseExpiresAt);
    }

    [Fact]
    public async Task ClearSessionLease_WhenNoLeaseActive_IsANoOpSuccess()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders");

        var result = await actor.ClearSessionLease();

        Assert.True(result.Success);
        var metadata = await mockStateManager.Object.GetStateAsync<ActorMetadata>("metadata");
        Assert.Null(metadata.ActiveSessionLeaseId);
    }

    // --- Session-lease enforcement guard (Dequeue/DequeueLocked/Acknowledge/ExtendLock/DeadLetter) ---
    //
    // The "no active lease" (ordinary, non-session queue actor) case needs no dedicated tests
    // here - every pre-existing test in QueueActorTests.cs/QueueActorRaceConditionTests.cs/
    // QueueActorBlobOffloadTests.cs already exercises it exhaustively and still passes unmodified,
    // which is exactly what proves the guard is a no-op there.

    private static async Task SeedActiveLeaseAsync(QueueActor actor, string leaseId, double expiresAt)
    {
        await actor.SetSessionLease(new SetSessionLeaseRequest { LeaseId = leaseId, ExpiresAt = expiresAt });
    }

    private static double FutureExpiry(int seconds = 30) => DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds();
    private static double PastExpiry(int secondsAgo = 10) => DateTimeOffset.UtcNow.AddSeconds(-secondsAgo).ToUnixTimeSeconds();

    [Fact]
    public async Task Dequeue_WithActiveLease_WrongLeaseId_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());

        var result = await actor.Dequeue(new DequeueRequest { Count = 1, LeaseId = "wrong-lease" });

        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task Dequeue_WithActiveLease_Expired_ReturnsSessionLeaseExpired()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", PastExpiry());

        var result = await actor.Dequeue(new DequeueRequest { Count = 1, LeaseId = "correct-lease" });

        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task Dequeue_WithActiveLease_ValidLeaseId_Succeeds()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await actor.Enqueue(new EnqueueRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } } });
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());

        var result = await actor.Dequeue(new DequeueRequest { Count = 1, LeaseId = "correct-lease" });

        Assert.Null(result.ErrorCode);
        Assert.False(result.IsEmpty);
        Assert.Equal("{\"id\":1}", result.Items[0].ItemJson);
    }

    [Fact]
    public async Task DequeueLocked_WithActiveLease_WrongLeaseId_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());

        var result = await actor.DequeueLocked(new DequeueLockedRequest { LeaseId = "wrong-lease" });

        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task DequeueLocked_WithActiveLease_Expired_ReturnsSessionLeaseExpired()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", PastExpiry());

        var result = await actor.DequeueLocked(new DequeueLockedRequest { LeaseId = "correct-lease" });

        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task DequeueLocked_WithActiveLease_ValidLeaseId_Succeeds()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await actor.Enqueue(new EnqueueRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } } });
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());

        var result = await actor.DequeueLocked(new DequeueLockedRequest { LeaseId = "correct-lease" });

        Assert.Null(result.ErrorCode);
        Assert.False(result.IsEmpty);
        Assert.NotNull(result.Items[0].LockId);
    }

    [Fact]
    public async Task Acknowledge_WithActiveLease_WrongLeaseId_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());

        var result = await actor.Acknowledge(new AcknowledgeRequest { LockId = "nonexistent-lock", LeaseId = "wrong-lease" });

        Assert.False(result.Success);
        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task Acknowledge_WithActiveLease_Expired_ReturnsSessionLeaseExpired()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", PastExpiry());

        var result = await actor.Acknowledge(new AcknowledgeRequest { LockId = "nonexistent-lock", LeaseId = "correct-lease" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task Acknowledge_WithActiveLease_ValidLeaseId_Succeeds()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await actor.Enqueue(new EnqueueRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } } });
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { LeaseId = "correct-lease" });

        var result = await actor.Acknowledge(new AcknowledgeRequest { LockId = dequeueResult.Items[0].LockId, LeaseId = "correct-lease" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExtendLock_WithActiveLease_WrongLeaseId_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());

        var result = await actor.ExtendLock(new ExtendLockRequest { LockId = "nonexistent-lock", LeaseId = "wrong-lease" });

        Assert.False(result.Success);
        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_WithActiveLease_Expired_ReturnsSessionLeaseExpired()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", PastExpiry());

        var result = await actor.ExtendLock(new ExtendLockRequest { LockId = "nonexistent-lock", LeaseId = "correct-lease" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_WithActiveLease_ValidLeaseId_Succeeds()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await actor.Enqueue(new EnqueueRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } } });
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { LeaseId = "correct-lease" });

        var result = await actor.ExtendLock(new ExtendLockRequest { LockId = dequeueResult.Items[0].LockId, LeaseId = "correct-lease", AdditionalTtlSeconds = 30 });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task DeadLetter_WithActiveLease_WrongLeaseId_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());

        var result = await actor.DeadLetter(new DeadLetterRequest { LockId = "nonexistent-lock", LeaseId = "wrong-lease" });

        Assert.Equal("ERROR", result.Status);
        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_WithActiveLease_Expired_ReturnsSessionLeaseExpired()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await SeedActiveLeaseAsync(actor, "correct-lease", PastExpiry());

        var result = await actor.DeadLetter(new DeadLetterRequest { LockId = "nonexistent-lock", LeaseId = "correct-lease" });

        Assert.Equal("ERROR", result.Status);
        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_WithActiveLease_ValidLeaseId_Succeeds()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await actor.Enqueue(new EnqueueRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } } });
        await SeedActiveLeaseAsync(actor, "correct-lease", FutureExpiry());
        var dequeueResult = await actor.DequeueLocked(new DequeueLockedRequest { LeaseId = "correct-lease" });

        var result = await actor.DeadLetter(new DeadLetterRequest { LockId = dequeueResult.Items[0].LockId, LeaseId = "correct-lease" });

        Assert.Equal("SUCCESS", result.Status);
    }
}
