using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DaprMQ.Client.Exceptions;
using Moq;

namespace DaprMQ.Client.Tests;

public class QueueConsumerTests
{
    /// <summary>
    /// Stands in for one ConsumeAsync stream: the test pushes deliveries, and settlements are
    /// recorded. Like the real stream, cancelling the token ends it (OperationCanceledException), and
    /// a delivery settled after that throws StreamClosedException.
    /// </summary>
    private sealed class FakeStream
    {
        private readonly Channel<QueueDelivery> _deliveries = Channel.CreateUnbounded<QueueDelivery>();
        private volatile bool _closed;

        public readonly ConcurrentQueue<(string Action, string LockId)> Settled = new();
        public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Deliver(string lockId, int deliveryCount = 1) => _deliveries.Writer.TryWrite(new QueueDelivery
        {
            LockId = lockId,
            Item = JsonDocument.Parse("{\"n\":1}").RootElement,
            Priority = 1,
            LockExpiresAt = 0,
            DeliveryCount = deliveryCount,
            AckAsync = _ => Settle("ack", lockId),
            NackAsync = _ => Settle("nack", lockId),
            DeadLetterAsync = _ => Settle("deadletter", lockId)
        });

        /// <summary>The stream breaks, as when the gateway goes away.</summary>
        public void Break() => _deliveries.Writer.TryComplete(new IOException("stream broke"));

        private Task Settle(string action, string lockId)
        {
            if (_closed)
            {
                throw new StreamClosedException("closed");
            }
            Settled.Enqueue((action, lockId));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<QueueDelivery> Read([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                while (true)
                {
                    QueueDelivery delivery;
                    try
                    {
                        if (!await _deliveries.Reader.WaitToReadAsync(ct) || !_deliveries.Reader.TryRead(out delivery!))
                        {
                            yield break;
                        }
                    }
                    catch (ChannelClosedException e) when (e.InnerException != null)
                    {
                        throw e.InnerException;
                    }
                    yield return delivery;
                }
            }
            finally
            {
                _closed = true;
                Closed.TrySetResult();
            }
        }
    }

    private static (Mock<IDaprMQClient> client, List<ConsumeOptions> opened) ClientOver(params FakeStream[] streams)
    {
        var opened = new List<ConsumeOptions>();
        var next = 0;
        var client = new Mock<IDaprMQClient>();
        client
            .Setup(c => c.ConsumeAsync("q", It.IsAny<ConsumeOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, ConsumeOptions? options, CancellationToken ct) =>
            {
                lock (opened)
                {
                    opened.Add(options!);
                }
                var stream = streams[Math.Min(Interlocked.Increment(ref next) - 1, streams.Length - 1)];
                return stream.Read(ct);
            });
        return (client, opened);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(5);
        }
    }

    private static QueueConsumer Consumer(IDaprMQClient client, QueueConsumerOptions options, Func<QueueMessageContext, CancellationToken, Task> handler) =>
        new(client, "q", options, handler) { DelayAsync = (_, _) => Task.CompletedTask };

    [Fact]
    public async Task OpensTheStream_WithDefaultOptions()
    {
        var stream = new FakeStream();
        var (client, opened) = ClientOver(stream);
        var consumer = Consumer(client.Object, new QueueConsumerOptions(), (_, _) => Task.CompletedTask);

        await consumer.StartAsync();
        await WaitUntilAsync(() => opened.Count == 1);
        await consumer.StopAsync();

        Assert.Equal(100, opened[0].PrefetchCount);
        Assert.Equal(TimeSpan.FromSeconds(30), opened[0].LockTtl);
        Assert.True(opened[0].AllowCompetingConsumers);
    }

    [Fact]
    public async Task HandlerSuccess_Acks_AndTheContextCarriesTheDelivery()
    {
        var stream = new FakeStream();
        var (client, _) = ClientOver(stream);
        var contexts = new ConcurrentBag<QueueMessageContext>();
        var consumer = Consumer(client.Object, new QueueConsumerOptions(), (ctx, _) => { contexts.Add(ctx); return Task.CompletedTask; });

        await consumer.StartAsync();
        stream.Deliver("L1", deliveryCount: 3);
        await WaitUntilAsync(() => stream.Settled.Count == 1);
        await consumer.StopAsync();

        Assert.Equal(("ack", "L1"), Assert.Single(stream.Settled));
        var ctx = Assert.Single(contexts);
        Assert.Equal("q", ctx.QueueId);
        Assert.Equal("L1", ctx.LockId);
        Assert.Equal(3, ctx.DeliveryCount);
        Assert.Equal(1, ctx.Item.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task HandlerError_NacksByDefault()
    {
        var stream = new FakeStream();
        var (client, _) = ClientOver(stream);
        var consumer = Consumer(client.Object, new QueueConsumerOptions(), (_, _) => throw new InvalidOperationException("boom"));

        await consumer.StartAsync();
        stream.Deliver("L1");
        await WaitUntilAsync(() => stream.Settled.Count == 1);
        await consumer.StopAsync();

        Assert.Equal(("nack", "L1"), Assert.Single(stream.Settled));
    }

    [Fact]
    public async Task HandlerError_DeadLettersWhenConfigured()
    {
        var stream = new FakeStream();
        var (client, _) = ClientOver(stream);
        var consumer = Consumer(client.Object, new QueueConsumerOptions { OnHandlerError = QueueHandlerFailureAction.DeadLetter },
            (_, _) => throw new InvalidOperationException("boom"));

        await consumer.StartAsync();
        stream.Deliver("L1");
        await WaitUntilAsync(() => stream.Settled.Count == 1);
        await consumer.StopAsync();

        Assert.Equal(("deadletter", "L1"), Assert.Single(stream.Settled));
    }

    [Fact]
    public async Task HandlerErrors_PaceNacks_AtMaxRetriableErrorsPerSec()
    {
        var stream = new FakeStream();
        var (client, _) = ClientOver(stream);
        var paced = new ConcurrentQueue<TimeSpan>();
        var consumer = new QueueConsumer(client.Object, "q",
            new QueueConsumerOptions { MaxRetriableErrorsPerSec = 10, StrictOrder = true },
            (_, _) => throw new InvalidOperationException("boom"))
        {
            DelayAsync = (delay, _) => { paced.Enqueue(delay); return Task.CompletedTask; }
        };

        await consumer.StartAsync();
        stream.Deliver("L1");
        stream.Deliver("L2");
        stream.Deliver("L3");
        await WaitUntilAsync(() => stream.Settled.Count == 3);
        await consumer.StopAsync();

        // The first nack goes straight away; each later one waits for its own slot, 100 ms after the
        // previous one (the fake delay doesn't pass time, so the slots stack up).
        var waits = paced.ToList();
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0].TotalMilliseconds, 50, 100);
        Assert.InRange(waits[1].TotalMilliseconds, 150, 200);
    }

