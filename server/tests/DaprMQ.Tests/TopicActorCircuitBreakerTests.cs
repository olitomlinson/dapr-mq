using Dapr.Actors;
using Moq;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class TopicActorCircuitBreakerTests
{
    private static Mock<IQueueActorInvoker> CreateFailingInvoker(string subscriberSuffix)
    {
        var mock = new Mock<IQueueActorInvoker>();
        mock.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.Is<ActorId>(id => id.GetId().EndsWith(subscriberSuffix)),
                ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = false, ItemsEnqueued = 0, ErrorMessage = "down" });
        return mock;
    }

    private async Task<(TopicActor actor, Dictionary<string, object> state)> SetupWithFailingSubscriberAsync(TopicActorConfig? config = null)
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = CreateFailingInvoker("-sub-a");
        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockInvoker, config);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });
        return (actor, stateData);
    }

    [Fact]
    public async Task SingleFailure_CreatesCircuitStateWithFutureNextRetryAt_SkipsSubsequentTickBeforeThat()
    {
        var (actor, state) = await SetupWithFailingSubscriberAsync();

        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var circuit = (CircuitBreakerState)state["circuit_a"];
        Assert.Equal(1, circuit.ConsecutiveFailures);
        Assert.NotNull(circuit.NextRetryAt);
        Assert.True(circuit.NextRetryAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.False(circuit.Blacklisted);
    }

    [Fact]
    public async Task ConsecutiveFailures_IncreaseBackoffDelayEachTime()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = CreateFailingInvoker("-sub-a");
        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockInvoker);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });

        double? previousGap = null;
        for (int i = 0; i < 3; i++)
        {
            await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });

            // Force the retry window open so each tick actually attempts (and fails) again.
            if (stateData.TryGetValue("circuit_a", out var existing))
            {
                stateData["circuit_a"] = ((CircuitBreakerState)existing) with { NextRetryAt = 0 };
            }

            double before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

            var circuit = (CircuitBreakerState)stateData["circuit_a"];
            double gap = circuit.NextRetryAt!.Value - before;

            if (previousGap.HasValue)
            {
                Assert.True(gap > previousGap.Value, $"Expected growing backoff, got {previousGap} then {gap}");
            }
            previousGap = gap;
        }
    }

    [Fact]
    public async Task OneHourElapsedSinceFirstFailure_Blacklists_NoFurtherAttempts()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = CreateFailingInvoker("-sub-a");
        var config = new TopicActorConfig { CircuitBreakerBlacklistAfterSeconds = 3600 };
        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockInvoker, config);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });

        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // Simulate an hour having elapsed since the first failure, and reopen the retry window.
        var circuit = (CircuitBreakerState)stateData["circuit_a"];
        stateData["circuit_a"] = circuit with { FirstFailureAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3601, NextRetryAt = 0 };

        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var updated = (CircuitBreakerState)stateData["circuit_a"];
        Assert.True(updated.Blacklisted);
        Assert.Null(updated.NextRetryAt);

        // Further ticks must not attempt Enqueue at all for a blacklisted subscriber.
        var invocationsBefore = mockInvoker.Invocations.Count;
        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(invocationsBefore, mockInvoker.Invocations.Count);
    }

    [Fact]
    public async Task SingleSuccess_FullyClearsCircuitState()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();
        int callCount = 0;
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new EnqueueResponse { Success = false, ItemsEnqueued = 0, ErrorMessage = "down" }
                    : new EnqueueResponse { Success = true, ItemsEnqueued = 1 };
            });

        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockInvoker);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });

        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.True(stateData.ContainsKey("circuit_a"));

        stateData["circuit_a"] = ((CircuitBreakerState)stateData["circuit_a"]) with { NextRetryAt = 0 };
        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.False(stateData.ContainsKey("circuit_a"));
    }

    [Fact]
    public async Task ResetCircuitBreaker_OnBlacklisted_DeletesState_NextTickRetriesFromUnchangedCursor()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = CreateFailingInvoker("-sub-a");
        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockInvoker);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });

        stateData["circuit_a"] = new CircuitBreakerState
        {
            SubscriberId = "a",
            ConsecutiveFailures = 10,
            FirstFailureAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4000,
            NextRetryAt = null,
            Blacklisted = true
        };

        var resetResponse = await actor.ResetCircuitBreaker(new ResetCircuitBreakerRequest { SubscriberId = "a" });
        Assert.True(resetResponse.Success);
        Assert.False(stateData.ContainsKey("circuit_a"));

        // Now, with the invoker made to succeed, the cursor (still 0, untouched while blacklisted) should advance.
        var successInvoker = new Mock<IQueueActorInvoker>();
        successInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var actor2 = await TopicActorTests.CreateActorAsync(mockStateManager, successInvoker);
        await actor2.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor2.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Equal(1, metadata.DispatchCursors["a"]);
    }

    [Fact]
    public async Task ResetCircuitBreaker_OnHealthySubscriber_NoOpSuccess()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var actor = await TopicActorTests.CreateActorAsync(mockStateManager);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });

        var response = await actor.ResetCircuitBreaker(new ResetCircuitBreakerRequest { SubscriberId = "a" });

        Assert.True(response.Success);
    }

    [Fact]
    public async Task ResetCircuitBreaker_UnknownSubscriber_ReturnsSubscriberNotFound()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var actor = await TopicActorTests.CreateActorAsync(mockStateManager);

        var response = await actor.ResetCircuitBreaker(new ResetCircuitBreakerRequest { SubscriberId = "ghost" });

        Assert.False(response.Success);
        Assert.Equal("SUBSCRIBER_NOT_FOUND", response.ErrorCode);
    }

    [Fact]
    public async Task OneSubscribersBackoffState_HasZeroEffectOnOtherSubscribersTicks()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.Is<ActorId>(id => id.GetId().EndsWith("-bad")),
                ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = false, ItemsEnqueued = 0, ErrorMessage = "down" });
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.Is<ActorId>(id => id.GetId().EndsWith("-good")),
                ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockInvoker);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "bad" });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "good" });

        // Blacklist "bad" up front.
        stateData["circuit_bad"] = new CircuitBreakerState
        {
            SubscriberId = "bad",
            ConsecutiveFailures = 20,
            FirstFailureAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4000,
            NextRetryAt = null,
            Blacklisted = true
        };

        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Equal(1, metadata.DispatchCursors["good"]);
        Assert.Equal(0, metadata.DispatchCursors["bad"]);
    }
}
