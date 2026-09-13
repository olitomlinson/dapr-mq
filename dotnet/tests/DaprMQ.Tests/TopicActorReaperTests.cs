using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class TopicActorReaperTests
{
    private static Task<TopicActor> CreateActorAsync(Mock<IActorStateManager> mockStateManager, Mock<IQueueActorInvoker>? invoker = null, TopicActorConfig? config = null) =>
        TopicActorTests.CreateActorAsync(mockStateManager, invoker, config);

    [Fact]
    public async Task ReapItems_DeletesItemsBelowMinCursor_KeepsItemsStillNeeded()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();

        // "fast" always succeeds, "slow" always fails so its cursor never advances.
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.Is<ActorId>(id => id.GetId().EndsWith("-fast")),
                ActorMethodNames.Push, It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 1 });
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.Is<ActorId>(id => id.GetId().EndsWith("-slow")),
                ActorMethodNames.Push, It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = false, ItemsPushed = 0, ErrorMessage = "nope" });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "fast" });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "slow" });
        await actor.Publish(new PublishRequest { Items = new List<PushItem> { new PushItem { ItemJson = "{}" } } });

        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // "fast" has advanced past item 0, "slow" hasn't - minCursor is still 0, item must survive.
        Assert.True(stateData.ContainsKey("item_0"));

        await actor.ReceiveReminderAsync("reap-items", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(30));

        Assert.True(stateData.ContainsKey("item_0"));
    }

    [Fact]
    public async Task ReapItems_ZeroSubscribers_ReapsEverythingUpToNextSequence()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var actor = await CreateActorAsync(mockStateManager);

        await actor.Publish(new PublishRequest { Items = new List<PushItem> { new PushItem { ItemJson = "{}" }, new PushItem { ItemJson = "{}" } } });

        Assert.True(stateData.ContainsKey("item_0"));
        Assert.True(stateData.ContainsKey("item_1"));

        await actor.ReceiveReminderAsync("reap-items", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(30));

        Assert.False(stateData.ContainsKey("item_0"));
        Assert.False(stateData.ContainsKey("item_1"));
    }

    [Fact]
    public async Task ReapItems_AllCursorsPastItem_ReapsIt()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Push, It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 1 });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });
        await actor.Publish(new PublishRequest { Items = new List<PushItem> { new PushItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        await actor.ReceiveReminderAsync("reap-items", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(30));

        Assert.False(stateData.ContainsKey("item_0"));
    }

    [Fact]
    public async Task ReapItems_UnsubscribingRemovesFrozenCursorFromMinComputation()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.Is<ActorId>(id => id.GetId().EndsWith("-active")),
                ActorMethodNames.Push, It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 1 });
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.Is<ActorId>(id => id.GetId().EndsWith("-stuck")),
                ActorMethodNames.Push, It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = false, ItemsPushed = 0, ErrorMessage = "nope" });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "active" });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "stuck" });
        await actor.Publish(new PublishRequest { Items = new List<PushItem> { new PushItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // "stuck" never advances - unsubscribing it should unblock the reaper.
        await actor.Unsubscribe(new UnsubscribeRequest { SubscriberId = "stuck" });
        await actor.ReceiveReminderAsync("reap-items", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(30));

        Assert.False(stateData.ContainsKey("item_0"));
    }

    [Fact]
    public async Task ReapItems_DeliveredPublish_RemovesPublishRecordAndSeqIndex_StatusThenNotFound()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Push, It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 1 });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);

        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });
        var publishResponse = await actor.Publish(new PublishRequest { Items = new List<PushItem> { new PushItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // Delivered but not yet reaped - status still resolves.
        var statusBeforeReap = await actor.GetPublishStatus(new GetPublishStatusRequest { PublishId = publishResponse.PublishId });
        Assert.True(statusBeforeReap.Found);
        Assert.True(stateData.ContainsKey($"publish_{publishResponse.PublishId}"));
        Assert.True(stateData.ContainsKey("publish-seq_0"));

        await actor.ReceiveReminderAsync("reap-items", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(30));

        Assert.False(stateData.ContainsKey($"publish_{publishResponse.PublishId}"));
        Assert.False(stateData.ContainsKey("publish-seq_0"));

        var statusAfterReap = await actor.GetPublishStatus(new GetPublishStatusRequest { PublishId = publishResponse.PublishId });
        Assert.False(statusAfterReap.Found);
    }

    [Fact]
    public async Task ReapItems_StopsAtFirstUndeliveredPublish_MonotonicOrder()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockInvoker = new Mock<IQueueActorInvoker>();

        // First relay tick: subscriber succeeds, delivering publish #1 only (item 0).
        var callCount = 0;
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Push, It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // Succeed on the first relay tick (delivers publish #1), then fail forever after
                // (publish #2 never gets delivered), so its cursor freezes right after item 0.
                return callCount == 1
                    ? new PushResponse { Success = true, ItemsPushed = 1 }
                    : new PushResponse { Success = false, ItemsPushed = 0, ErrorMessage = "nope" };
            });

        var actor = await CreateActorAsync(mockStateManager, mockInvoker);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "sub-a" });

        var firstPublish = await actor.Publish(new PublishRequest { Items = new List<PushItem> { new PushItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        var secondPublish = await actor.Publish(new PublishRequest { Items = new List<PushItem> { new PushItem { ItemJson = "{}" } } });
        await actor.ReceiveReminderAsync("relay", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // Sanity: first publish delivered (cursor advanced past it), second did not.
        var metadata = (TopicMetadata)stateData["metadata"];
        Assert.Equal(1, metadata.DispatchCursors["sub-a"]);

        await actor.ReceiveReminderAsync("reap-items", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromSeconds(30));

        var firstStatus = await actor.GetPublishStatus(new GetPublishStatusRequest { PublishId = firstPublish.PublishId });
        var secondStatus = await actor.GetPublishStatus(new GetPublishStatusRequest { PublishId = secondPublish.PublishId });

        Assert.False(firstStatus.Found, "delivered publish should have been reaped");
        Assert.True(secondStatus.Found, "undelivered publish must survive reaping");
    }

    [Fact]
    public async Task ReapGeneration_FiredWhileStillCurrent_DoesNotDeleteAndReschedules()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockTimerManager = new Mock<ActorTimerManager>();
        List<(string name, TimeSpan due, TimeSpan period)> registrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => registrations.Add((r.Name, r.DueTime, r.Period)))
            .Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>())).Returns(Task.CompletedTask);

        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockTimerManager: mockTimerManager);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });

        registrations.Clear();
        await actor.ReceiveReminderAsync("reap-generation-1", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromMilliseconds(-1));

        Assert.True(stateData.ContainsKey("subscriber-set-generation-1"));
        Assert.Contains(registrations, r => r.name == "reap-generation-1");
    }

    [Fact]
    public async Task ReapGeneration_FiredAfterSuperseded_DeletesAndDoesNotReschedule()
    {
        var stateData = new Dictionary<string, object>();
        var mockStateManager = TopicActorTests.CreateMockStateManager(stateData);
        var mockTimerManager = new Mock<ActorTimerManager>();
        List<string> registrations = new();
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>()))
            .Callback<ActorReminder>(r => registrations.Add(r.Name))
            .Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>())).Returns(Task.CompletedTask);

        var actor = await TopicActorTests.CreateActorAsync(mockStateManager, mockTimerManager: mockTimerManager);
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "a" });
        await actor.Subscribe(new SubscribeRequest { SubscriberId = "b" }); // generation now 2, generation 1 superseded

        registrations.Clear();
        await actor.ReceiveReminderAsync("reap-generation-1", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.FromMilliseconds(-1));

        Assert.False(stateData.ContainsKey("subscriber-set-generation-1"));
        Assert.DoesNotContain("reap-generation-1", registrations);
    }
}
