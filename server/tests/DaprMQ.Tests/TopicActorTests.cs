using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class TopicActorTests
{
    internal static Mock<IActorStateManager> CreateMockStateManager(Dictionary<string, object> stateData)
    {
        var mock = new Mock<IActorStateManager>();

        mock.Setup(m => m.TryGetStateAsync<TopicMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is TopicMetadata m
                    ? new ConditionalValue<TopicMetadata>(true, m)
                    : new ConditionalValue<TopicMetadata>(false, null));

        mock.Setup(m => m.TryGetStateAsync<SubscriberSetGeneration>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is SubscriberSetGeneration g
                    ? new ConditionalValue<SubscriberSetGeneration>(true, g)
                    : new ConditionalValue<SubscriberSetGeneration>(false, null));

        mock.Setup(m => m.TryGetStateAsync<TopicItem>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is TopicItem i
                    ? new ConditionalValue<TopicItem>(true, i)
                    : new ConditionalValue<TopicItem>(false, null));

        mock.Setup(m => m.TryGetStateAsync<CircuitBreakerState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is CircuitBreakerState c
                    ? new ConditionalValue<CircuitBreakerState>(true, c)
                    : new ConditionalValue<CircuitBreakerState>(false, null));

        mock.Setup(m => m.TryGetStateAsync<PublishRecord>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is PublishRecord p
                    ? new ConditionalValue<PublishRecord>(true, p)
                    : new ConditionalValue<PublishRecord>(false, null));

        mock.Setup(m => m.TryGetStateAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.TryGetValue(key, out var v) && v is string s
                    ? new ConditionalValue<string>(true, s)
                    : new ConditionalValue<string>(false, null));

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

    internal static async Task<TopicActor> CreateActorAsync(
        Mock<IActorStateManager> mockStateManager,
        Mock<IQueueActorInvoker>? mockQueueActorInvoker = null,
        TopicActorConfig? config = null,
        Mock<ActorTimerManager>? mockTimerManager = null,
        string actorId = "test-topic",
        Mock<IHttpSinkActorInvoker>? mockHttpSinkActorInvoker = null)
    {
        bool timerManagerProvided = mockTimerManager != null;
        mockTimerManager ??= new Mock<ActorTimerManager>();
        if (!timerManagerProvided)
        {
            // Only install default no-op setups when the caller didn't bring their own mock -
            // otherwise this would shadow any callback-based setups the caller configured.
            mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);
            mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>())).Returns(Task.CompletedTask);
        }

        var testOptions = new ActorTestOptions { ActorId = new ActorId(actorId), TimerManager = mockTimerManager.Object };

        mockQueueActorInvoker ??= new Mock<IQueueActorInvoker>();
        mockHttpSinkActorInvoker ??= new Mock<IHttpSinkActorInvoker>();
        config ??= new TopicActorConfig();

        var actorHost = ActorHost.CreateForTest<TopicActor>(testOptions);
        var actor = new TopicActor(actorHost, mockQueueActorInvoker.Object, mockHttpSinkActorInvoker.Object, config);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        var onActivateMethod = typeof(TopicActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return actor;
    }

    [Fact]
    public async Task Subscribe_NewSubscriber_AddsAndInitializesCursorAtNextSequence()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        mockStateManager.Invocations.Clear();
        var response = await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });

        Assert.True(response.Success);
        Assert.Equal("test-topic-sub-sub-a", response.QueueActorId);

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Contains("sub-a", metadata.SubscriberIds);
        Assert.Equal(1, metadata.CurrentGeneration);
        Assert.Equal(0, metadata.DispatchCursors["sub-a"]);

        var generation = (SubscriberSetGeneration)stateData["subscriber-set-generation-1"];
        Assert.Equal(new List<string> { "sub-a" }, generation.SubscriberIds);

        mockStateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Subscribe_DuplicateSubscriberId_ReturnsSubscriberExists()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });
        var response = await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });

        Assert.False(response.Success);
        Assert.Equal("SUBSCRIBER_EXISTS", response.ErrorCode);
    }

    [Fact]
    public async Task Subscribe_WithHttpSink_InitializesSinkOnProvisionedQueue()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockHttpSinkActorInvoker = new Mock<IHttpSinkActorInvoker>();
        InitializeHttpSinkRequest? capturedRequest = null;
        ActorId? capturedActorId = null;
        mockHttpSinkActorInvoker.Setup(i => i.InvokeMethodAsync(
                It.IsAny<ActorId>(), "InitializeHttpSink", It.IsAny<InitializeHttpSinkRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, InitializeHttpSinkRequest, CancellationToken>((id, _, req, _) =>
            {
                capturedActorId = id;
                capturedRequest = req;
            })
            .Returns(Task.CompletedTask);

        var actor = await CreateActorAsync(mockStateManager, mockHttpSinkActorInvoker: mockHttpSinkActorInvoker);

        var response = await actor.Subscribe(new SubscribeRequest
        {
            SubscriberId = "sub-a",
            HttpSink = new TopicHttpSinkConfig { Url = "https://example.com/webhook", MaxConcurrency = 7, LockTtlSeconds = 45 }
        });

        Assert.True(response.Success);
        Assert.Equal("test-topic-sub-sub-a-sink", capturedActorId?.GetId());
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://example.com/webhook", capturedRequest!.Url);
        Assert.Equal("test-topic-sub-sub-a", capturedRequest.QueueActorId);
        Assert.Equal(7, capturedRequest.MaxConcurrency);
        Assert.Equal(45, capturedRequest.LockTtlSeconds);
    }

    [Fact]
    public async Task Subscribe_WithoutHttpSink_NeverCallsSinkInvoker()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockHttpSinkActorInvoker = new Mock<IHttpSinkActorInvoker>();

        var actor = await CreateActorAsync(mockStateManager, mockHttpSinkActorInvoker: mockHttpSinkActorInvoker);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });

        mockHttpSinkActorInvoker.Verify(i => i.InvokeMethodAsync(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<InitializeHttpSinkRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Subscribe_SinkInitializationThrows_SubscribeStillSucceeds()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockHttpSinkActorInvoker = new Mock<IHttpSinkActorInvoker>();
        mockHttpSinkActorInvoker.Setup(i => i.InvokeMethodAsync(
                It.IsAny<ActorId>(), "InitializeHttpSink", It.IsAny<InitializeHttpSinkRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sink actor unreachable"));

        var actor = await CreateActorAsync(mockStateManager, mockHttpSinkActorInvoker: mockHttpSinkActorInvoker);

        var response = await actor.Subscribe(new SubscribeRequest
        {
            SubscriberId = "sub-a",
            HttpSink = new TopicHttpSinkConfig { Url = "https://example.com/webhook" }
        });

        Assert.True(response.Success);
        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Contains("sub-a", metadata.SubscriberIds);
    }

    [Fact]
    public async Task Subscribe_WithDedupEnabledFalse_CallsConfigureDedupOnProvisionedQueue()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueActorInvoker = new Mock<IQueueActorInvoker>();
        ConfigureDedupRequest? capturedRequest = null;
        ActorId? capturedActorId = null;
        mockQueueActorInvoker.Setup(i => i.InvokeMethodAsync<ConfigureDedupRequest, ConfigureDedupResponse>(
                It.IsAny<ActorId>(), "ConfigureDedup", It.IsAny<ConfigureDedupRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ConfigureDedupRequest, CancellationToken>((id, _, req, _) =>
            {
                capturedActorId = id;
                capturedRequest = req;
            })
            .ReturnsAsync(new ConfigureDedupResponse { Success = true });

        var actor = await CreateActorAsync(mockStateManager, mockQueueActorInvoker: mockQueueActorInvoker);

        var response = await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a", DedupEnabled = false });

        Assert.True(response.Success);
        Assert.Equal("test-topic-sub-sub-a", capturedActorId?.GetId());
        Assert.NotNull(capturedRequest);
        Assert.False(capturedRequest!.Enabled);
    }

    [Fact]
    public async Task Subscribe_WithDedupEnabledOmitted_NeverCallsConfigureDedup()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueActorInvoker = new Mock<IQueueActorInvoker>();

        var actor = await CreateActorAsync(mockStateManager, mockQueueActorInvoker: mockQueueActorInvoker);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });

        mockQueueActorInvoker.Verify(i => i.InvokeMethodAsync<ConfigureDedupRequest, ConfigureDedupResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ConfigureDedupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Subscribe_ConfigureDedupThrows_SubscribeStillSucceeds()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueActorInvoker = new Mock<IQueueActorInvoker>();
        mockQueueActorInvoker.Setup(i => i.InvokeMethodAsync<ConfigureDedupRequest, ConfigureDedupResponse>(
                It.IsAny<ActorId>(), "ConfigureDedup", It.IsAny<ConfigureDedupRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue actor unreachable"));

        var actor = await CreateActorAsync(mockStateManager, mockQueueActorInvoker: mockQueueActorInvoker);

        var response = await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a", DedupEnabled = true });

        Assert.True(response.Success);
    }

    [Fact]
    public async Task Subscribe_AfterItemsAlreadyExist_CursorSkipsBacklog()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "late-sub" });

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Equal(1, metadata.DispatchCursors["late-sub"]);
    }

    [Fact]
    public async Task Unsubscribe_RemovesSubscriberAndCursor_NewGenerationExcludesThem()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });
        var response = await actor.Unsubscribe(new UnsubscribeRequest { SubscriberId = "sub-a" });

        Assert.True(response.Success);

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.DoesNotContain("sub-a", metadata.SubscriberIds);
        Assert.False(metadata.DispatchCursors.ContainsKey("sub-a"));
        Assert.Equal(2, metadata.CurrentGeneration);

        var generation = (SubscriberSetGeneration)stateData["subscriber-set-generation-2"];
        Assert.DoesNotContain("sub-a", generation.SubscriberIds);
    }

    [Fact]
    public async Task Unsubscribe_UnknownSubscriber_ReturnsSubscriberNotFound()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        var response = await actor.Unsubscribe(new UnsubscribeRequest { SubscriberId = "ghost" });

        Assert.False(response.Success);
        Assert.Equal("SUBSCRIBER_NOT_FOUND", response.ErrorCode);
    }

    [Fact]
    public async Task ListSubscribers_ReturnsCurrentSet()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "b" });

        var response = await actor.ListSubscribers();

        Assert.Equal(new List<string> { "a", "b" }, response.SubscriberIds);
    }

    [Fact]
    public async Task Publish_WritesItemsWithStrictlyIncreasingSequenceAndCurrentGeneration_DoesNotTouchCursors()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });
        var metadataBefore = (TopicMetadata)stateData["metadata"];

        var response = await actor.Publish(new PublishRequest
        {
            Items = new List<EnqueueItem>
            {
                new EnqueueItem { ItemJson = "{\"a\":1}" },
                new EnqueueItem { ItemJson = "{\"a\":2}" }
            }
        });

        Assert.True(response.Accepted);
        Assert.Equal(0, response.Sequence);
        Assert.NotEmpty(response.PublishId);

        var item0 = (TopicItem)stateData["item_0"];
        var item1 = (TopicItem)stateData["item_1"];
        Assert.Equal(0, item0.Sequence);
        Assert.Equal(1, item1.Sequence);
        Assert.Equal(metadataBefore.CurrentGeneration, item0.Generation);
        Assert.Equal(metadataBefore.CurrentGeneration, item1.Generation);

        var metadataAfter = (TopicMetadata)stateData["metadata"];
        Assert.Equal(2, metadataAfter.NextSequence);
        Assert.Equal(metadataBefore.DispatchCursors["sub-a"], metadataAfter.DispatchCursors["sub-a"]);
    }

    [Fact]
    public async Task Publish_EmptyItems_ReturnsNotAccepted()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        var response = await actor.Publish(new PublishRequest { Items = new List<EnqueueItem>() });

        Assert.False(response.Accepted);
    }

    [Fact]
    public async Task RelayTick_TwoSubscribers_OneFailsOneSucceeds_OnlySuccessfulCursorAdvances_SingleSave()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();

        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.Is<ActorId>(id => id.GetId() == "test-topic-sub-good"),
                ActorMethodNames.Enqueue,
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.Is<ActorId>(id => id.GetId() == "test-topic-sub-bad"),
                ActorMethodNames.Enqueue,
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = false, ItemsEnqueued = 0, ErrorMessage = "boom" });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "good" });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "bad" });
        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });

        mockStateManager.Invocations.Clear();

        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Equal(1, metadata.DispatchCursors["good"]);
        Assert.Equal(0, metadata.DispatchCursors["bad"]);

        mockStateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(stateData.ContainsKey("circuit_bad"));
        Assert.False(stateData.ContainsKey("circuit_good"));
    }

    [Fact]
    public async Task RelayTick_PublishedItemWithIdempotencyKey_ForwardsKeyUnchangedToSubscriberQueueEnqueue()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();

        List<EnqueueItem>? enqueuedItems = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(),
                ActorMethodNames.Enqueue,
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((_, _, req, _) => enqueuedItems = req.Items)
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });
        await actor.Publish(new PublishRequest
        {
            Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}", IdempotencyKey = "relay-key-1" } }
        });

        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.NotNull(enqueuedItems);
        Assert.Equal("relay-key-1", enqueuedItems![0].IdempotencyKey);
    }

    [Fact]
    public async Task RelayTick_ExcludesItemFromSubscriberWhoseGenerationPredatesIt()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();

        List<EnqueueItem>? enqueuedToLate = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(),
                ActorMethodNames.Enqueue,
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((id, _, req, _) =>
            {
                if (id.GetId() == "test-topic-sub-late") enqueuedToLate = req.Items;
            })
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "early" });
        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"n\":1}" } } });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "late" });
        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{\"n\":2}" } } });

        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var metadata = (TopicMetadata)stateData["metadata"];
        // "late" subscribed after item 0 existed, so its cursor should skip past it without delivery,
        // landing at 2 (past both items) but only ever receiving item 1.
        Assert.Equal(2, metadata.DispatchCursors["late"]);
        Assert.NotNull(enqueuedToLate);
        Assert.Single(enqueuedToLate!);
        Assert.Equal("{\"n\":2}", enqueuedToLate![0].ItemJson);
    }

    [Fact]
    public async Task RelayTick_FullyNonMemberBatch_SkipsEnqueueCallButAdvancesCursor()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "late" });

        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        mockInvoker.Verify(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
            It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Equal(1, metadata.DispatchCursors["late"]);
    }

    [Fact]
    public async Task RelayTick_EnqueueNeverCompletes_TreatedAsFailedNotAwaitedIndefinitely()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();

        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<EnqueueResponse>().Task); // never completes

        var config = new TopicActorConfig { RelayTickTimeoutSeconds = 1 };
        var actor = await CreateActorAsync(mockStateManager, mockInvoker, config);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });
        await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Relay tick took too long: {sw.Elapsed}");

        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Equal(0, metadata.DispatchCursors["sub-a"]);
        Assert.True(stateData.ContainsKey("circuit_sub-a"));
    }

    [Fact]
    public async Task GetPublishStatus_ResolvesTargetAndDeliveredSubscribers()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "b" });
        var publishResponse = await actor.Publish(new PublishRequest { Items = new List<EnqueueItem> { new EnqueueItem { ItemJson = "{}" } } });

        var beforeRelay = await actor.GetPublishStatus(new GetPublishStatusRequest { PublishId = publishResponse.PublishId });
        Assert.True(beforeRelay.Found);
        Assert.False(beforeRelay.Complete);
        Assert.Equal(new List<string> { "a", "b" }, beforeRelay.TargetSubscriberIds);
        Assert.Empty(beforeRelay.DeliveredSubscriberIds);

        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var afterRelay = await actor.GetPublishStatus(new GetPublishStatusRequest { PublishId = publishResponse.PublishId });
        Assert.True(afterRelay.Complete);
        Assert.Equal(2, afterRelay.DeliveredSubscriberIds.Count);
    }

    [Fact]
    public async Task GetPublishStatus_UnknownPublishId_ReturnsNotFound()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        var response = await actor.GetPublishStatus(new GetPublishStatusRequest { PublishId = "does-not-exist" });

        Assert.False(response.Found);
    }
}
