using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class SessionCoordinatorActorTests
{
    private (Mock<IActorStateManager> mock, Dictionary<string, object> stateData) CreateMockStateManager()
    {
        var mock = new Mock<IActorStateManager>();
        var stateData = new Dictionary<string, object>();

        mock.Setup(m => m.TryGetStateAsync<SessionCoordinatorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is SessionCoordinatorMetadata metadata
                    ? new ConditionalValue<SessionCoordinatorMetadata>(true, metadata)
                    : new ConditionalValue<SessionCoordinatorMetadata>(false, null));

        mock.Setup(m => m.TryGetStateAsync<SessionLockState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is SessionLockState lockState
                    ? new ConditionalValue<SessionLockState>(true, lockState)
                    : new ConditionalValue<SessionLockState>(false, null));

        mock.Setup(m => m.GetStateAsync<SessionCoordinatorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is SessionCoordinatorMetadata metadata
                    ? metadata
                    : null!);

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

        return (mock, stateData);
    }

    /// <summary>
    /// Creates and activates a SessionCoordinatorActor with the given actor id (default:
    /// "orders", matching a queue's own QueueActor id). SetSessionLease/ClearSessionLease calls
    /// the mocked IQueueActorInvoker makes resolve via setSessionLeaseSucceeds.
    /// </summary>
    private async Task<(SessionCoordinatorActor actor, Mock<IQueueActorInvoker> mockQueueActorInvoker)> CreateActorAsync(
        Mock<IActorStateManager> mockStateManager,
        string actorId = "orders",
        bool setSessionLeaseSucceeds = true)
    {
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>())).Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>())).Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object,
            ActorId = new ActorId(actorId)
        };

        var mockQueueActorInvoker = new Mock<IQueueActorInvoker>();
        mockQueueActorInvoker.Setup(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<SetSessionLeaseRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetSessionLeaseResponse { Success = setSessionLeaseSucceeds });

        mockQueueActorInvoker.Setup(i => i.InvokeMethodAsync<ClearSessionLeaseResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClearSessionLeaseResponse { Success = true });

        var actorHost = ActorHost.CreateForTest<SessionCoordinatorActor>(testOptions);
        var actor = new SessionCoordinatorActor(actorHost, mockQueueActorInvoker.Object);

        var stateManagerProperty = typeof(Actor).GetProperty("StateManager");
        stateManagerProperty?.SetValue(actor, mockStateManager.Object);

        var onActivateMethod = typeof(SessionCoordinatorActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return (actor, mockQueueActorInvoker);
    }

    private static async Task SeedDirectoryAsync(SessionCoordinatorActor actor, params string[] sessionIds)
    {
        foreach (var id in sessionIds)
        {
            await actor.RegisterSession(new RegisterSessionRequest { SessionId = id });
        }
    }

    // --- RegisterSession ---

    [Fact]
    public async Task RegisterSession_AddsSessionIdToDirectory()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });

        Assert.True(result.Success);
        var metadata = await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");
        Assert.Contains("order-42", metadata.SessionDirectory);
    }

    [Fact]
    public async Task RegisterSession_CalledTwiceWithSameId_DoesNotDuplicate()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });
        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });

        var metadata = await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");
        Assert.Single(metadata.SessionDirectory, "order-42");
    }

    [Fact]
    public async Task RegisterSession_DifferentIds_AddsBoth()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });
        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-43" });

        var metadata = await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");
        Assert.Contains("order-42", metadata.SessionDirectory);
        Assert.Contains("order-43", metadata.SessionDirectory);
        Assert.Equal(2, metadata.SessionDirectory.Count);
    }

    [Fact]
    public async Task RegisterSession_EmptySessionId_ReturnsFailure()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.RegisterSession(new RegisterSessionRequest { SessionId = "" });

        Assert.False(result.Success);
        var metadata = await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");
        Assert.Empty(metadata.SessionDirectory);
    }

    // --- AcceptSession ---

    [Fact]
    public async Task AcceptSession_TargetedMode_UnknownSession_ReturnsSessionNotFound()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_TargetedMode_UnlockedSession_ClaimsSuccessfully_AndSyncsLease()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, mockQueueActorInvoker) = await CreateActorAsync(mockStateManager, actorId: "orders");
        await SeedDirectoryAsync(actor, "order-42");

        var result = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        Assert.True(result.Success);
        Assert.Equal("order-42", result.SessionId);
        Assert.NotNull(result.LeaseId);
        Assert.NotNull(result.LeaseExpiresAt);

        mockQueueActorInvoker.Verify(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
            It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
            "SetSessionLease",
            It.Is<SetSessionLeaseRequest>(r => r.LeaseId == result.LeaseId),
            It.IsAny<CancellationToken>()), Times.Once());

        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.True(stored.HasValue);
        Assert.Equal(result.LeaseId, stored.Value.LeaseId);
    }

    [Fact]
    public async Task AcceptSession_TargetedMode_AlreadyLocked_ReturnsSessionLocked()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        await SeedDirectoryAsync(actor, "order-42");
        await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        var second = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        Assert.False(second.Success);
        Assert.Equal("SESSION_LOCKED", second.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_TargetedMode_ExpiredLock_CanBeReclaimed()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        await SeedDirectoryAsync(actor, "order-42");
        stateData["session-lock_order-42"] = new SessionLockState
        {
            SessionId = "order-42",
            LeaseId = "old-lease",
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds() // expired
        };

        var result = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        Assert.True(result.Success);
        Assert.NotEqual("old-lease", result.LeaseId);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailableMode_NoSessionsInDirectory_ReturnsNoSessionsAvailable()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.AcceptSession(new AcceptSessionRequest());

        Assert.False(result.Success);
        Assert.Equal("NO_SESSIONS_AVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailableMode_AllLocked_ReturnsNoSessionsAvailable()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        await SeedDirectoryAsync(actor, "order-42");
        await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        var result = await actor.AcceptSession(new AcceptSessionRequest());

        Assert.False(result.Success);
        Assert.Equal("NO_SESSIONS_AVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailableMode_PicksFreeSession()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        await SeedDirectoryAsync(actor, "order-42", "order-43");
        await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" }); // lock the first

        var result = await actor.AcceptSession(new AcceptSessionRequest());

        Assert.True(result.Success);
        Assert.Equal("order-43", result.SessionId);
    }

    [Fact]
    public async Task AcceptSession_WhenSyncFails_ReturnsSessionActorUnavailable_AndDoesNotWriteLocalState()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager, setSessionLeaseSucceeds: false);
        await SeedDirectoryAsync(actor, "order-42");

        var result = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_ACTOR_UNAVAILABLE", result.ErrorCode);

        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.False(stored.HasValue);

        // Session remains genuinely unclaimed - a subsequent claim (once the actor is reachable) should work.
        var retry = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });
        Assert.False(retry.Success); // still fails because the mock invoker is still set to fail
        Assert.Equal("SESSION_ACTOR_UNAVAILABLE", retry.ErrorCode);
    }

    // --- RenewSessionLease ---

    [Fact]
    public async Task RenewSessionLease_Success_ExtendsFromCurrentExpiresAt_AndSyncs()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, mockQueueActorInvoker) = await CreateActorAsync(mockStateManager);
        await SeedDirectoryAsync(actor, "order-42");
        var claim = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        var result = await actor.RenewSessionLease(new RenewSessionLeaseRequest
        {
            SessionId = "order-42",
            LeaseId = claim.LeaseId!,
            AdditionalSeconds = 30
        });

        Assert.True(result.Success);
        Assert.Equal(claim.LeaseExpiresAt!.Value + 30, result.NewExpiresAt);

        mockQueueActorInvoker.Verify(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
            It.IsAny<ActorId>(),
            "SetSessionLease",
            It.Is<SetSessionLeaseRequest>(r => r.ExpiresAt == result.NewExpiresAt),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RenewSessionLease_MissingLease_ReturnsSessionLeaseExpired()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = "whatever" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task RenewSessionLease_ExpiredLease_ReturnsSessionLeaseExpired_AndCleansUp()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        stateData["session-lock_order-42"] = new SessionLockState
        {
            SessionId = "order-42",
            LeaseId = "lease-1",
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds()
        };

        var result = await actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = "lease-1" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.False(stored.HasValue);
    }

    [Fact]
    public async Task RenewSessionLease_LeaseIdMismatch_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        await SeedDirectoryAsync(actor, "order-42");
        await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        var result = await actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = "wrong-lease" });

        Assert.False(result.Success);
        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task RenewSessionLease_WhenSyncFails_ReturnsSessionActorUnavailable_DoesNotUpdateLocalState()
    {
        // Claim with a coordinator whose sync succeeds, seeding real lock state in shared storage.
        var (mockStateManager, _) = CreateMockStateManager();
        var (claimingActor, _) = await CreateActorAsync(mockStateManager, setSessionLeaseSucceeds: true);
        await SeedDirectoryAsync(claimingActor, "order-42");
        var claim = await claimingActor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        // A second coordinator instance over the same (shared, mocked) state, whose sync fails -
        // simulates the session actor becoming unreachable between claim and renew.
        var (renewingActor, _) = await CreateActorAsync(mockStateManager, setSessionLeaseSucceeds: false);
        var result = await renewingActor.RenewSessionLease(new RenewSessionLeaseRequest
        {
            SessionId = "order-42",
            LeaseId = claim.LeaseId!,
            AdditionalSeconds = 30
        });

        Assert.False(result.Success);
        Assert.Equal("SESSION_ACTOR_UNAVAILABLE", result.ErrorCode);

        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.True(stored.HasValue);
        Assert.Equal(claim.LeaseExpiresAt!.Value, stored.Value.ExpiresAt); // unchanged
    }

    // --- ReleaseSession ---

    [Fact]
    public async Task ReleaseSession_MissingLease_ReturnsIdempotentSuccess()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = "whatever" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ReleaseSession_LeaseIdMismatch_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        await SeedDirectoryAsync(actor, "order-42");
        await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        var result = await actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = "wrong-lease" });

        Assert.False(result.Success);
        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task ReleaseSession_Success_RemovesLockState_AndBestEffortClearsSessionActor()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, mockQueueActorInvoker) = await CreateActorAsync(mockStateManager, actorId: "orders");
        await SeedDirectoryAsync(actor, "order-42");
        var claim = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        var result = await actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = claim.LeaseId! });

        Assert.True(result.Success);
        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.False(stored.HasValue);

        mockQueueActorInvoker.Verify(i => i.InvokeMethodAsync<ClearSessionLeaseResponse>(
            It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
            "ClearSessionLease",
            It.IsAny<CancellationToken>()), Times.Once());

        // Freed immediately - a new claim should now succeed.
        var reclaim = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });
        Assert.True(reclaim.Success);
    }

    // --- Reminder (lease expiry) ---

    [Fact]
    public async Task ReceiveReminderAsync_SessionPrefix_RemovesLockState_WhenPresent()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);
        stateData["session-lock_order-42"] = new SessionLockState
        {
            SessionId = "order-42",
            LeaseId = "lease-1",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await actor.ReceiveReminderAsync("session-order-42", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.False(stored.HasValue);
    }

    [Fact]
    public async Task ReceiveReminderAsync_SessionPrefix_NoOp_WhenAlreadyReleased()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _) = await CreateActorAsync(mockStateManager);

        // Should not throw even though there's nothing to clean up.
        await actor.ReceiveReminderAsync("session-order-42", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.False(stored.HasValue);
    }
}
