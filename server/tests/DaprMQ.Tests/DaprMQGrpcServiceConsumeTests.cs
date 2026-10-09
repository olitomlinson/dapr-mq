using System.Collections.Concurrent;
using System.Threading.Channels;
using Dapr.Actors;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Services;
using DaprMQ.ApiServer.Grpc;
using DaprMQ.Interfaces;
using ActorModels = DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for DaprMQGrpcService.Consume - the managed consume stream for plain queues. Same
/// hand-rolled stream doubles as DaprMQGrpcServiceConsumeSessionTests.
/// </summary>
public class DaprMQGrpcServiceConsumeTests
{
    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T> where T : class
    {
        private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();

        public T Current { get; private set; } = null!;

        public void Add(T item) => _channel.Writer.TryWrite(item);

        public void Complete() => _channel.Writer.TryComplete();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                if (_channel.Reader.TryRead(out var item))
                {
                    Current = item;
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly ConcurrentQueue<T> _written = new();
        private int _writing;

        public IReadOnlyCollection<T> Written => _written;

        /// <summary>
        /// Set if two writes ever overlapped - gRPC forbids concurrent writes on one stream.
        /// </summary>
        public bool ConcurrentWriteSeen { get; private set; }

        public WriteOptions? WriteOptions { get; set; }

        public async Task WriteAsync(T message)
        {
            if (Interlocked.Increment(ref _writing) > 1)
            {
                ConcurrentWriteSeen = true;
            }

            await Task.Yield();
            _written.Enqueue(message);
            Interlocked.Decrement(ref _writing);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout");
            }

            await Task.Delay(10);
        }
    }

    private readonly Mock<ILogger<DaprMQGrpcService>> _mockLogger = new();
    private readonly Mock<ServerCallContext> _mockContext = new();
    private readonly Mock<IQueueActorInvoker> _queue = new();
    private readonly ConcurrentQueue<DateTime> _dequeueTimes = new();

    private DaprMQGrpcService CreateService() =>
        new(_mockLogger.Object, _queue.Object, new Mock<ISessionCoordinatorActorInvoker>().Object)
        {
            PollInterval = TimeSpan.FromMilliseconds(20)
        };

    private static ConsumeRequest Start(int prefetchCount = 5, int lockTtlSeconds = 30, bool competing = true) =>
        new() { Start = new ConsumeStart { QueueId = "q", PrefetchCount = prefetchCount, LockTtlSeconds = lockTtlSeconds, AllowCompetingConsumers = competing } };

    private static ActorModels.DequeueLockedItem Item(string lockId, int deliveryCount = 0) =>
        new() { ItemJson = $"{{\"id\":\"{lockId}\"}}", Priority = 1, LockId = lockId, LockExpiresAt = 999, DeliveryCount = deliveryCount };

    /// <summary>
    /// DequeueLocked answers with the given batches in turn, then an empty queue. Records every request.
    /// </summary>
    private ConcurrentQueue<ActorModels.DequeueLockedRequest> SetupDequeue(params ActorModels.DequeueLockedItem[][] batches)
    {
        var requests = new ConcurrentQueue<ActorModels.DequeueLockedRequest>();
        var call = 0;
        _queue.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.DequeueLockedRequest, CancellationToken>((_, _, req, _) =>
            {
                requests.Enqueue(req);
                _dequeueTimes.Enqueue(DateTime.UtcNow);
            })
            .ReturnsAsync(() =>
            {
                var n = Interlocked.Increment(ref call) - 1;
                return n < batches.Length
                    ? new ActorModels.DequeueLockedResponse { Items = batches[n].ToList() }
                    : new ActorModels.DequeueLockedResponse { IsEmpty = true };
            });
        return requests;
    }

