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
/// Unit tests for DaprMQGrpcService.ConsumeSession - the managed one-session-per-stream loop
/// (plan §2.4/§2.4.1, §6 step 8). No live gRPC transport is involved: FakeAsyncStreamReader/
/// FakeServerStreamWriter below are minimal hand-rolled test doubles for Grpc.Core.Api's
/// IAsyncStreamReader{T}/IServerStreamWriter{T} - there is no established precedent for testing
/// a streaming RPC elsewhere in this codebase (ConsumeSession is the first one), and pulling in
/// Grpc.Core.Testing would drag in the old native-libgrpc-backed test helpers for two interfaces
/// this project can trivially fake itself.
/// </summary>
public class DaprMQGrpcServiceConsumeSessionTests
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

        public IReadOnlyCollection<T> Written => _written;

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            _written.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
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

    private DaprMQGrpcService CreateService(
        IQueueActorInvoker queueActorInvoker,
        ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker) =>
        new(_mockLogger.Object, queueActorInvoker, sessionCoordinatorActorInvoker);

    private static ConsumeSessionRequest StartRequest(string queueId, string? sessionId = null, int leaseSeconds = 30, int prefetchCount = 5)
    {
        var start = new ConsumeSessionStart { QueueId = queueId, LeaseSeconds = leaseSeconds, PrefetchCount = prefetchCount };
        if (sessionId != null)
        {
            start.SessionId = sessionId;
        }

        return new ConsumeSessionRequest { Start = start };
    }

    [Fact]
    public async Task ConsumeSession_FirstMessageNotStart_WritesErrorAndReturns()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);

        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(new ConsumeSessionRequest { Ack = new ConsumeSessionAck { LockId = "lock-1" } });
        reader.Complete();

        await service.ConsumeSession(reader, writer, _mockContext.Object);

        var response = Assert.Single(writer.Written);
        Assert.Equal(ConsumeSessionResponse.PayloadOneofCase.Error, response.PayloadCase);
        mockSessionInvoker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConsumeSession_AcceptSessionFails_WritesErrorAndReturns()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = false, ErrorCode = "SESSION_NOT_FOUND", ErrorMessage = "not found" });

        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue", "missing"));
        reader.Complete();

        await service.ConsumeSession(reader, writer, _mockContext.Object);

        var response = Assert.Single(writer.Written);
        Assert.Equal(ConsumeSessionResponse.PayloadOneofCase.Error, response.PayloadCase);
        Assert.Equal("SESSION_NOT_FOUND", response.Error.ErrorCode);
        mockSessionInvoker.Verify(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
            It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConsumeSession_Success_AssignsSessionDeliversItemsAndAcksThenReleasesOnDisconnect()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });

        var dequeueCallCount = 0;
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                dequeueCallCount++;
                if (dequeueCallCount == 1)
                {
                    return new ActorModels.DequeueLockedResponse
                    {
                        Items = new List<ActorModels.DequeueLockedItem>
                        {
                            new() { ItemJson = "{\"a\":1}", Priority = 1, LockId = "lock-1", LockExpiresAt = 999 }
                        }
                    };
                }

                return new ActorModels.DequeueLockedResponse { IsEmpty = true };
            });

        ActorModels.AcknowledgeRequest? capturedAck = null;
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                It.IsAny<ActorId>(), "Acknowledge", It.IsAny<ActorModels.AcknowledgeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.AcknowledgeRequest, CancellationToken>((_, _, req, _) => capturedAck = req)
            .ReturnsAsync(new ActorModels.AcknowledgeResponse { Success = true, ItemsAcknowledged = 1 });

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });

        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue", "s1"));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);

        await WaitUntilAsync(() => writer.Written.Any(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Delivered));

        reader.Add(new ConsumeSessionRequest { Ack = new ConsumeSessionAck { LockId = "lock-1" } });
        await WaitUntilAsync(() => capturedAck != null);

        reader.Complete();
        await task;

        Assert.Contains(writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionAssigned && r.SessionAssigned.SessionId == "s1");
        var delivered = Assert.Single(writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Delivered);
        Assert.Equal("lock-1", delivered.Delivered.LockId);
        Assert.Equal("lease-1", capturedAck!.LeaseId);

        mockSessionInvoker.Verify(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
            It.IsAny<ActorId>(),
            "ReleaseSession",
            It.Is<ActorModels.ReleaseSessionRequest>(r => r.SessionId == "s1" && r.LeaseId == "lease-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeSession_DeadLetterFrame_InvokesDeadLetterOnSessionActor()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 1 });

        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse { IsEmpty = true });

        ActorModels.DeadLetterRequest? capturedDeadLetter = null;
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                It.IsAny<ActorId>(), "DeadLetter", It.IsAny<ActorModels.DeadLetterRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.DeadLetterRequest, CancellationToken>((_, _, req, _) => capturedDeadLetter = req)
            .ReturnsAsync(new ActorModels.DeadLetterResponse { Status = "SUCCESS", DlqId = "dlq-1" });

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });

        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue", "s1"));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);

        await WaitUntilAsync(() => writer.Written.Any(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionAssigned));

        reader.Add(new ConsumeSessionRequest { DeadLetter = new ConsumeSessionDeadLetter { LockId = "lock-9" } });
        await WaitUntilAsync(() => capturedDeadLetter != null);

        reader.Complete();
        await task;

        Assert.Equal("lock-9", capturedDeadLetter!.LockId);
        Assert.Equal("lease-1", capturedDeadLetter.LeaseId);
    }

    [Fact]
    public async Task ConsumeSession_RenewalFails_WritesSessionLostAndReleases()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 1 });

        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse { IsEmpty = true });

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.RenewSessionLeaseRequest, ActorModels.RenewSessionLeaseResponse>(
                It.IsAny<ActorId>(), "RenewSessionLease", It.IsAny<ActorModels.RenewSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.RenewSessionLeaseResponse { Success = false, ErrorCode = "SESSION_ACTOR_UNAVAILABLE", ErrorMessage = "unreachable" });

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });

        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        // 2-second lease -> ~1-second renewal interval, so the loop hits it quickly.
        reader.Add(StartRequest("test-queue", "s1", leaseSeconds: 2, prefetchCount: 5));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);

        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionLost);
        mockSessionInvoker.Verify(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
            It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
