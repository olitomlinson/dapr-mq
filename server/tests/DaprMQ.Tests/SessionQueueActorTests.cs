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
        Exception? registerSessionThrows = null,
        ICatalogRegistrar? catalogRegistrar = null,
        ISessionActivityReporter? reporter = null,
        SessionDirectoryConfig? sessionDirectoryConfig = null,
        Func<CancellationToken, Task<RegisterSessionResponse>>? registerSession = null)
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
        if (registerSession != null)
        {
            registerSetup.Returns((ActorId _, string _, RegisterSessionRequest _, CancellationToken ct) => registerSession(ct));
        }
        else if (registerSessionThrows != null)
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
        var lockConfig = new LockConfig { MaxDeliveryCount = 10, SweepBatchSize = 200 };

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(
            actorHost,
            mockInvoker.Object,
            mockBlobReaperActorInvoker.Object,
            mockSessionCoordinatorInvoker.Object,
            tokenIssuer,
            blobReapConfig,
            idempotencyConfig,
            lockConfig,
            catalogRegistrar ?? Mock.Of<ICatalogRegistrar>(),
            reporter ?? Mock.Of<ISessionActivityReporter>(),
            sessionDirectoryConfig ?? new SessionDirectoryConfig());

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

    // --- After every method: observe, and register on the first non-empty save ---

    private static Task EnqueueOneAsync(QueueActor actor) =>
        actor.Enqueue(new EnqueueRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } } });

    private static void VerifyRegistered(Mock<ISessionCoordinatorActorInvoker> invoker, Times times) =>
        invoker.Verify(i => i.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
            It.Is<ActorId>(id => id.GetId() == "orders"),
            "RegisterSession",
            It.Is<RegisterSessionRequest>(r => r.SessionId == "order-42"),
            It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Activation_DoesNotRegister()
    {
        // Activation runs before the triggering method, so there is no work yet to register.
        var (mockStateManager, _) = CreateMockStateManager();
        var (_, invoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");

        VerifyRegistered(invoker, Times.Never());
    }

    [Fact]
    public async Task FirstNonEmptySave_RegistersDirectly_AndMarksItReported()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, invoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);

        await EnqueueOneAsync(actor);
        await actor.AfterActorMethodAsync("Enqueue");

        VerifyRegistered(invoker, Times.Once());
        reporter.Verify(r => r.Observe("orders", "order-42", true, false), Times.Once());
        reporter.Verify(r => r.MarkReported("orders", "order-42", true), Times.Once());
    }

    [Fact]
    public async Task LaterNonEmptySaves_OnlyObserve()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, invoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);

        await EnqueueOneAsync(actor);
        await actor.AfterActorMethodAsync("Enqueue");
        await EnqueueOneAsync(actor);
        await actor.AfterActorMethodAsync("Enqueue");

        VerifyRegistered(invoker, Times.Once());
        reporter.Verify(r => r.Observe("orders", "order-42", true, false), Times.Exactly(2));
    }

    [Fact]
    public async Task EmptySave_ObservesEmpty_AndDoesNotRegister()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, invoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);

        await actor.Dequeue(new DequeueRequest { Count = 1 });
        await actor.AfterActorMethodAsync("Dequeue");

        VerifyRegistered(invoker, Times.Never());
        reporter.Verify(r => r.Observe("orders", "order-42", false, false), Times.Once());
    }

    [Fact]
    public async Task Reactivated_RegistersAgainOnItsFirstNonEmptySave()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (first, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await EnqueueOneAsync(first);
        await first.AfterActorMethodAsync("Enqueue");

        // Same persisted state, new activation.
        var (second, invoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await EnqueueOneAsync(second);
        await second.AfterActorMethodAsync("Enqueue");

        VerifyRegistered(invoker, Times.Once());
    }

    [Fact]
    public async Task OnlyLockedItems_StillCountsAsWork()
    {
        // A session abandoned mid-consumption holds its items in locks; it has to stay claimable.
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, invoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);
        await EnqueueOneAsync(actor);
        await actor.DequeueLocked(new DequeueLockedRequest());

        await actor.AfterActorMethodAsync("DequeueLocked");

        VerifyRegistered(invoker, Times.Once());
        reporter.Verify(r => r.Observe("orders", "order-42", true, false), Times.Once());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task ClearSessionLease_RecordsWhatTheCoordinatorNowBelieves(int items, bool hasWork)
    {
        // The coordinator marks the session Idle exactly when nothing is left, so the reporter's
        // view must follow, or a revival before the empty debounce would never be sent.
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);
        for (int i = 0; i < items; i++)
        {
            await EnqueueOneAsync(actor);
        }

        await actor.ClearSessionLease();
        await actor.AfterActorMethodAsync("ClearSessionLease");

        reporter.Verify(r => r.MarkReported("orders", "order-42", hasWork), Times.Once());
    }

    [Fact]
    public async Task ExpiredLeaseSeen_MarksTheStateUnsent_OncePerLease()
    {
        // A lease expiry is decided by the coordinator without calling the session, so the session
        // can't know what the coordinator now believes: the reporter resends its current state.
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);
        await SeedActiveLeaseAsync(actor, "lapsed-lease", PastExpiry());

        await EnqueueOneAsync(actor);
        await actor.AfterActorMethodAsync("Enqueue");
        await EnqueueOneAsync(actor);
        await actor.AfterActorMethodAsync("Enqueue");

        reporter.Verify(r => r.ResetReported("orders", "order-42"), Times.Once());
    }

    [Fact]
    public async Task LiveLease_DoesNotMarkTheStateUnsent()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);
        await SeedActiveLeaseAsync(actor, "live-lease", FutureExpiry());

        await actor.Enqueue(new EnqueueRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 } } });
        await actor.AfterActorMethodAsync("Enqueue");

        reporter.Verify(r => r.ResetReported(It.IsAny<string>(), It.IsAny<string>()), Times.Never());
    }

    [Theory]
    [InlineData("SetSessionLease")]
    [InlineData("ClearSessionLease")]
    public async Task CoordinatorOriginatedMethods_ObserveButNeverCallBackIntoTheCoordinator(string methodName)
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, invoker) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);
        await EnqueueOneAsync(actor);

        await actor.AfterActorMethodAsync(methodName);

        VerifyRegistered(invoker, Times.Never());
        reporter.Verify(r => r.Observe("orders", "order-42", true, false), Times.Once());
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("orders-session-order-42-deadletter")]
    public async Task PlainQueueOrSessionDeadLetter_NeverObservesOrRegisters(string actorId)
    {
        // A session's DLQ id still contains the session marker; registering it would let an
        // any-available claim hand a consumer its own dead-lettered items as a live session.
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, invoker) = await CreateActorAsync(mockStateManager, actorId: actorId, reporter: reporter.Object);
        await EnqueueOneAsync(actor);

        await actor.AfterActorMethodAsync("Enqueue");

        invoker.Verify(i => i.InvokeMethodAsync<RegisterSessionRequest, RegisterSessionResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<RegisterSessionRequest>(), It.IsAny<CancellationToken>()), Times.Never());
        reporter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RegistrationThrows_IsSwallowed_AndLeftToTheReporter()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, _) = await CreateActorAsync(
            mockStateManager,
            actorId: "orders-session-order-42",
            registerSessionThrows: new InvalidOperationException("session coordinator unreachable"),
            reporter: reporter.Object);
        await EnqueueOneAsync(actor);

        await actor.AfterActorMethodAsync("Enqueue");

        reporter.Verify(r => r.Observe("orders", "order-42", true, false), Times.Once());
        reporter.Verify(r => r.MarkReported(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task RegistrationThatHangs_TimesOut_AndIsLeftToTheReporter()
    {
        // A coordinator turn that is itself waiting on this session (SetSessionLease) would never
        // answer: the timeout is what breaks that cycle and frees this session's turn.
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, _) = await CreateActorAsync(
            mockStateManager,
            actorId: "orders-session-order-42",
            reporter: reporter.Object,
            sessionDirectoryConfig: new SessionDirectoryConfig { RegisterSessionTimeout = TimeSpan.FromMilliseconds(100) },
            registerSession: async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new RegisterSessionResponse { Success = true };
            });
        await EnqueueOneAsync(actor);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await actor.AfterActorMethodAsync("Enqueue");

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"hook took {stopwatch.Elapsed}");
        reporter.Verify(r => r.Observe("orders", "order-42", true, false), Times.Once());
        reporter.Verify(r => r.MarkReported(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task RegistrationRejected_IsNotMarkedReported()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, _) = await CreateActorAsync(
            mockStateManager,
            actorId: "orders-session-order-42",
            registerSessionResponse: new RegisterSessionResponse { Success = false },
            reporter: reporter.Object);
        await EnqueueOneAsync(actor);

        await actor.AfterActorMethodAsync("Enqueue");

        reporter.Verify(r => r.MarkReported(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
    }

    [Fact]
    public async Task Deactivation_ObservesTheFinalStateAsDeactivating()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var reporter = new Mock<ISessionActivityReporter>();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42", reporter: reporter.Object);

        await (Task)typeof(QueueActor)
            .GetMethod("OnDeactivateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(actor, null)!;

        reporter.Verify(r => r.Observe("orders", "order-42", false, true), Times.Once());
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
    public async Task ClearSessionLease_ReportsQueuedPlusLockedItemsRemaining()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, actorId: "orders-session-order-42");
        await EnqueueOneAsync(actor);
        await EnqueueOneAsync(actor);
        await actor.DequeueLocked(new DequeueLockedRequest());

        var result = await actor.ClearSessionLease();

        Assert.Equal(2, result.Remaining);
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

    [Theory]
    [InlineData("orders-session-order-42")]
    [InlineData("orders-session-order-42-deadletter")]
    public async Task OnActivate_SessionActor_NotRegisteredInTheCatalog(string actorId)
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var registrar = new Mock<ICatalogRegistrar>();

        await CreateActorAsync(mockStateManager, actorId: actorId, catalogRegistrar: registrar.Object);

        registrar.Verify(r => r.Register(It.IsAny<CatalogEntry>()), Times.Never);
    }
}