    /// <summary>
    /// Acks reach the actor through AcknowledgeBatch. Records every acked lock id and every batch.
    /// </summary>
    private ConcurrentQueue<string> SetupAck(bool success = true, ConcurrentQueue<List<string>>? batches = null)
    {
        var acked = new ConcurrentQueue<string>();
        _queue.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeBatchRequest, ActorModels.AcknowledgeBatchResponse>(
                It.IsAny<ActorId>(), "AcknowledgeBatch", It.IsAny<ActorModels.AcknowledgeBatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.AcknowledgeBatchRequest, CancellationToken>((_, _, req, _) =>
            {
                batches?.Enqueue(req.LockIds.ToList());
                foreach (var id in req.LockIds)
                {
                    acked.Enqueue(id);
                }
            })
            .ReturnsAsync((ActorId _, string _, ActorModels.AcknowledgeBatchRequest req, CancellationToken _) => new ActorModels.AcknowledgeBatchResponse
            {
                Success = true,
                ItemsAcknowledged = success ? req.LockIds.Count : 0,
                Results = req.LockIds.Select(id => new ActorModels.AcknowledgeResult { LockId = id, Outcome = success ? "ACKNOWLEDGED" : "LOCK_NOT_FOUND" }).ToList()
            });
        return acked;
    }

    private ConcurrentQueue<string> SetupNack()
    {
        var nacked = new ConcurrentQueue<string>();
        _queue.Setup(i => i.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                It.IsAny<ActorId>(), "Nack", It.IsAny<ActorModels.NackRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.NackRequest, CancellationToken>((_, _, req, _) => nacked.Enqueue(req.LockId))
            .ReturnsAsync(new ActorModels.NackResponse { Success = true, DeliveryCount = 1 });
        return nacked;
    }

    private ConcurrentQueue<ActorModels.ExtendLockBatchRequest> SetupExtend()
    {
        var extends = new ConcurrentQueue<ActorModels.ExtendLockBatchRequest>();
        _queue.Setup(i => i.InvokeMethodAsync<ActorModels.ExtendLockBatchRequest, ActorModels.ExtendLockBatchResponse>(
                It.IsAny<ActorId>(), "ExtendLockBatch", It.IsAny<ActorModels.ExtendLockBatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.ExtendLockBatchRequest, CancellationToken>((_, _, req, _) => extends.Enqueue(req))
            .ReturnsAsync((ActorId _, string _, ActorModels.ExtendLockBatchRequest req, CancellationToken _) => new ActorModels.ExtendLockBatchResponse
            {
                Success = true,
                Results = req.LockIds.Select(id => new ActorModels.ExtendLockResult { LockId = id, Outcome = "EXTENDED" }).ToList()
            });
        return extends;
    }

    private IEnumerable<ConsumeDelivered> Delivered(FakeServerStreamWriter<ConsumeResponse> writer) =>
        writer.Written.Where(r => r.PayloadCase == ConsumeResponse.PayloadOneofCase.Delivered).Select(r => r.Delivered);

    [Fact]
    public async Task Consume_FirstMessageNotStart_WritesErrorAndReturns()
    {
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = "lock-1" } });
        reader.Complete();

        await CreateService().Consume(reader, writer, _mockContext.Object);

        var response = Assert.Single(writer.Written);
        Assert.Equal(ConsumeResponse.PayloadOneofCase.Error, response.PayloadCase);
        Assert.Equal("INVALID_ARGUMENT", response.Error.ErrorCode);
        _queue.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Consume_MissingQueueId_WritesErrorAndReturns()
    {
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(new ConsumeRequest { Start = new ConsumeStart { PrefetchCount = 1 } });
        reader.Complete();

        await CreateService().Consume(reader, writer, _mockContext.Object);

        var response = Assert.Single(writer.Written);
        Assert.Equal("INVALID_ARGUMENT", response.Error.ErrorCode);
    }

    [Fact]
    public async Task Consume_DeliversItemsWithTheStartSettingsAndOneBasedDeliveryCounts()
    {
        var requests = SetupDequeue([Item("lock-1"), Item("lock-2", deliveryCount: 2)]);
        SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start(prefetchCount: 5, lockTtlSeconds: 45, competing: true));

        var task = CreateService().Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => Delivered(writer).Count() == 2);
        reader.Complete();
        await task;

        Assert.True(requests.TryPeek(out var first));
        Assert.Equal(5, first!.Count);
        Assert.Equal(45, first.TtlSeconds);
        Assert.True(first.AllowCompetingConsumers);
        var delivered = Delivered(writer).ToList();
        Assert.Equal(new[] { "lock-1", "lock-2" }, delivered.Select(d => d.LockId));
        Assert.Equal(new[] { 1, 3 }, delivered.Select(d => d.DeliveryCount));
    }

