using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class QueueActorRaceConditionTests
{
    private Mock<IActorStateManager> CreateMockStateManager()
    {
        var mock = new Mock<IActorStateManager>();
        var stateData = new Dictionary<string, object>();

        mock.Setup(m => m.TryGetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is ActorMetadata metadata)
                {
                    return new ConditionalValue<ActorMetadata>(true, metadata);
                }
                return new ConditionalValue<ActorMetadata>(false, null);
            });

        mock.Setup(m => m.TryGetStateAsync<LockState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is LockState lockState)
                {
                    return new ConditionalValue<LockState>(true, lockState);
                }
                return new ConditionalValue<LockState>(false, null);
            });

        mock.Setup(m => m.TryGetStateAsync<Queue<QueueSegmentItem>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is Queue<QueueSegmentItem> queue)
                {
                    return new ConditionalValue<Queue<QueueSegmentItem>>(true, queue);
                }
                return new ConditionalValue<Queue<QueueSegmentItem>>(false, null);
            });

        mock.Setup(m => m.GetStateAsync<ActorMetadata>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) =>
            {
                if (stateData.ContainsKey(key) && stateData[key] is ActorMetadata metadata)
                {
                    return metadata;
                }
                return null!;
            });

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

        return mock;
    }

    private async Task<QueueActor> CreateActorAsync(Mock<IActorStateManager> mockStateManager)
    {
        var mockTimerManager = new Mock<ActorTimerManager>();
        mockTimerManager.Setup(m => m.RegisterTimerAsync(It.IsAny<ActorTimer>()))
            .Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions
        {
            TimerManager = mockTimerManager.Object
        };

        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<Interfaces.PushRequest, Interfaces.PushResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<Interfaces.PushRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Interfaces.PushResponse { Success = true });

        var actorHost = ActorHost.CreateForTest<QueueActor>(testOptions);
        var actor = new QueueActor(actorHost, mockInvoker.Object);

        var stateManagerProperty = typeof(Actor).GetProperty("StateManager");
        stateManagerProperty?.SetValue(actor, mockStateManager.Object);

        var onActivateMethod = typeof(QueueActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onActivateMethod != null)
        {
            await (Task)onActivateMethod.Invoke(actor, null)!;
        }

        return actor;
    }

    [Fact]
    public async Task ReceiveReminderAsync_CallsSaveStateTwice_CreatesRaceCondition()
    {
        // Arrange
        var mockStateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(mockStateManager);

        // Push an item and lock it
        string itemJson = "{\"id\":1,\"data\":\"test\"}";
        await actor.Push(new PushRequest
        {
            Items = [new PushItem { ItemJson = itemJson, Priority = 1 }]
        });

        var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 1 });
        string lockId = popResult.Items[0].LockId!;

        // Reset SaveStateAsync invocation count
        mockStateManager.Invocations.Clear();

        // Act - Simulate reminder firing (should only call SaveStateAsync ONCE)
        await actor.ReceiveReminderAsync($"lock-{lockId}", Array.Empty<byte>(), TimeSpan.Zero, TimeSpan.Zero);

        // Assert - Should only have ONE SaveStateAsync call for atomicity
        var saveStateCalls = mockStateManager.Invocations
            .Where(i => i.Method.Name == "SaveStateAsync")
            .Count();

        // EXPECTED: 1 (atomic batch)
        // ACTUAL: 2 (Push calls SaveStateAsync, then ReceiveReminderAsync calls it again)
        Assert.Equal(1, saveStateCalls); // This will FAIL, demonstrating the race condition
    }
}
