using System.Diagnostics;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Time.Testing;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class SessionCoordinatorActorTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private sealed class Harness
    {
        public required SessionCoordinatorActor Actor { get; init; }
        public required Mock<IActorStateManager> State { get; init; }
        public required Dictionary<string, object> Data { get; init; }
        public required Mock<IQueueActorInvoker> Invoker { get; init; }
        public required Mock<IQueueActorStateReader> Reader { get; init; }
        public List<string> RegisteredReminders { get; } = [];
        public List<string> UnregisteredReminders { get; } = [];
        public Dictionary<string, TimeSpan> ReminderDueTimes { get; } = [];

        public SessionCoordinatorMetadata Metadata => (SessionCoordinatorMetadata)Data["metadata"];
        public DirectorySummary Summary => (DirectorySummary)Data["dir_summary"];
        public SessionEntry Entry(string sessionId) => Metadata.SessionDirectory[sessionId];
    }

    private static (Mock<IActorStateManager> mock, Dictionary<string, object> stateData) CreateMockStateManager()
    {
        var mock = new Mock<IActorStateManager>();
        var stateData = new Dictionary<string, object>();

        SetupTryGet<SessionCoordinatorMetadata>(mock, stateData);
        SetupTryGet<DirectorySummary>(mock, stateData);
        SetupTryGet<SessionLockState>(mock, stateData);

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

    private static void SetupTryGet<T>(Mock<IActorStateManager> mock, Dictionary<string, object> stateData) where T : class =>
        mock.Setup(m => m.TryGetStateAsync<T>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is T value
                    ? new ConditionalValue<T>(true, value)
                    : new ConditionalValue<T>(false, null!));

    /// <summary>
    /// Creates and activates a SessionCoordinatorActor (default id "orders"). SetSessionLease
    /// succeeds and ClearSessionLease reports nothing left unless overridden; every session reads
    /// as empty unless a test sets up its reader. Pass an existing state manager to simulate the
    /// coordinator reactivating over the same state.
    /// </summary>
    private async Task<Harness> CreateAsync(
        (Mock<IActorStateManager> mock, Dictionary<string, object> data)? state = null,
        SessionDirectoryConfig? config = null,
        ICatalogRegistrar? catalogRegistrar = null)
    {
        var (mockStateManager, stateData) = state ?? CreateMockStateManager();
        var registered = new List<string>();
        var unregistered = new List<string>();

        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>())).Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => registered.Add(r.Name))
            .Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>()))
            .Callback<ActorReminderToken>(t => unregistered.Add(t.Name))
            .Returns(Task.CompletedTask);

        var mockQueueActorInvoker = new Mock<IQueueActorInvoker>();
        mockQueueActorInvoker.Setup(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SetSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetSessionLeaseResponse { Success = true });
        mockQueueActorInvoker.Setup(i => i.InvokeMethodAsync<ClearSessionLeaseResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClearSessionLeaseResponse { Success = true, Remaining = 0 });

        var mockStateReader = new Mock<IQueueActorStateReader>();
        mockStateReader.Setup(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionActorState(0, 0, null));

        var actorHost = ActorHost.CreateForTest<SessionCoordinatorActor>(new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object,
            ActorId = new ActorId("orders")
        });
        var actor = new SessionCoordinatorActor(actorHost, mockQueueActorInvoker.Object, mockStateReader.Object,
            catalogRegistrar ?? Mock.Of<ICatalogRegistrar>(), config ?? new SessionDirectoryConfig(), _time);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);
        await (Task)typeof(SessionCoordinatorActor)
            .GetMethod("OnActivateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(actor, null)!;

        var harness = new Harness
        {
            Actor = actor,
            State = mockStateManager,
            Data = stateData,
            Invoker = mockQueueActorInvoker,
            Reader = mockStateReader
        };
        harness.RegisteredReminders.AddRange(registered);
        registered.Clear();
        mockTimerManager.Invocations.Clear();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r =>
            {
                harness.RegisteredReminders.Add(r.Name);
                harness.ReminderDueTimes[r.Name] = r.DueTime;
            })
            .Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>()))
            .Callback<ActorReminderToken>(t => harness.UnregisteredReminders.Add(t.Name))
            .Returns(Task.CompletedTask);
        return harness;
    }

    private static async Task RegisterAsync(Harness h, params string[] sessionIds)
    {
        foreach (var id in sessionIds)
        {
            await h.Actor.RegisterSession(new RegisterSessionRequest { SessionId = id });
        }
    }

    private static Task<ReportSessionChangesResponse> ReportAsync(Harness h, string sessionId, bool hasWork) =>
        h.Actor.ReportSessionChanges(new ReportSessionChangesRequest
        {
            Changes = [new SessionChange { SessionId = sessionId, HasWork = hasWork }]
        });

    private static Task PassAsync(Harness h) =>
        h.Actor.ReceiveReminderAsync(SessionCoordinatorActor.EvictionReminderName, [], TimeSpan.Zero, TimeSpan.Zero);

    private static void SessionReads(Harness h, string sessionId, int items, int locks = 0) =>
        h.Reader.Setup(r => r.ReadSessionStateAsync(It.Is<ActorId>(id => id.GetId() == $"orders-session-{sessionId}"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionActorState(items, locks, null));

    private void PlantLease(Harness h, string sessionId, double secondsFromNow) =>
        h.Data[$"session-lock_{sessionId}"] = new SessionLockState
        {
            SessionId = sessionId,
            LeaseId = "planted",
            CreatedAt = _time.GetUtcNow().AddSeconds(-60).ToUnixTimeSeconds(),
            ExpiresAt = _time.GetUtcNow().AddSeconds(secondsFromNow).ToUnixTimeSeconds()
        };

    private void PastDeferralWindow() => _time.Advance(TimeSpan.FromSeconds(31));

    // Past the longest lease a claim can take, so a lease-expiry reminder finds its lease lapsed.
    private void PastLease() => _time.Advance(TimeSpan.FromSeconds(301));

    /// <summary>Registers then reports the session empty after the deferral window: an Idle entry.</summary>
    private async Task MakeIdleAsync(Harness h, string sessionId)
    {
        await RegisterAsync(h, sessionId);
        PastDeferralWindow();
        await ReportAsync(h, sessionId, hasWork: false);
    }

    // --- Activation ---

    [Fact]
    public async Task OnActivate_RegistersTheQueueInTheCatalog()
    {
        var registrar = new Mock<ICatalogRegistrar>();

        await CreateAsync(catalogRegistrar: registrar.Object);

        registrar.Verify(r => r.Register(It.Is<CatalogEntry>(e => e.Id == "orders" && e.Kind == CatalogEntryKind.Queue)), Times.Once);
    }

    [Fact]
    public async Task OnActivate_EmptyDirectory_RegistersNoEvictionReminder()
    {
        var h = await CreateAsync();

        Assert.Empty(h.RegisteredReminders);
        Assert.Equal(new DirectorySummary(), h.Summary);
    }

    // --- RegisterSession ---

    [Fact]
    public async Task RegisterSession_UnknownSession_AddsItActiveAndQueued()
    {
        var h = await CreateAsync();

        var result = await h.Actor.RegisterSession(new RegisterSessionRequest { SessionId = "order-42" });

        Assert.True(result.Success);
        Assert.True(h.Entry("order-42").Active);
        Assert.True(h.Entry("order-42").Queued);
        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
        Assert.Equal(new DirectorySummary { TotalCount = 1, ActiveCount = 1 }, h.Summary);
    }

    [Fact]
    public async Task RegisterSession_CalledTwice_DoesNotDuplicateOrWriteAgain()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        h.State.Invocations.Clear();

        await RegisterAsync(h, "order-42");

        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
        Assert.Equal(new DirectorySummary { TotalCount = 1, ActiveCount = 1 }, h.Summary);
        h.State.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task RegisterSession_EmptySessionId_ReturnsFailure()
    {
        var h = await CreateAsync();

        var result = await h.Actor.RegisterSession(new RegisterSessionRequest { SessionId = "" });

        Assert.False(result.Success);
        Assert.Empty(h.Metadata.SessionDirectory);
    }

    [Fact]
    public async Task RegisterSession_FirstEntry_RegistersEvictionReminderOnce()
    {
        var h = await CreateAsync();

        await RegisterAsync(h, "order-42");
        await RegisterAsync(h, "order-43");

        Assert.Equal([SessionCoordinatorActor.EvictionReminderName], h.RegisteredReminders);
    }

    [Fact]
    public async Task RegisterSession_LeasedIdleSession_BecomesActiveButNotQueued()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        PlantLease(h, "order-42", secondsFromNow: 60);
        var withoutStale = h.Metadata with { ReadyQueue = [] };
        h.Data["metadata"] = withoutStale with
        {
            SessionDirectory = new(withoutStale.SessionDirectory) { ["order-42"] = h.Entry("order-42") with { Queued = false } }
        };

        await RegisterAsync(h, "order-42");

        Assert.True(h.Entry("order-42").Active);
        Assert.False(h.Entry("order-42").Queued);
        Assert.Empty(h.Metadata.ReadyQueue);
    }

    // --- ReportSessionChanges ---

    [Fact]
    public async Task Report_EmptyAfterDeferralWindow_MarksIdle()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        PastDeferralWindow();

        var response = await ReportAsync(h, "order-42", hasWork: false);

        Assert.Empty(response.Deferred);
        Assert.False(h.Entry("order-42").Active);
        Assert.NotNull(h.Entry("order-42").IdleSince);
        Assert.Equal(new DirectorySummary { TotalCount = 1, ActiveCount = 0 }, h.Summary);
    }

    [Fact]
    public async Task Report_EmptyWithinDeferralWindow_IsDeferredAndNothingWritten()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        _time.Advance(TimeSpan.FromSeconds(5));
        h.State.Invocations.Clear();

        var response = await ReportAsync(h, "order-42", hasWork: false);

        Assert.Equal(["order-42"], response.Deferred);
        Assert.True(h.Entry("order-42").Active);
        h.State.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Report_EmptyForUnknownOrIdleSession_IsIgnored()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        h.State.Invocations.Clear();

        var response = await h.Actor.ReportSessionChanges(new ReportSessionChangesRequest
        {
            Changes = [new SessionChange { SessionId = "order-42" }, new SessionChange { SessionId = "order-99" }]
        });

        Assert.Empty(response.Deferred);
        Assert.False(h.Metadata.SessionDirectory.ContainsKey("order-99"));
        h.State.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Report_ActiveForIdleSession_ReactivatesAndRequeuesAtTheTail()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest()); // drops the stale id order-42 left behind
        await RegisterAsync(h, "order-43");

        await ReportAsync(h, "order-42", hasWork: true);

        Assert.True(h.Entry("order-42").Active);
        Assert.Equal(["order-43", "order-42"], h.Metadata.ReadyQueue);
        Assert.Equal(2, h.Summary.ActiveCount);
    }

    [Fact]
    public async Task Report_ActiveForUnknownSession_AddsIt()
    {
        var h = await CreateAsync();

        await ReportAsync(h, "order-42", hasWork: true);

        Assert.True(h.Entry("order-42").Active);
        Assert.Equal(new DirectorySummary { TotalCount = 1, ActiveCount = 1 }, h.Summary);
    }

    [Fact]
    public async Task Report_ActiveRefreshesTheDeferralWindow()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        PastDeferralWindow();
        await ReportAsync(h, "order-42", hasWork: true);
        _time.Advance(TimeSpan.FromSeconds(5));

        var response = await ReportAsync(h, "order-42", hasWork: false);

        Assert.Equal(["order-42"], response.Deferred);
    }

    [Fact]
    public async Task AfterReactivation_EmptyIsDeferredForOneWindow()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        PastDeferralWindow();

        var reactivated = await CreateAsync(state: (h.State, h.Data));
        var deferred = await ReportAsync(reactivated, "order-42", hasWork: false);
        PastDeferralWindow();
        var applied = await ReportAsync(reactivated, "order-42", hasWork: false);

        Assert.Equal(["order-42"], deferred.Deferred);
        Assert.Empty(applied.Deferred);
        Assert.False(reactivated.Entry("order-42").Active);
    }

    // --- AcceptSession: targeted ---

    [Fact]
    public async Task AcceptSession_Targeted_UnknownSession_ReturnsSessionNotFound()
    {
        var h = await CreateAsync();

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_Targeted_ClaimsAndSyncsLease()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        Assert.True(result.Success);
        Assert.Equal("order-42", result.SessionId);
        h.Invoker.Verify(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
            It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"),
            "SetSessionLease",
            It.Is<SetSessionLeaseRequest>(r => r.LeaseId == result.LeaseId),
            It.IsAny<CancellationToken>()), Times.Once());
        Assert.Equal(result.LeaseId, ((SessionLockState)h.Data["session-lock_order-42"]).LeaseId);
    }

    [Fact]
    public async Task AcceptSession_Targeted_IdleSession_CanStillBeClaimed()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task AcceptSession_Targeted_AlreadyLeased_ReturnsSessionLocked()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        var second = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        Assert.False(second.Success);
        Assert.Equal("SESSION_LOCKED", second.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_Targeted_ExpiredLease_CanBeReclaimed()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        PlantLease(h, "order-42", secondsFromNow: -10);

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        Assert.True(result.Success);
        Assert.NotEqual("planted", result.LeaseId);
    }

    [Fact]
    public async Task AcceptSession_Targeted_WhenSyncFails_ReturnsSessionActorUnavailable_AndWritesNoLease()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        h.Invoker.Setup(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SetSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetSessionLeaseResponse { Success = false });

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        Assert.False(result.Success);
        Assert.Equal("SESSION_ACTOR_UNAVAILABLE", result.ErrorCode);
        Assert.False(h.Data.ContainsKey("session-lock_order-42"));
    }

    // --- AcceptSession: any available ---

    [Fact]
    public async Task AcceptSession_AnyAvailable_EmptyDirectory_ReturnsNoSessionsAvailable()
    {
        var h = await CreateAsync();

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest());

        Assert.False(result.Success);
        Assert.Equal("NO_SESSIONS_AVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_ClaimsInFifoOrder_AndPopsTheQueue()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await RegisterAsync(h, "order-43");
        await RegisterAsync(h, "order-44");

        var claimed = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            claimed.Add((await h.Actor.AcceptSession(new AcceptSessionRequest())).SessionId!);
        }

        Assert.Equal(["order-42", "order-43", "order-44"], claimed);
        Assert.Empty(h.Metadata.ReadyQueue);
        Assert.All(h.Metadata.SessionDirectory.Values, e => Assert.False(e.Queued));
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_NeverReturnsAnIdleSession()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        await MakeIdleAsync(h, "order-43");

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest());

        Assert.Equal("NO_SESSIONS_AVAILABLE", result.ErrorCode);
        Assert.Empty(h.Metadata.ReadyQueue);
        Assert.False(h.Entry("order-42").Queued);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_SkipsStaleIdsToTheNextSessionWithWork()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42"); // stays in the queue as a stale id
        await RegisterAsync(h, "order-43");

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest());

        Assert.Equal("order-43", result.SessionId);
        Assert.Empty(h.Metadata.ReadyQueue);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_SkipsASessionLeasedByATargetedClaim()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await RegisterAsync(h, "order-43");
        await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest());

        Assert.Equal("order-43", result.SessionId);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_UnreachableSession_MovesToTheTail()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await RegisterAsync(h, "order-43");
        h.Invoker.Setup(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"), It.IsAny<string>(), It.IsAny<SetSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetSessionLeaseResponse { Success = false });

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest());

        Assert.Equal("order-43", result.SessionId);
        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
        Assert.True(h.Entry("order-42").Queued);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_OnlySessionUnreachable_ReturnsUnavailableAfterOneTry()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        h.Invoker.Setup(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SetSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetSessionLeaseResponse { Success = false });

        var result = await h.Actor.AcceptSession(new AcceptSessionRequest());

        Assert.Equal("SESSION_ACTOR_UNAVAILABLE", result.ErrorCode);
        h.Invoker.Verify(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SetSessionLeaseRequest>(), It.IsAny<CancellationToken>()), Times.Once());
        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
    }

    // --- RenewSessionLease ---

    [Fact]
    public async Task RenewSessionLease_Success_ExtendsFromCurrentExpiresAt_AndSyncs()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        var claim = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });

        var result = await h.Actor.RenewSessionLease(new RenewSessionLeaseRequest
        {
            SessionId = "order-42",
            LeaseId = claim.LeaseId!,
            AdditionalSeconds = 30
        });

        Assert.True(result.Success);
        Assert.Equal(claim.LeaseExpiresAt!.Value + 30, result.NewExpiresAt);
        h.Invoker.Verify(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
            It.IsAny<ActorId>(), "SetSessionLease", It.Is<SetSessionLeaseRequest>(r => r.ExpiresAt == result.NewExpiresAt), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task RenewSessionLease_Success_LeavesTheLeaseReminderAlone()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        var claim = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });
        h.RegisteredReminders.Clear();

        await h.Actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = claim.LeaseId!, AdditionalSeconds = 30 });

        Assert.DoesNotContain("session-order-42", h.RegisteredReminders);
        Assert.DoesNotContain("session-order-42", h.UnregisteredReminders);
    }

    [Fact]
    public async Task RenewSessionLease_MissingLease_ReturnsSessionLeaseExpired()
    {
        var h = await CreateAsync();

        var result = await h.Actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = "whatever" });

        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task RenewSessionLease_ExpiredLease_ReturnsSessionLeaseExpired_AndCleansUp()
    {
        var h = await CreateAsync();
        PlantLease(h, "order-42", secondsFromNow: -10);

        var result = await h.Actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = "planted" });

        Assert.Equal("SESSION_LEASE_EXPIRED", result.ErrorCode);
        Assert.False(h.Data.ContainsKey("session-lock_order-42"));
    }

    [Fact]
    public async Task RenewSessionLease_LeaseIdMismatch_ReturnsInvalidLeaseId()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        var result = await h.Actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = "wrong-lease" });

        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task RenewSessionLease_WhenSyncFails_ReturnsSessionActorUnavailable_AndKeepsTheOldExpiry()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        var claim = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });
        h.Invoker.Setup(i => i.InvokeMethodAsync<SetSessionLeaseRequest, SetSessionLeaseResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SetSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetSessionLeaseResponse { Success = false });

        var result = await h.Actor.RenewSessionLease(new RenewSessionLeaseRequest
        {
            SessionId = "order-42",
            LeaseId = claim.LeaseId!,
            AdditionalSeconds = 30
        });

        Assert.Equal("SESSION_ACTOR_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(claim.LeaseExpiresAt!.Value, ((SessionLockState)h.Data["session-lock_order-42"]).ExpiresAt);
    }

    // --- ReleaseSession (lease end) ---

    [Fact]
    public async Task ReleaseSession_MissingLease_ReturnsIdempotentSuccess()
    {
        var h = await CreateAsync();

        var result = await h.Actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = "whatever" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ReleaseSession_LeaseIdMismatch_ReturnsInvalidLeaseId()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42" });

        var result = await h.Actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = "wrong-lease" });

        Assert.Equal("INVALID_LEASE_ID", result.ErrorCode);
    }

    [Fact]
    public async Task ReleaseSession_NothingLeft_MarksIdle_AndDoesNotRequeue()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        var claim = await h.Actor.AcceptSession(new AcceptSessionRequest());

        var result = await h.Actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = claim.LeaseId! });

        Assert.True(result.Success);
        Assert.False(h.Data.ContainsKey("session-lock_order-42"));
        h.Invoker.Verify(i => i.InvokeMethodAsync<ClearSessionLeaseResponse>(
            It.Is<ActorId>(id => id.GetId() == "orders-session-order-42"), "ClearSessionLease", It.IsAny<CancellationToken>()), Times.Once());
        Assert.False(h.Entry("order-42").Active);
        Assert.Empty(h.Metadata.ReadyQueue);
        Assert.Equal(0, h.Summary.ActiveCount);
    }

    [Fact]
    public async Task ReleaseSession_ItemsLeft_RequeuesAtTheTail_AndIsClaimableAgain()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await RegisterAsync(h, "order-43");
        var claim = await h.Actor.AcceptSession(new AcceptSessionRequest());
        h.Invoker.Setup(i => i.InvokeMethodAsync<ClearSessionLeaseResponse>(It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClearSessionLeaseResponse { Success = true, Remaining = 3 });

        await h.Actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = claim.LeaseId! });

        Assert.Equal(["order-43", "order-42"], h.Metadata.ReadyQueue);
        Assert.True(h.Entry("order-42").Active);
        Assert.Equal(2, h.Summary.ActiveCount);
    }

    [Fact]
    public async Task ReleaseSession_ClearSessionLeaseFails_TreatsTheSessionAsHoldingItems()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        var claim = await h.Actor.AcceptSession(new AcceptSessionRequest());
        h.Invoker.Setup(i => i.InvokeMethodAsync<ClearSessionLeaseResponse>(It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());

        var result = await h.Actor.ReleaseSession(new ReleaseSessionRequest { SessionId = "order-42", LeaseId = claim.LeaseId! });

        Assert.True(result.Success);
        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
        Assert.True(h.Entry("order-42").Active);
    }

    // --- Lease-expiry reminder (lease end) ---

    [Fact]
    public async Task LeaseExpiry_ItemsLeft_RemovesTheLease_AndRequeues()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest());
        SessionReads(h, "order-42", items: 0, locks: 2);

        PastLease();
        await h.Actor.ReceiveReminderAsync("session-order-42", [], TimeSpan.Zero, TimeSpan.Zero);

        Assert.False(h.Data.ContainsKey("session-lock_order-42"));
        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
    }

    [Fact]
    public async Task LeaseExpiry_NothingLeft_MarksIdle()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest());

        PastLease();
        await h.Actor.ReceiveReminderAsync("session-order-42", [], TimeSpan.Zero, TimeSpan.Zero);

        Assert.False(h.Entry("order-42").Active);
        Assert.Empty(h.Metadata.ReadyQueue);
    }

    [Fact]
    public async Task LeaseExpiry_ReadFails_TreatsTheSessionAsHoldingItems()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest());
        h.Reader.Setup(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("sidecar down"));

        PastLease();
        await h.Actor.ReceiveReminderAsync("session-order-42", [], TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
    }

    [Fact]
    public async Task LeaseExpiry_LeaseRenewedSinceTheReminderWasSet_RearmsForTheTimeLeft_AndKeepsTheLease()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        var claim = await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 30 });
        await h.Actor.RenewSessionLease(new RenewSessionLeaseRequest { SessionId = "order-42", LeaseId = claim.LeaseId!, AdditionalSeconds = 30 });
        _time.Advance(TimeSpan.FromSeconds(30)); // the claim's reminder fires at its original expiry
        h.RegisteredReminders.Clear();

        await h.Actor.ReceiveReminderAsync("session-order-42", [], TimeSpan.Zero, TimeSpan.Zero);

        Assert.True(h.Data.ContainsKey("session-lock_order-42"));
        Assert.Equal(["session-order-42"], h.RegisteredReminders);
        Assert.Equal(TimeSpan.FromSeconds(30), h.ReminderDueTimes["session-order-42"]);
        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task LeaseExpiry_AlreadyReleased_IsANoOp()
    {
        var h = await CreateAsync();

        await h.Actor.ReceiveReminderAsync("session-order-42", [], TimeSpan.Zero, TimeSpan.Zero);

        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // --- Eviction pass ---

    [Fact]
    public async Task Pass_IdleEntryPastGrace_FinalReadEmpty_Evicts()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(11));

        await PassAsync(h);

        Assert.False(h.Metadata.SessionDirectory.ContainsKey("order-42"));
        Assert.DoesNotContain("order-42", h.Metadata.ReadyQueue);
        Assert.Equal(new DirectorySummary(), h.Summary);
        Assert.Contains(SessionCoordinatorActor.EvictionReminderName, h.UnregisteredReminders);
    }

    [Fact]
    public async Task Pass_IdleEntryWithinGrace_IsNotRead()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(5));

        await PassAsync(h);

        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());
        Assert.True(h.Metadata.SessionDirectory.ContainsKey("order-42"));
    }

    [Fact]
    public async Task Pass_IdleEntryPastGrace_HasItems_PromotesAndQueues()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        h.Data["metadata"] = h.Metadata with
        {
            ReadyQueue = [],
            SessionDirectory = new(h.Metadata.SessionDirectory) { ["order-42"] = h.Entry("order-42") with { Queued = false } }
        };
        _time.Advance(TimeSpan.FromMinutes(11));
        SessionReads(h, "order-42", items: 4);

        await PassAsync(h);

        Assert.True(h.Entry("order-42").Active);
        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
        Assert.Equal(1, h.Summary.ActiveCount);
    }

    [Fact]
    public async Task Pass_LeasedIdleEntry_IsNotRead_AndCheckedAgainLater()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(11));
        PlantLease(h, "order-42", secondsFromNow: 60);

        await PassAsync(h);

        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());
        Assert.True(h.Entry("order-42").NextCheckAt > _time.GetUtcNow().ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Pass_ActiveEntryBeforeBackstop_IsNotRead()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(30));

        await PassAsync(h);

        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Pass_ActiveUnleasedPastBackstop_Empty_Demotes()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(61));

        await PassAsync(h);

        Assert.False(h.Entry("order-42").Active);
        Assert.Equal(0, h.Summary.ActiveCount);
    }

    [Fact]
    public async Task Pass_ActiveUnleasedPastBackstop_HasItems_BacksOffAndStaysActive()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        SessionReads(h, "order-42", items: 2);
        _time.Advance(TimeSpan.FromMinutes(61));

        await PassAsync(h);
        _time.Advance(TimeSpan.FromMinutes(61));
        await PassAsync(h);

        Assert.True(h.Entry("order-42").Active);
        Assert.Equal(TimeSpan.FromHours(2).TotalSeconds, h.Entry("order-42").BackoffSeconds);
        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Pass_ActiveLeasedPastBackstop_IsNotRead()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest { SessionId = "order-42", LeaseSeconds = 300 });
        _time.Advance(TimeSpan.FromMinutes(61));
        PlantLease(h, "order-42", secondsFromNow: 60);

        await PassAsync(h);

        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Pass_ActiveSessionWhoseLeaseLapsedUnnoticed_IsRequeuedWithoutARead()
    {
        var h = await CreateAsync();
        await RegisterAsync(h, "order-42");
        await h.Actor.AcceptSession(new AcceptSessionRequest()); // popped, leased
        PlantLease(h, "order-42", secondsFromNow: -5);              // lapsed, expiry reminder never fired

        await PassAsync(h);

        Assert.Equal(["order-42"], h.Metadata.ReadyQueue);
        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Pass_ReadFails_LeavesTheEntryUnchanged()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(11));
        h.Reader.Setup(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("inconclusive"));
        var before = h.Entry("order-42");

        await PassAsync(h);

        Assert.Equal(before, h.Entry("order-42"));
    }

    [Fact]
    public async Task Pass_MoreDueThanTheReadBudget_ReadsTheBudget_AndSchedulesACatchUp()
    {
        var h = await CreateAsync(config: new SessionDirectoryConfig { MaxReadsPerPass = 2 });
        await MakeIdleAsync(h, "order-41");
        await MakeIdleAsync(h, "order-42");
        await MakeIdleAsync(h, "order-43");
        _time.Advance(TimeSpan.FromMinutes(11));

        await PassAsync(h);

        h.Reader.Verify(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Single(h.Metadata.SessionDirectory);
        Assert.Contains(SessionCoordinatorActor.CatchUpReminderName, h.RegisteredReminders);
    }

    [Fact]
    public async Task Pass_HungReads_ReturnWithinTheTimeCap_ApplyNothing_AndScheduleACatchUp()
    {
        var h = await CreateAsync(config: new SessionDirectoryConfig { MaxPassDuration = TimeSpan.FromMilliseconds(100) });
        await MakeIdleAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(11));
        h.Reader.Setup(r => r.ReadSessionStateAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()))
            .Returns(async (ActorId _, CancellationToken ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new SessionActorState(0, 0, null);
            });
        var before = h.Entry("order-42");

        var stopwatch = Stopwatch.StartNew();
        await PassAsync(h);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"pass took {stopwatch.Elapsed}");
        Assert.Equal(before, h.Entry("order-42"));
        Assert.Contains(SessionCoordinatorActor.CatchUpReminderName, h.RegisteredReminders);
    }

    [Fact]
    public async Task Pass_CatchUpReminder_RunsAPass()
    {
        var h = await CreateAsync();
        await MakeIdleAsync(h, "order-42");
        _time.Advance(TimeSpan.FromMinutes(11));

        await h.Actor.ReceiveReminderAsync(SessionCoordinatorActor.CatchUpReminderName, [], TimeSpan.Zero, TimeSpan.Zero);

        Assert.Empty(h.Metadata.SessionDirectory);
    }

    [Fact]
    public async Task Pass_EmptyDirectory_UnregistersTheReminder()
    {
        var h = await CreateAsync();

        await PassAsync(h);

        Assert.Contains(SessionCoordinatorActor.EvictionReminderName, h.UnregisteredReminders);
    }
}
