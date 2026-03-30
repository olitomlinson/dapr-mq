using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using Moq.Protected;
using DaprMQ.Interfaces;
using System.Net;

namespace DaprMQ.Tests;

public class HttpSinkActorTests
{
    private Mock<IActorStateManager> CreateMockStateManager(Dictionary<string, object> stateData)
    {
        var mock = new Mock<IActorStateManager>();

        mock.Setup(m => m.GetStateAsync<HttpSinkActorState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key))
                {
                    return (HttpSinkActorState)stateData[key];
                }
                throw new KeyNotFoundException($"State key '{key}' not found");
            });

        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken ct) =>
            {
                stateData[key] = value;
                return Task.CompletedTask;
            });

        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    [Fact]
    public async Task DynamicPolling_EmptyQueue_DoublesInterval()
    {
        // Arrange
        var initialState = new HttpSinkActorState
        {
            Url = "http://test.com",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            CurrentIntervalSeconds = 1
        };

        var stateData = new Dictionary<string, object> { ["sink-state"] = initialState };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        // Mock PopWithAck to return empty queue
        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(),
                "PopWithAck",
                It.IsAny<PopWithAckRequest>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                Items = new List<PopWithAckItem>(),
                IsEmpty = true,
                MaxConcurrencyReached = false
            });

        List<(string name, TimeSpan dueTime, TimeSpan period)> reminderRegistrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => reminderRegistrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<HttpSinkActor>(testOptions);
        var actor = new HttpSinkActor(actorHost, mockQueueInvoker.Object, mockHttpClientFactory.Object);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        // Act - Simulate reminder firing
        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // Assert - Interval should double from 1 to 2
        var updatedState = (HttpSinkActorState)stateData["sink-state"];
        Assert.Equal(2, updatedState.CurrentIntervalSeconds);

        Assert.Single(reminderRegistrations);
        Assert.Equal("sink-poll", reminderRegistrations[0].name);
        Assert.Equal(TimeSpan.FromSeconds(2), reminderRegistrations[0].period);
    }

    [Fact]
    public async Task DynamicPolling_EmptyQueue_CapsAt60Seconds()
    {
        // Arrange - Start at 32s to test cap
        var initialState = new HttpSinkActorState
        {
            Url = "http://test.com",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            // PollingIntervalSeconds removed - using dynamic polling starting at32,
            CurrentIntervalSeconds = 32
        };

        var stateData = new Dictionary<string, object> { ["sink-state"] = initialState };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(),
                "PopWithAck",
                It.IsAny<PopWithAckRequest>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                Items = new List<PopWithAckItem>(),
                IsEmpty = true,
                MaxConcurrencyReached = false
            });

        List<(string name, TimeSpan dueTime, TimeSpan period)> reminderRegistrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => reminderRegistrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<HttpSinkActor>(testOptions);
        var actor = new HttpSinkActor(actorHost, mockQueueInvoker.Object, mockHttpClientFactory.Object);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        // Act
        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(32));

        // Assert - Should cap at 60s, not double to 64s
        var updatedState = (HttpSinkActorState)stateData["sink-state"];
        Assert.Equal(60, updatedState.CurrentIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(60), reminderRegistrations[0].period);
    }

    [Fact]
    public async Task DynamicPolling_ItemsFound_ResetsTo1Second()
    {
        // Arrange - Start at 16s backoff
        var initialState = new HttpSinkActorState
        {
            Url = "http://test.com",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            // PollingIntervalSeconds removed - using dynamic polling starting at16,
            CurrentIntervalSeconds = 16
        };

        var stateData = new Dictionary<string, object> { ["sink-state"] = initialState };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        // Mock PopWithAck to return items
        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(),
                "PopWithAck",
                It.IsAny<PopWithAckRequest>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                Items = new List<PopWithAckItem>
                {
                    new() { ItemJson = "{\"test\":1}", LockId = Guid.NewGuid().ToString(), Priority = 1, LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds() }
                },
                IsEmpty = false,
                MaxConcurrencyReached = false
            });

        // Mock HTTP response as 200 OK
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var mockHttpClient = new HttpClient(mockHttpMessageHandler.Object);
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(mockHttpClient);

        // Mock Acknowledge
        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(),
                "Acknowledge",
                It.IsAny<AcknowledgeRequest>()))
            .ReturnsAsync(new AcknowledgeResponse { Success = true });

        List<(string name, TimeSpan dueTime, TimeSpan period)> reminderRegistrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => reminderRegistrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<HttpSinkActor>(testOptions);
        var actor = new HttpSinkActor(actorHost, mockQueueInvoker.Object, mockHttpClientFactory.Object);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        // Act
        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(16));

        // Assert - Should reset to 1s
        var updatedState = (HttpSinkActorState)stateData["sink-state"];
        Assert.Equal(1, updatedState.CurrentIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(1), reminderRegistrations[0].period);
    }

    [Fact]
    public async Task DynamicPolling_MaxConcurrencyReached_IncreasesButCapsAt4Seconds()
    {
        // Arrange - Start at 2s
        var initialState = new HttpSinkActorState
        {
            Url = "http://test.com",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            // PollingIntervalSeconds removed - using dynamic polling starting at2,
            CurrentIntervalSeconds = 2
        };

        var stateData = new Dictionary<string, object> { ["sink-state"] = initialState };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        // Mock PopWithAck to return MaxConcurrencyReached
        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(),
                "PopWithAck",
                It.IsAny<PopWithAckRequest>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                Items = new List<PopWithAckItem>(),
                IsEmpty = false,
                MaxConcurrencyReached = true
            });

        List<(string name, TimeSpan dueTime, TimeSpan period)> reminderRegistrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => reminderRegistrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<HttpSinkActor>(testOptions);
        var actor = new HttpSinkActor(actorHost, mockQueueInvoker.Object, mockHttpClientFactory.Object);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        // Act - First poll (2→4s)
        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        // Assert - Should double to 4s
        var updatedState = (HttpSinkActorState)stateData["sink-state"];
        Assert.Equal(4, updatedState.CurrentIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(4), reminderRegistrations[0].period);

        // Act - Second poll (should stay at 4s, not double to 8s)
        reminderRegistrations.Clear();
        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(4));

        // Assert - Should cap at 4s
        updatedState = (HttpSinkActorState)stateData["sink-state"];
        Assert.Equal(4, updatedState.CurrentIntervalSeconds);
        Assert.Empty(reminderRegistrations); // No re-registration since interval unchanged
    }
}