    [Fact]
    public async Task Consume_FullWindowWaitsForSettlesThenAsksOnlyForTheFreeSlots()
    {
        var requests = SetupDequeue([Item("lock-1"), Item("lock-2")], [Item("lock-3")]);
        var acked = SetupAck();
        SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start(prefetchCount: 2));

        var task = CreateService().Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => Delivered(writer).Count() == 2);
        await Task.Delay(100);
        Assert.Single(requests);

        reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = "lock-1" } });
        await WaitUntilAsync(() => Delivered(writer).Count() == 3);
        reader.Complete();
        await task;

        Assert.Equal(new[] { "lock-1" }, acked);
        Assert.Equal(1, requests.ElementAt(1).Count);
    }

    [Fact]
    public async Task Consume_RejectedSettleWritesSettleFailedAndTheStreamCarriesOn()
    {
        SetupDequeue([Item("lock-1")], [Item("lock-2")]);
        SetupAck(success: false);
        SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start(prefetchCount: 1));

        var task = CreateService().Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => Delivered(writer).Count() == 1);
        reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = "lock-1" } });
        await WaitUntilAsync(() => Delivered(writer).Count() == 2);
        reader.Complete();
        await task;

        var failed = Assert.Single(writer.Written, r => r.PayloadCase == ConsumeResponse.PayloadOneofCase.SettleFailed).SettleFailed;
        Assert.Equal("lock-1", failed.LockId);
        Assert.Equal("LOCK_NOT_FOUND", failed.ErrorCode);
        Assert.DoesNotContain(writer.Written, r => r.PayloadCase == ConsumeResponse.PayloadOneofCase.Error);
    }

    [Fact]
    public async Task Consume_RenewsOutstandingLocksButNotSettledOnes()
    {
        SetupDequeue([Item("lock-1"), Item("lock-2")]);
        SetupAck();
        SetupNack();
        var extends = SetupExtend();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start(prefetchCount: 2, lockTtlSeconds: 2));

        var task = CreateService().Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => Delivered(writer).Count() == 2);
        reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = "lock-1" } });
        await WaitUntilAsync(() => !extends.IsEmpty);
        reader.Complete();
        await task;

        Assert.True(extends.TryPeek(out var renewal));
        Assert.Equal(new[] { "lock-2" }, renewal!.LockIds);
        Assert.Equal(2, renewal.TtlSeconds);
    }

    [Fact]
    public async Task Consume_StreamEnd_NacksEveryOutstandingItem()
    {
        SetupDequeue([Item("lock-1"), Item("lock-2"), Item("lock-3")]);
        var acked = SetupAck();
        var nacked = SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start(prefetchCount: 3));

        var task = CreateService().Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => Delivered(writer).Count() == 3);
        reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = "lock-2" } });
        await WaitUntilAsync(() => acked.Count == 1);
        reader.Complete();
        await task;

        Assert.Equal(new[] { "lock-1", "lock-3" }, nacked.OrderBy(id => id));
    }

    [Fact]
    public async Task Consume_EmptyQueue_BacksOffThePollUpToTheIdleCap()
    {
        var requests = SetupDequeue();
        SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start());
        var service = CreateService();
        service.MaxIdlePollInterval = TimeSpan.FromMilliseconds(160);

        var task = service.Consume(reader, writer, _mockContext.Object);
        await Task.Delay(1000);
        reader.Complete();
        await task;

        // 20, 40, 80, then 160 ms between polls: about 9 in a second, against about 50 unbacked-off.
        Assert.InRange(requests.Count, 5, 15);
    }

    [Fact]
    public async Task Consume_ADeliveryResetsTheIdleBackoff()
    {
        SetupDequeue([], [], [], [], [], [], [Item("lock-1")]);
        SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start());
        var service = CreateService();
        service.MaxIdlePollInterval = TimeSpan.FromMilliseconds(160);

        var task = service.Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => _dequeueTimes.Count >= 9);
        reader.Complete();
        await task;

        var t = _dequeueTimes.ToArray();
        // Calls 0-5 come back empty, backing off to the cap; call 6 delivers; call 7 asks straight
        // away for the free slots and is empty, so call 8 waits the initial interval again.
        Assert.True(t[6] - t[5] >= TimeSpan.FromMilliseconds(120), $"backed off before the delivery: {(t[6] - t[5]).TotalMilliseconds} ms");
        Assert.True(t[8] - t[7] < TimeSpan.FromMilliseconds(100), $"reset after the delivery: {(t[8] - t[7]).TotalMilliseconds} ms");
    }

    [Fact]
    public async Task Consume_NeverWritesConcurrently()
    {
        SetupDequeue([Item("lock-1"), Item("lock-2"), Item("lock-3"), Item("lock-4")], [Item("lock-5"), Item("lock-6")]);
        SetupAck(success: false);
        SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start(prefetchCount: 4));

        var task = CreateService().Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => Delivered(writer).Count() == 4);
        foreach (var id in new[] { "lock-1", "lock-2", "lock-3", "lock-4" })
        {
            reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = id } });
        }
        await WaitUntilAsync(() => Delivered(writer).Count() == 6);
        reader.Complete();
        await task;

        Assert.False(writer.ConcurrentWriteSeen);
    }

    /// <summary>
    /// Settling one ack per actor call capped a stream at roughly 1 / (one Acknowledge round trip)
    /// messages a second. Acks already waiting are settled together in one AcknowledgeBatch.
    /// </summary>
    [Fact]
    public async Task Consume_AcksWaitingTogetherAreSettledInOneBatch()
    {
        SetupDequeue([Item("lock-1"), Item("lock-2"), Item("lock-3"), Item("lock-4")]);
        var batches = new ConcurrentQueue<List<string>>();
        var gate = new TaskCompletionSource();
        var acked = new ConcurrentQueue<string>();
        _queue.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeBatchRequest, ActorModels.AcknowledgeBatchResponse>(
                It.IsAny<ActorId>(), "AcknowledgeBatch", It.IsAny<ActorModels.AcknowledgeBatchRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (ActorId _, string _, ActorModels.AcknowledgeBatchRequest req, CancellationToken _) =>
            {
                batches.Enqueue(req.LockIds.ToList());
                // Hold the first batch, so the acks sent meanwhile pile up behind it.
                await gate.Task;
                foreach (var id in req.LockIds)
                {
                    acked.Enqueue(id);
                }
                return new ActorModels.AcknowledgeBatchResponse
                {
                    Success = true,
                    Results = req.LockIds.Select(id => new ActorModels.AcknowledgeResult { LockId = id, Outcome = "ACKNOWLEDGED" }).ToList()
                };
            });
        SetupNack();
        var reader = new FakeAsyncStreamReader<ConsumeRequest>();
        var writer = new FakeServerStreamWriter<ConsumeResponse>();
        reader.Add(Start(prefetchCount: 4));

        var task = CreateService().Consume(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => Delivered(writer).Count() == 4);
        reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = "lock-1" } });
        await WaitUntilAsync(() => batches.Count == 1);
        foreach (var id in new[] { "lock-2", "lock-3", "lock-4" })
        {
            reader.Add(new ConsumeRequest { Ack = new ConsumeAck { LockId = id } });
        }
        await Task.Delay(100);
        gate.SetResult();
        await WaitUntilAsync(() => acked.Count == 4);
        reader.Complete();
        await task;

        Assert.Equal(new[] { new List<string> { "lock-1" }, new List<string> { "lock-2", "lock-3", "lock-4" } }, batches);
        _queue.Verify(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
            It.IsAny<ActorId>(), "Acknowledge", It.IsAny<ActorModels.AcknowledgeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
