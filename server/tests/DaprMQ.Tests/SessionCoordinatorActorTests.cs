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
    /// the mocked IQueueActorInvoker makes resolve via setSessionLeaseSucceeds. The mocked
    /// IQueueActorStateReader defaults every session to "empty" (IsSessionEmptyAsync returns
    /// true) - sweep-specific tests override individual ActorIds as needed.
    /// </summary>
    private async Task<(SessionCoordinatorActor actor, Mock<IQueueActorInvoker> mockQueueActorInvoker, Mock<IQueueActorStateReader> mockStateReader)> CreateActorAsync(
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

        var mockStateReader = new Mock<IQueueActorStateReader>();
        mockStateReader.Setup(r => r.IsSessionEmptyAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var actorHost = ActorHost.CreateForTest<SessionCoordinatorActor>(testOptions);
        var actor = new SessionCoordinatorActor(actorHost, mockQueueActorInvoker.Object, mockStateReader.Object);

        var stateManagerProperty = typeof(Actor).GetProperty("StateManager");
        stateManagerProperty?.SetValue(actor, mockStateManager.Object);

        var onActivateMethod = typeof(SessionCoordinatorActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return (actor, mockQueueActorInvoker, mockStateReader);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });

        Assert.True(result.Success);
        var metadata = await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");
        Assert.Contains("order-42", metadata.SessionDirectory.Keys);
    }

    [Fact]
    public async Task RegisterSession_CalledTwiceWithSameId_DoesNotDuplicate()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });
        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });

        var metadata = await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");
        Assert.Single(metadata.SessionDirectory.Keys, "order-42");
    }

    [Fact]
    public async Task RegisterSession_DifferentIds_AddsBoth()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });
        await actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-43" });

        var metadata = await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");
        Assert.Contains("order-42", metadata.SessionDirectory.Keys);
        Assert.Contains("order-43", metadata.SessionDirectory.Keys);
        Assert.Equal(2, metadata.SessionDirectory.Count);
    }

    [Fact]
    public async Task RegisterSession_EmptySessionId_ReturnsFailure()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_TargetedMode_UnlockedSession_ClaimsSuccessfully_AndSyncsLease()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, mockQueueActorInvoker, _) = await CreateActorAsync(mockStateManager, actorId: "orders");
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.AcceptSession(new AcceptSessionRequest());

        Assert.False(result.Success);
        Assert.Equal("NO_SESSIONS_AVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailableMode_AllLocked_ReturnsNoSessionsAvailable()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager, setSessionLeaseSucceeds: false);
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
        var (actor, mockQueueActorInvoker, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = "whatever" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task RenewSessionLease_ExpiredLease_ReturnsSessionLeaseExpired_AndCleansUp()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (claimingActor, _, _) = await CreateActorAsync(mockStateManager, setSessionLeaseSucceeds: true);
        await SeedDirectoryAsync(claimingActor, "order-42");
        var claim = await claimingActor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        // A second coordinator instance over the same (shared, mocked) state, whose sync fails -
        // simulates the session actor becoming unreachable between claim and renew.
        var (renewingActor, _, _) = await CreateActorAsync(mockStateManager, setSessionLeaseSucceeds: false);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        var result = await actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = "whatever" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ReleaseSession_LeaseIdMismatch_ReturnsInvalidLeaseId()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, mockQueueActorInvoker, _) = await CreateActorAsync(mockStateManager, actorId: "orders");
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);
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
        var (actor, _, _) = await CreateActorAsync(mockStateManager);

        // Should not throw even though there's nothing to clean up.
        await actor.ReceiveReminderAsync("session-order-42", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var stored = await mockStateManager.Object.TryGetStateAsync<SessionLockState>("session-lock_order-42");
        Assert.False(stored.HasValue);
    }

    // --- Directory sweep ---

    private const string SweepReminderName = "directory-sweep";

    private static async Task<SessionCoordinatorMetadata> GetDirectoryMetadataAsync(Mock<IActorStateManager> mockStateManager) =>
        await mockStateManager.Object.GetStateAsync<SessionCoordinatorMetadata>("metadata");

    [Fact]
    public async Task Sweep_LeasedSession_SurvivesUntouched_ButNextCheckAtAdvances()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _, mockStateReader) = await CreateActorAsync(mockStateManager, actorId: "orders");
        await SeedDirectoryAsync(actor, "order-42");
        stateData["session-lock_order-42"] = new SessionLockState
        {
            SessionId = "order-42",
            LeaseId = "lease-1",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(300).ToUnixTimeSeconds()
        };

        await actor.ReceiveReminderAsync(SweepReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        mockStateReader.Verify(r => r.IsSessionEmptyAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());

        var metadata = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.True(metadata.SessionDirectory.ContainsKey("order-42"));
        Assert.True(metadata.SessionDirectory["order-42"].NextCheckAt > 0);
    }

    [Fact]
    public async Task Sweep_UnleasedEmptySession_FirstTick_ConfirmsButDoesNotEvict()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _, mockStateReader) = await CreateActorAsync(mockStateManager, actorId: "orders");
        await SeedDirectoryAsync(actor, "order-42");
        mockStateReader.Setup(r => r.IsSessionEmptyAsync(
                It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await actor.ReceiveReminderAsync(SweepReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var metadata = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.True(metadata.SessionDirectory.ContainsKey("order-42"));
        Assert.NotNull(metadata.SessionDirectory["order-42"].FirstConfirmedEmptyAt);
    }

    [Fact]
    public async Task Sweep_UnleasedEmptySession_SecondConsecutiveConfirmation_Evicts()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _, mockStateReader) = await CreateActorAsync(mockStateManager, actorId: "orders");
        // Seed as if the first confirmation already happened on an earlier tick.
        stateData["metadata"] = new SessionCoordinatorMetadata
        {
            SessionDirectory = new Dictionary<string, SweepCandidate>
            {
                ["order-42"] = new SweepCandidate
                {
                    FirstConfirmedEmptyAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
                    NextCheckAt = 0
                }
            }
        };
        mockStateReader.Setup(r => r.IsSessionEmptyAsync(
                It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await actor.ReceiveReminderAsync(SweepReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var metadata = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.False(metadata.SessionDirectory.ContainsKey("order-42"));
    }

    [Fact]
    public async Task Sweep_NonemptySession_NeverEvicted_AndBacksOff()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _, mockStateReader) = await CreateActorAsync(mockStateManager, actorId: "orders");
        // Seed as if this candidate already backed off once before (BackoffSeconds = 1 hour).
        stateData["metadata"] = new SessionCoordinatorMetadata
        {
            SessionDirectory = new Dictionary<string, SweepCandidate>
            {
                ["order-42"] = new SweepCandidate { NextCheckAt = 0, BackoffSeconds = 3600 }
            }
        };
        mockStateReader.Setup(r => r.IsSessionEmptyAsync(
                It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await actor.ReceiveReminderAsync(SweepReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var metadata = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.True(metadata.SessionDirectory.ContainsKey("order-42"));
        var candidate = metadata.SessionDirectory["order-42"];
        Assert.Null(candidate.FirstConfirmedEmptyAt);
        Assert.Equal(7200, candidate.BackoffSeconds); // doubled from 3600
        Assert.True(candidate.NextCheckAt > 0);
    }

    [Fact]
    public async Task Sweep_StateReaderThrows_LeavesCandidateUntouched_NoEviction()
    {
        var (mockStateManager, _) = CreateMockStateManager();
        var (actor, _, mockStateReader) = await CreateActorAsync(mockStateManager, actorId: "orders");
        await SeedDirectoryAsync(actor, "order-42");
        mockStateReader.Setup(r => r.IsSessionEmptyAsync(
                It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("state store unavailable"));

        await actor.ReceiveReminderAsync(SweepReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var metadata = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.True(metadata.SessionDirectory.ContainsKey("order-42"));
        Assert.Null(metadata.SessionDirectory["order-42"].FirstConfirmedEmptyAt);
    }

    [Fact]
    public async Task Sweep_ClaimedAndReleasedBetweenConfirmations_SweepBookkeepingUnaffectedByLeaseCalls()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _, mockStateReader) = await CreateActorAsync(mockStateManager, actorId: "orders");
        double firstConfirmedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        stateData["metadata"] = new SessionCoordinatorMetadata
        {
            SessionDirectory = new Dictionary<string, SweepCandidate>
            {
                ["order-42"] = new SweepCandidate { FirstConfirmedEmptyAt = firstConfirmedAt, NextCheckAt = 0 }
            }
        };

        // Claim and release the session in between - exercises real lease code paths, which must
        // not touch the sweep's bookkeeping at all (separate key entirely).
        var claim = await actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });
        Assert.True(claim.Success);
        var beforeSweep = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.Equal(firstConfirmedAt, beforeSweep.SessionDirectory["order-42"].FirstConfirmedEmptyAt);

        await actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = claim.LeaseId! });

        mockStateReader.Setup(r => r.IsSessionEmptyAsync(
                It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await actor.ReceiveReminderAsync(SweepReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var afterSweep = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.False(afterSweep.SessionDirectory.ContainsKey("order-42")); // second confirmation - evicted
    }

    [Fact]
    public async Task Sweep_MoreEligibleCandidatesThanBatchSize_PrioritizesMostOverdue()
    {
        var (mockStateManager, stateData) = CreateMockStateManager();
        var (actor, _, mockStateReader) = await CreateActorAsync(mockStateManager, actorId: "orders");

        // 200 candidates, all already confirmed-empty-once, tied at a less-overdue NextCheckAt.
        var directory = new Dictionary<string, SweepCandidate>();
        for (int i = 0; i < 200; i++)
        {
            directory[$"filler-{i}"] = new SweepCandidate
            {
                FirstConfirmedEmptyAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
                NextCheckAt = 200
            };
        }
        // One more candidate, strictly more overdue (lower NextCheckAt) than all 200 fillers -
        // must be included in the batch even though the pool exceeds the 200 batch cap.
        directory["priority"] = new SweepCandidate
        {
            FirstConfirmedEmptyAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            NextCheckAt = 100
        };
        stateData["metadata"] = new SessionCoordinatorMetadata { SessionDirectory = directory };

        mockStateReader.Setup(r => r.IsSessionEmptyAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await actor.ReceiveReminderAsync(SweepReminderName, Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        var metadata = await GetDirectoryMetadataAsync(mockStateManager);
        Assert.False(metadata.SessionDirectory.ContainsKey("priority")); // evicted - was processed this tick
    }
}
