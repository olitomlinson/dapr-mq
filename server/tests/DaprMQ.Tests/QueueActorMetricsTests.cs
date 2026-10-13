using Dapr.Actors.Runtime;
using DaprMQ.Interfaces;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public partial class QueueActorTests
{
    private static EnqueueRequest Items(int count) => new()
    {
        Items = Enumerable.Range(0, count).Select(i => new EnqueueItem { ItemJson = $"{{\"n\":{i}}}", Priority = 1 }).ToList()
    };

    [Fact]
    public async Task Metrics_CountEachOperationAfterItsSave()
    {
        using var telemetry = new QueueTelemetry(new MetricLabelPolicy(), new FakeTimeProvider());
        using var collector = new MetricCollector(telemetry.Meter);
        var actor = await CreateActorAsync(CreateMockStateManager(), actorId: "orders", telemetry: telemetry);

        await actor.Enqueue(Items(4));
        await actor.Dequeue(new DequeueRequest { Count = 1 });
        var locked = await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30, Count = 2 });
        await actor.Acknowledge(new AcknowledgeRequest { LockId = locked.Items[0].LockId });
        await actor.Nack(new NackRequest { LockId = locked.Items[1].LockId });

        (string, object?) queue = ("queue", "orders");
        Assert.Equal(4, collector.Sum("daprmq.queue.enqueued", queue, ("kind", "queue")));
        Assert.Equal(3, collector.Sum("daprmq.queue.dequeued", queue));
        Assert.Equal(1, collector.Sum("daprmq.queue.acknowledged", queue));
        Assert.Equal(1, collector.Sum("daprmq.queue.nacked", queue));
    }

    [Fact]
    public async Task Metrics_FailedSave_CountsNothing()
    {
        using var telemetry = new QueueTelemetry(new MetricLabelPolicy(), new FakeTimeProvider());
        using var collector = new MetricCollector(telemetry.Meter);
        var stateManager = CreateMockStateManager();
        var actor = await CreateActorAsync(stateManager, actorId: "orders", telemetry: telemetry);
        stateManager.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("store down"));

        try
        {
            await actor.Enqueue(Items(2));
        }
        catch (InvalidOperationException)
        {
        }

        Assert.Equal(0, collector.Sum("daprmq.queue.enqueued"));
    }

    [Fact]
    public async Task Metrics_DepthIsRecordedOnActivationAndAfterEachMethod()
    {
        using var telemetry = new QueueTelemetry(new MetricLabelPolicy(), new FakeTimeProvider());
        using var collector = new MetricCollector(telemetry.Meter);
        var actor = await CreateActorAsync(CreateMockStateManager(), actorId: "orders", telemetry: telemetry);

        collector.Observe();
        Assert.Equal(0, collector.Sum("daprmq.queue.depth", ("queue", "orders"), ("state", "ready")));

        await actor.Enqueue(Items(3));
        await actor.DequeueLocked(new DequeueLockedRequest { TtlSeconds = 30 });
        await InvokePostMethodAsync(actor);
        collector.Measurements.Clear();
        collector.Observe();

        Assert.Equal(2, collector.Sum("daprmq.queue.depth", ("queue", "orders"), ("state", "ready")));
        Assert.Equal(1, collector.Sum("daprmq.queue.depth", ("queue", "orders"), ("state", "locked")));
    }

    private static Task InvokePostMethodAsync(QueueActor actor) =>
        (Task)typeof(QueueActor).GetMethod("OnPostActorMethodAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(actor, [null!])!;
}
