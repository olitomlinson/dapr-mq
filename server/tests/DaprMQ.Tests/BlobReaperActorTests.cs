using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class BlobReaperActorTests
{
    private Mock<IActorStateManager> CreateMockStateManager(Dictionary<string, object> stateData)
    {
        var mock = new Mock<IActorStateManager>();

        mock.Setup(m => m.TryGetStateAsync<BlobReaperActorState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is BlobReaperActorState state)
                {
                    return new ConditionalValue<BlobReaperActorState>(true, state);
                }
                return new ConditionalValue<BlobReaperActorState>(false, null);
            });

        mock.Setup(m => m.TryGetStateAsync<int>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is int count)
                {
                    return new ConditionalValue<int>(true, count);
                }
                return new ConditionalValue<int>(false, 0);
            });

        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken ct) =>
            {
                stateData[key] = value;
                return Task.CompletedTask;
            });

        mock.Setup(m => m.TryRemoveStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken ct) =>
            {
                stateData.Remove(key);
                return Task.FromResult(true);
            });

        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private BlobReaperActor CreateActor(Mock<IActorStateManager> mockStateManager, Mock<IObjectStore> mockObjectStore, Mock<ActorTimerManager>? mockTimerManager = null)
    {
        if (mockTimerManager == null)
        {
            mockTimerManager = new Mock<ActorTimerManager>();
            mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
                .Returns(Task.CompletedTask);
            mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>()))
                .Returns(Task.CompletedTask);
        }

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<BlobReaperActor>(testOptions);
        var actor = new BlobReaperActor(actorHost, mockObjectStore.Object);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        return actor;
    }

    [Fact]
    public async Task ScheduleDeletion_PersistsStateAndRegistersReminder()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockObjectStore = new Mock<IObjectStore>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        List<(string name, TimeSpan dueTime, TimeSpan period)> reminderRegistrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => reminderRegistrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);

        var actor = CreateActor(mockStateManager, mockObjectStore, mockTimerManager);

        await actor.ScheduleDeletion(new ScheduleDeletionRequest { BlobReference = "blob-1", DelaySeconds = 30 });

        Assert.Single(reminderRegistrations);
        Assert.Equal("reap", reminderRegistrations[0].name);
        Assert.Equal(TimeSpan.FromSeconds(30), reminderRegistrations[0].dueTime);

        var state = (BlobReaperActorState)stateData["reap-state"];
        Assert.Equal("blob-1", state.BlobReference);
    }

    [Fact]
    public async Task ReceiveReminder_DeletesBlobAndUnregistersReminder_OnSuccess()
    {
        var stateData = new Dictionary<string, object>
        {
            ["reap-state"] = new BlobReaperActorState { BlobReference = "blob-1" }
        };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockObjectStore = new Mock<IObjectStore>();
        mockObjectStore.Setup(o => o.DeleteAsync("blob-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockTimerManager = new Mock<ActorTimerManager>();
        List<string> unregistered = new();
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>()))
            .Callback<ActorReminderToken>(token => unregistered.Add(token.Name))
            .Returns(Task.CompletedTask);

        var actor = CreateActor(mockStateManager, mockObjectStore, mockTimerManager);

        await actor.ReceiveReminderAsync("reap", null!, TimeSpan.Zero, TimeSpan.FromMinutes(1));

        mockObjectStore.Verify(o => o.DeleteAsync("blob-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("reap", unregistered);
        Assert.False(stateData.ContainsKey("reap-state"));
    }

    [Fact]
    public async Task ReceiveReminder_RetriesOnFailure_AndGivesUpAfterMaxAttempts()
    {
        var stateData = new Dictionary<string, object>
        {
            ["reap-state"] = new BlobReaperActorState { BlobReference = "blob-1" }
        };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockObjectStore = new Mock<IObjectStore>();
        mockObjectStore.Setup(o => o.DeleteAsync("blob-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));

        var mockTimerManager = new Mock<ActorTimerManager>();
        List<string> unregistered = new();
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>()))
            .Callback<ActorReminderToken>(token => unregistered.Add(token.Name))
            .Returns(Task.CompletedTask);

        var actor = CreateActor(mockStateManager, mockObjectStore, mockTimerManager);

        // Attempts 1 and 2 fail but keep retrying (no unregister yet)
        await actor.ReceiveReminderAsync("reap", null!, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.Empty(unregistered);
        Assert.True(stateData.ContainsKey("reap-state"));

        await actor.ReceiveReminderAsync("reap", null!, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.Empty(unregistered);

        // Attempt 3 (MaxAttempts) fails and gives up
        await actor.ReceiveReminderAsync("reap", null!, TimeSpan.Zero, TimeSpan.FromMinutes(1));

        mockObjectStore.Verify(o => o.DeleteAsync("blob-1", It.IsAny<CancellationToken>()), Times.Exactly(3));
        Assert.Contains("reap", unregistered);
        Assert.False(stateData.ContainsKey("reap-state"));
    }

    [Fact]
    public async Task ReceiveReminder_IgnoresUnknownReminderName()
    {
        var stateData = new Dictionary<string, object>
        {
            ["reap-state"] = new BlobReaperActorState { BlobReference = "blob-1" }
        };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockObjectStore = new Mock<IObjectStore>();

        var actor = CreateActor(mockStateManager, mockObjectStore);

        await actor.ReceiveReminderAsync("some-other-reminder", null!, TimeSpan.Zero, TimeSpan.FromMinutes(1));

        mockObjectStore.Verify(o => o.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PostponeDeletion_LaterDelay_ReregistersReminderWithLaterFireTime()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockObjectStore = new Mock<IObjectStore>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        List<(string name, TimeSpan dueTime, TimeSpan period)> reminderRegistrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => reminderRegistrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);

        var actor = CreateActor(mockStateManager, mockObjectStore, mockTimerManager);

        await actor.ScheduleDeletion(new ScheduleDeletionRequest { BlobReference = "blob-1", DelaySeconds = 30 });
        await actor.PostponeDeletion(new PostponeDeletionRequest { BlobReference = "blob-1", NewDelaySeconds = 86400 });

        Assert.Equal(2, reminderRegistrations.Count);
        Assert.True(reminderRegistrations[1].dueTime > reminderRegistrations[0].dueTime);
        Assert.True(reminderRegistrations[1].dueTime >= TimeSpan.FromSeconds(86399));
    }

    [Fact]
    public async Task PostponeDeletion_EarlierDelay_DoesNotShortenSchedule()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockObjectStore = new Mock<IObjectStore>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        List<(string name, TimeSpan dueTime, TimeSpan period)> reminderRegistrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => reminderRegistrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);

        var actor = CreateActor(mockStateManager, mockObjectStore, mockTimerManager);

        await actor.ScheduleDeletion(new ScheduleDeletionRequest { BlobReference = "blob-1", DelaySeconds = 86400 });
        await actor.PostponeDeletion(new PostponeDeletionRequest { BlobReference = "blob-1", NewDelaySeconds = 30 });

        // Only the initial ScheduleDeletion registration - postponing to an earlier time is a no-op.
        Assert.Single(reminderRegistrations);
    }

    [Fact]
    public async Task PostponeDeletion_UnknownBlob_IsNoOp()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockObjectStore = new Mock<IObjectStore>();
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);

        var actor = CreateActor(mockStateManager, mockObjectStore, mockTimerManager);

        await actor.PostponeDeletion(new PostponeDeletionRequest { BlobReference = "unknown-blob", NewDelaySeconds = 86400 });

        mockTimerManager.Verify(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()), Times.Never);
    }
}
