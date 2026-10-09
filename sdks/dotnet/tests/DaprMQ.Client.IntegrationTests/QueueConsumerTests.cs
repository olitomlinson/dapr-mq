using System.Collections.Concurrent;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>QC-01..QC-06 from sdks/testing/INTEGRATION_TESTS.md - the managed QueueConsumer.</summary>
[Collection(DaprMQClientCollection.Name)]
public class QueueConsumerTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static Task WaitForAsync(Func<bool> condition, string because, int timeoutSeconds = 30) =>
        PollUntilTrueAsync(() => Task.FromResult(condition()), because, timeoutSeconds, intervalMs: 50);

    [Fact]
    public async Task QC01_HandlerSuccess_Acks_AndTheQueueEndsEmpty()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 20);
        var handled = new ConcurrentBag<int>();

        await using (var consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions(),
            (ctx, _) => { handled.Add(Seq(ctx.Item)); return Task.CompletedTask; }))
        {
            await consumer.StartAsync();
            await WaitForAsync(() => handled.Count == 20, "every message to be handled");
        }

        Assert.Equal(Enumerable.Range(1, 20), handled.Order());
        Assert.Null(await client.DequeueLockedAsync(queueId, allowCompetingConsumers: true));
    }

    [Fact]
    public async Task QC02_HandlerError_NackRedeliversWithDeliveryCount2()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);
        var deliveryCounts = new ConcurrentQueue<int>();

        await using (var consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions(), (ctx, _) =>
        {
            deliveryCounts.Enqueue(ctx.DeliveryCount);
            return ctx.DeliveryCount == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask;
        }))
        {
            await consumer.StartAsync();
            await WaitForAsync(() => deliveryCounts.Count == 2, "the nacked message to be redelivered");
        }

        Assert.Equal([1, 2], deliveryCounts);
        Assert.Null(await client.DequeueLockedAsync(queueId, allowCompetingConsumers: true));
    }

    [Fact]
    public async Task QC02_HandlerError_DeadLetterMovesItToTheDeadLetterQueue()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 7, count: 1);
        var attempts = 0;

        await using (var consumer = new QueueConsumer(client, queueId,
            new QueueConsumerOptions { OnHandlerError = QueueHandlerFailureAction.DeadLetter },
            (_, _) => { Interlocked.Increment(ref attempts); throw new InvalidOperationException("poison"); }))
        {
            await consumer.StartAsync();
            var dead = await PollUntilAsync(
                () => client.DequeueLockedAsync(DeadLetterQueueId(queueId)),
                because: "the message to reach the dead-letter queue");
            Assert.Equal(7, Seq(Assert.Single(dead.Items).Item));
        }

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task QC03_MaxConcurrentHandlers_IsNeverExceeded()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 10);
        var running = 0;
        var peak = 0;
        var handled = 0;

        await using (var consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions { MaxConcurrentHandlers = 2 }, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (this)
            {
                peak = Math.Max(peak, now);
            }
            await Task.Delay(100, ct);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref handled);
        }))
        {
            await consumer.StartAsync();
            await WaitForAsync(() => Volatile.Read(ref handled) == 10, "every message to be handled");
        }

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task QC04_StrictOrder_HandlesInQueueOrder_IncludingAfterANack()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 5);
        var succeeded = new ConcurrentQueue<int>();
        var failedOnce = false;

        await using (var consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions { StrictOrder = true }, (ctx, _) =>
        {
            var seq = Seq(ctx.Item);
            if (seq == 2 && !failedOnce)
            {
                failedOnce = true;
                throw new InvalidOperationException("nack 2 once");
            }
            succeeded.Enqueue(seq);
            return Task.CompletedTask;
        }))
        {
            await consumer.StartAsync();
            await WaitForAsync(() => succeeded.Count == 5, "every message to be handled");
        }

        Assert.Equal([1, 2, 3, 4, 5], succeeded);
    }

    [Fact]
    public async Task QC05_Stop_DrainsRunningHandlers_AndReturnsUnstartedMessagesStraightAway()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 3);
        var started = new TaskCompletionSource();
        var handled = new ConcurrentQueue<int>();

        var consumer = new QueueConsumer(client, queueId,
            new QueueConsumerOptions { MaxConcurrentHandlers = 1, MaxActiveMessages = 10, LockTtl = TimeSpan.FromSeconds(300) },
            async (ctx, _) =>
            {
                started.TrySetResult();
                await Task.Delay(500);
                handled.Enqueue(Seq(ctx.Item));
            });
        await consumer.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await consumer.StopAsync();

        Assert.Equal([1], handled);
        // Well inside the 300 s lock, so only the stream's close can have returned them.
        var back = await client.DequeueLockedAsync(queueId, count: 10, allowCompetingConsumers: true);
        Assert.Equal([2, 3], back!.Items.Select(i => Seq(i.Item)));
    }

    [Fact]
    public async Task QC06_BrokenStream_Reconnects_AndEveryMessageIsHandledAtLeastOnce()
    {
        await using var proxy = new TcpProxy(Fixture.GrpcUrl);
        using var channel = SharedGrpcChannel.Create(proxy.Url);
        var client = new DaprMQClient(Fixture.ApiClient, channel);
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 20);
        var handled = new ConcurrentDictionary<int, int>();

        await using (var consumer = new QueueConsumer(client, queueId,
            new QueueConsumerOptions { MaxActiveMessages = 5, MaxConcurrentHandlers = 2 },
            async (ctx, ct) =>
            {
                await Task.Delay(50, ct);
                handled.AddOrUpdate(Seq(ctx.Item), 1, (_, n) => n + 1);
            }))
        {
            await consumer.StartAsync();
            await WaitForAsync(() => handled.Count >= 5, "some messages to be handled before the break");
            proxy.BreakConnections();
            await WaitForAsync(() => handled.Count == 20, "every message to be handled after reconnecting");
        }

        Assert.Equal(Enumerable.Range(1, 20), handled.Keys.Order());
        Assert.Null(await client.DequeueLockedAsync(queueId, allowCompetingConsumers: true));
    }
}
