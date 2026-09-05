using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class QueueActorBlobOffloadTests
{
    private static readonly byte[] SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray();
    private readonly IObjectClaimTokenIssuer _tokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
    {
        SigningKey = SigningKey,
        TokenTtl = TimeSpan.FromMinutes(5)
    });
    private readonly BlobReapConfig _blobReapConfig = new() { BackstopSeconds = 86400, PostDownloadSeconds = 86400 };

    private Mock<IActorStateManager> CreateMockStateManager()
    {
        var mock = new Mock<IActorStateManager>();
        var stateData = new Dictionary<string, object>();

        mock.Setup(m => m.TryGetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.ContainsKey(key) && stateData[key] is ActorMetadata metadata
                    ? new ConditionalValue<ActorMetadata>(true, metadata)
                    : new ConditionalValue<ActorMetadata>(false, null));

        mock.Setup(m => m.TryGetStateAsync<LockState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.ContainsKey(key) && stateData[key] is LockState lockState
                    ? new ConditionalValue<LockState>(true, lockState)
                    : new ConditionalValue<LockState>(false, null));

        mock.Setup(m => m.TryGetStateAsync<Queue<QueueSegmentItem>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
                stateData.ContainsKey(key) && stateData[key] is Queue<QueueSegmentItem> queue
                    ? new ConditionalValue<Queue<QueueSegmentItem>>(true, queue)
                    : new ConditionalValue<Queue<QueueSegmentItem>>(false, null));

        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken ct) => { stateData[key] = value; return Task.CompletedTask; });

        mock.Setup(m => m.RemoveStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken ct) => { stateData.Remove(key); return Task.CompletedTask; });

        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return mock;
    }

    private async Task<(QueueActor actor, Mock<IBlobReaperActorInvoker> reaperInvoker)> CreateActorAsync(Mock<IActorStateManager> mockStateManager)
    {
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>())).Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);
        mockTimerManager.Setup(m => m.UnregisterReminderAsync(It.IsAny<ActorReminderToken>())).Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };

        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true });

        var mockReaperInvoker = new Mock<IBlobReaperActorInvoker>();
        mockReaperInvoker.Setup(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ScheduleDeletionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(actorHost, mockInvoker.Object, mockReaperInvoker.Object, _tokenIssuer, _blobReapConfig);

        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        var onActivateMethod = typeof(QueueActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return (actor, mockReaperInvoker);
    }

    private static string BlobRef(string reference) => $"{{\"__daprmq_blob_ref__\":\"{reference}\"}}";

    [Fact]
    public async Task Pop_BlobRefItem_PopulatesBlobReferenceAndSchedulesReaping()
    {
        var mockStateManager = CreateMockStateManager();
        var (actor, reaperInvoker) = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest { Items = new List<PushItem> { new() { ItemJson = BlobRef("blob-1"), Priority = 0 } } });
        var result = await actor.Pop(new PopRequest());

        Assert.NotNull(result.Items[0].ObjectClaimToken);
        Assert.True(_tokenIssuer.TryResolve(result.Items[0].ObjectClaimToken!, out var claim, out _));
        Assert.Equal("blob-1", claim!.BlobReference);
        reaperInvoker.Verify(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
            It.IsAny<ActorId>(), "ScheduleDeletion",
            It.Is<ScheduleDeletionRequest>(r => r.BlobReference == "blob-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pop_NormalItem_LeavesObjectClaimTokenNullAndDoesNotSchedule()
    {
        var mockStateManager = CreateMockStateManager();
        var (actor, reaperInvoker) = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest { Items = new List<PushItem> { new() { ItemJson = "{\"a\":1}", Priority = 0 } } });
        var result = await actor.Pop(new PopRequest());

        Assert.Null(result.Items[0].ObjectClaimToken);
        reaperInvoker.Verify(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ScheduleDeletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PopWithAck_BlobRefItem_PopulatesObjectClaimTokenButDoesNotScheduleReaping()
    {
        var mockStateManager = CreateMockStateManager();
        var (actor, reaperInvoker) = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest { Items = new List<PushItem> { new() { ItemJson = BlobRef("blob-2"), Priority = 0 } } });
        var result = await actor.PopWithAck(new PopWithAckRequest());

        Assert.NotNull(result.Items[0].ObjectClaimToken);
        Assert.True(_tokenIssuer.TryResolve(result.Items[0].ObjectClaimToken!, out var claim, out _));
        Assert.Equal("blob-2", claim!.BlobReference);
        reaperInvoker.Verify(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ScheduleDeletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Acknowledge_BlobRefItem_SchedulesReaping()
    {
        var mockStateManager = CreateMockStateManager();
        var (actor, reaperInvoker) = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest { Items = new List<PushItem> { new() { ItemJson = BlobRef("blob-3"), Priority = 0 } } });
        var popResult = await actor.PopWithAck(new PopWithAckRequest());
        await actor.Acknowledge(new AcknowledgeRequest { LockId = popResult.Items[0].LockId });

        reaperInvoker.Verify(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
            It.IsAny<ActorId>(), "ScheduleDeletion",
            It.Is<ScheduleDeletionRequest>(r => r.BlobReference == "blob-3"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Acknowledge_NormalItem_DoesNotScheduleReaping()
    {
        var mockStateManager = CreateMockStateManager();
        var (actor, reaperInvoker) = await CreateActorAsync(mockStateManager);

        await actor.Push(new PushRequest { Items = new List<PushItem> { new() { ItemJson = "{\"a\":1}", Priority = 0 } } });
        var popResult = await actor.PopWithAck(new PopWithAckRequest());
        await actor.Acknowledge(new AcknowledgeRequest { LockId = popResult.Items[0].LockId });

        reaperInvoker.Verify(i => i.InvokeMethodAsync<ScheduleDeletionRequest>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ScheduleDeletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

}