    [Fact]
    public async Task MaxConcurrentHandlers_IsNeverExceeded()
    {
        var stream = new FakeStream();
        var (client, _) = ClientOver(stream);
        var running = 0;
        var peak = 0;
        var consumer = Consumer(client.Object, new QueueConsumerOptions { MaxConcurrentHandlers = 2 }, async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
        });

        await consumer.StartAsync();
        for (var i = 0; i < 8; i++)
        {
            stream.Deliver($"L{i}");
        }
        await WaitUntilAsync(() => stream.Settled.Count == 8);
        await consumer.StopAsync();

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task StrictOrder_OpensAWindowOfOne_WithoutCompetingConsumers_AndHandlesOneAtATime()
    {
        var stream = new FakeStream();
        var (client, opened) = ClientOver(stream);
        var running = 0;
        var peak = 0;
        var consumer = Consumer(client.Object, new QueueConsumerOptions { StrictOrder = true }, async (_, _) =>
        {
            InterlockedMax(ref peak, Interlocked.Increment(ref running));
            await Task.Delay(10);
            Interlocked.Decrement(ref running);
        });

        await consumer.StartAsync();
        for (var i = 0; i < 4; i++)
        {
            stream.Deliver($"L{i}");
        }
        await WaitUntilAsync(() => stream.Settled.Count == 4);
        await consumer.StopAsync();

        Assert.Equal(1, opened[0].PrefetchCount);
        Assert.False(opened[0].AllowCompetingConsumers);
        Assert.Equal(1, peak);
        Assert.Equal(["L0", "L1", "L2", "L3"], stream.Settled.Select(s => s.LockId));
    }

    [Fact]
    public async Task Stop_LetsTheRunningHandlerFinishAndAck_BeforeClosingTheStream_AndStartsNoMore()
    {
        var stream = new FakeStream();
        var (client, _) = ClientOver(stream);
        var started = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource();
        var consumer = Consumer(client.Object, new QueueConsumerOptions { MaxConcurrentHandlers = 1 }, async (ctx, _) =>
        {
            started.Enqueue(ctx.LockId);
            await release.Task;
        });

        await consumer.StartAsync();
        stream.Deliver("L1");
        stream.Deliver("L2"); // prefetched; waits for the one handler slot
        await WaitUntilAsync(() => started.Count == 1);

        var stopping = consumer.StopAsync();
        await Task.Delay(50);
        Assert.False(stream.Closed.Task.IsCompleted, "the stream must stay open while a handler runs");
        release.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["L1"], started);
        Assert.Equal(("ack", "L1"), Assert.Single(stream.Settled));
        Assert.True(stream.Closed.Task.IsCompleted);
    }

    [Fact]
    public async Task Stop_CancelsHandlersOnceTheDrainTimeoutRunsOut()
    {
        var stream = new FakeStream();
        var (client, _) = ClientOver(stream);
        var cancelled = new TaskCompletionSource();
        var consumer = Consumer(client.Object, new QueueConsumerOptions { DrainTimeout = TimeSpan.FromMilliseconds(100) }, async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                cancelled.TrySetResult();
            }
        });

        await consumer.StartAsync();
        stream.Deliver("L1");
        await Task.Delay(50);
        await consumer.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancelled.Task.IsCompleted);
        Assert.Empty(stream.Settled); // left unsettled: the server returns it when the stream closes
    }

    [Fact]
    public async Task BrokenStream_Reopens_BackingOffUntilADeliveryResetsIt()
    {
        var broken1 = new FakeStream();
        var broken2 = new FakeStream();
        var delivering = new FakeStream();
        var broken3 = new FakeStream();
        var last = new FakeStream();
        broken1.Break();
        broken2.Break();
        delivering.Deliver("L1");
        var (client, opened) = ClientOver(broken1, broken2, delivering, broken3, last);
        var delays = new ConcurrentQueue<TimeSpan>();
        var consumer = new QueueConsumer(client.Object, "q",
            new QueueConsumerOptions { MinBackoffSeconds = 1, MaxBackoffSeconds = 60 },
            (_, _) => Task.CompletedTask)
        {
            DelayAsync = (delay, _) => { delays.Enqueue(delay); return Task.CompletedTask; }
        };

        await consumer.StartAsync();
        await WaitUntilAsync(() => delivering.Settled.Count == 1);
        delivering.Break();
        await WaitUntilAsync(() => opened.Count == 4);
        broken3.Break();
        await WaitUntilAsync(() => opened.Count == 5);
        await consumer.StopAsync();

        Assert.Equal([1, 2, 1, 2], delays.Select(d => d.TotalSeconds));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
