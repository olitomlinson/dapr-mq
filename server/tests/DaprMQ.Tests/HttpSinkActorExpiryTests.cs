using System.Net;
using System.Text.Json;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using Moq.Protected;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// HttpSinkActor's side of message expiry: a poll that only removed expired items calls again within
/// the same reminder turn, and each posted item carries its expiry.
/// </summary>
public class HttpSinkActorExpiryTests
{
    private readonly Dictionary<string, object> _state = new()
    {
        ["sink-state"] = new HttpSinkActorState
        {
            Url = "http://test.com",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            CurrentIntervalSeconds = 4
        }
    };

    private readonly Mock<IQueueActorInvoker> _queue = new();
    private readonly List<string> _posted = new();

    private HttpSinkActor CreateActor()
    {
        var stateManager = new Mock<IActorStateManager>();
        stateManager.Setup(m => m.GetStateAsync<HttpSinkActorState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => (HttpSinkActorState)_state[key]);
        stateManager.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken _) =>
            {
                _state[key] = value;
                return Task.CompletedTask;
            });
        stateManager.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage request, CancellationToken _) =>
            {
                _posted.Add(await request.Content!.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            });
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));

        var timerManager = new Mock<ActorTimerManager>();
        timerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);

        var actor = new HttpSinkActor(
            ActorHost.CreateForTest<HttpSinkActor>(new ActorTestOptions { TimerManager = timerManager.Object }),
            _queue.Object, httpClientFactory.Object, Mock.Of<ICatalogRegistrar>());
        typeof(Actor).GetProperty("StateManager")!.SetValue(actor, stateManager.Object);
        return actor;
    }

    private Moq.Language.ISetupSequentialResult<Task<DequeueLockedResponse>> SetupDequeue() =>
        _queue.SetupSequence(m => m.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
            It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<DequeueLockedRequest>()));

    private int CurrentInterval => ((HttpSinkActorState)_state["sink-state"]).CurrentIntervalSeconds;

    [Fact]
    public async Task Poll_ExpiredMore_IsRetriedWithinTheSameTurn_AndPostsTheItemsItThenFinds()
    {
        SetupDequeue()
            .ReturnsAsync(new DequeueLockedResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueLockedResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueLockedResponse
            {
                Items =
                [
                    new DequeueLockedItem
                    {
                        ItemJson = "{\"id\":1}", Priority = 1, LockId = "lock-1", LockExpiresAt = 1_800_000_030,
                        ExpiryAt = 1_800_003_600, ExpiryTtlSeconds = 3600, OriginalExpiryTtlSeconds = 60
                    }
                ]
            });

        await CreateActor().ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(4));

        var element = Assert.Single(JsonDocument.Parse(Assert.Single(_posted)).RootElement.EnumerateArray());
        Assert.Equal(1_800_003_600, element.GetProperty("expiryAt").GetDouble());
        Assert.Equal(3600, element.GetProperty("expiryTtlSeconds").GetInt32());
        Assert.Equal(60, element.GetProperty("originalExpiryTtlSeconds").GetInt32());
        Assert.Equal(1, CurrentInterval);
    }

    [Fact]
    public async Task Poll_ExpiredMoreThenEmpty_BacksOffAsForAnEmptyQueue()
    {
        SetupDequeue()
            .ReturnsAsync(new DequeueLockedResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueLockedResponse { IsEmpty = true });

        await CreateActor().ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(4));

        Assert.Empty(_posted);
        Assert.Equal(8, CurrentInterval);
        _queue.Verify(m => m.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
            It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<DequeueLockedRequest>()), Times.Exactly(2));
    }
}
