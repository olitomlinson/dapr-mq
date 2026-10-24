using System.Collections.Concurrent;
using System.Threading.Channels;
using Dapr.Actors;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
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
        ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker,
        SessionReleaseHandoff? releaseHandoff = null) =>
        new(_mockLogger.Object, queueActorInvoker, sessionCoordinatorActorInvoker, releaseHandoff);

    private static SessionReleaseHandoff CreateHandoff(ISessionCoordinatorActorInvoker invoker) =>
        new(invoker, TimeSpan.FromSeconds(30), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionReleaseHandoff>.Instance);

    /// <summary>Every session reads as empty, so a stream with a 1s idle timeout drains.</summary>
    private static (Mock<IQueueActorInvoker> queue, Mock<ISessionCoordinatorActorInvoker> sessions) DrainingMocks()
    {
        var queue = new Mock<IQueueActorInvoker>();
        queue.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse { IsEmpty = true });
        var sessions = new Mock<ISessionCoordinatorActorInvoker>();
        sessions.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });
        sessions.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });
        return (queue, sessions);
    }

    private async Task DrainOneStreamAsync(DaprMQGrpcService service, ConsumeSessionRequest start)
    {
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(start);
        await service.ConsumeSession(reader, writer, _mockContext.Object).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionDrained);
    }

    private static void VerifyReleased(Mock<ISessionCoordinatorActorInvoker> sessions, string sessionId, Func<Times> times) =>
        sessions.Verify(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
            It.IsAny<ActorId>(), "ReleaseSession", It.Is<ActorModels.ReleaseSessionRequest>(r => r.SessionId == sessionId), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task ConsumeSession_AnyAvailableDrained_NextStreamReleasesAndAcceptsInOneCall()
    {
        var (queue, sessions) = DrainingMocks();
        sessions.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseAndAcceptSessionRequest, ActorModels.ReleaseAndAcceptSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseAndAcceptSession", It.IsAny<ActorModels.ReleaseAndAcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseAndAcceptSessionResponse
            {
                Release = new ActorModels.ReleaseSessionResponse { Success = true },
                Accept = new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s2", LeaseId = "lease-2", LeaseExpiresAt = 12345 }
            });
        var service = CreateService(queue.Object, sessions.Object, CreateHandoff(sessions.Object));

        await DrainOneStreamAsync(service, StartRequest("test-queue", leaseSeconds: 40, sessionIdleTimeoutSeconds: 1));
        VerifyReleased(sessions, "s1", Times.Never);

        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue", leaseSeconds: 40));
        var second = service.ConsumeSession(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => writer.Written.Any(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionAssigned));

        Assert.Equal("s2", writer.Written.First().SessionAssigned.SessionId);
        sessions.Verify(i => i.InvokeMethodAsync<ActorModels.ReleaseAndAcceptSessionRequest, ActorModels.ReleaseAndAcceptSessionResponse>(
            It.Is<ActorId>(a => a.GetId() == "test-queue"), "ReleaseAndAcceptSession",
            It.Is<ActorModels.ReleaseAndAcceptSessionRequest>(r => r.SessionId == "s1" && r.LeaseId == "lease-1" && r.LeaseSeconds == 40),
            It.IsAny<CancellationToken>()), Times.Once);
        sessions.Verify(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
            It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        VerifyReleased(sessions, "s1", Times.Never);

        reader.Complete();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConsumeSession_TargetedDrained_ReleasesImmediately()
    {
        var (queue, sessions) = DrainingMocks();
        var handoff = CreateHandoff(sessions.Object);
        var service = CreateService(queue.Object, sessions.Object, handoff);

        await DrainOneStreamAsync(service, StartRequest("test-queue", "s1", sessionIdleTimeoutSeconds: 1));

        VerifyReleased(sessions, "s1", Times.Once);
        Assert.Null(handoff.TryTake("test-queue"));
    }

    [Fact]
    public async Task ConsumeSession_AnyAvailableEndedByClient_ReleasesImmediately()
    {
        var (queue, sessions) = DrainingMocks();
        var handoff = CreateHandoff(sessions.Object);
        var service = CreateService(queue.Object, sessions.Object, handoff);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue"));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => writer.Written.Any(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionAssigned));
        reader.Complete();
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        VerifyReleased(sessions, "s1", Times.Once);
        Assert.Null(handoff.TryTake("test-queue"));
    }

    [Fact]
    public async Task ConsumeSession_ReleaseAndAcceptFails_ReleasesTheTakenLease()
    {
        var (queue, sessions) = DrainingMocks();
        sessions.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseAndAcceptSessionRequest, ActorModels.ReleaseAndAcceptSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseAndAcceptSession", It.IsAny<ActorModels.ReleaseAndAcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ActorCallException(DeliveryOutcome.Unknown, new TimeoutException()));
        var service = CreateService(queue.Object, sessions.Object, CreateHandoff(sessions.Object));
        await DrainOneStreamAsync(service, StartRequest("test-queue", sessionIdleTimeoutSeconds: 1));

        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue"));
        await Assert.ThrowsAsync<ActorCallException>(() => service.ConsumeSession(reader, writer, _mockContext.Object));

        VerifyReleased(sessions, "s1", Times.Once);
    }

    private static ConsumeSessionRequest StartRequest(
        string queueId, string? sessionId = null, int leaseSeconds = 30, int prefetchCount = 5, int sessionIdleTimeoutSeconds = 0)
    {
        var start = new ConsumeSessionStart
        {
            QueueId = queueId,
            LeaseSeconds = leaseSeconds,
            PrefetchCount = prefetchCount,
            SessionIdleTimeoutSeconds = sessionIdleTimeoutSeconds
        };
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

    /// <summary>
    /// docs/issues/resolved/session-stream-acks-lost-on-disconnect.md: once the server has read an Ack frame,
    /// the client going away (cancelling the call, or the SDK disposing it after a break) must not
    /// abandon that ack - the client was told it succeeded. The fake Acknowledge honours its
    /// cancellation token the way a real actor invocation does.
    /// </summary>
    [Fact]
    public async Task ConsumeSession_ClientCancelsWhileAckInFlight_AckStillApplied()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });

        var dequeueCallCount = 0;
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref dequeueCallCount) == 1
                ? new ActorModels.DequeueLockedResponse
                {
                    Items = new List<ActorModels.DequeueLockedItem>
                    {
                        new() { ItemJson = "{\"a\":1}", Priority = 1, LockId = "lock-1", LockExpiresAt = 999 }
                    }
                }
                : new ActorModels.DequeueLockedResponse { IsEmpty = true });

        var ackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actorFinishesAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackApplied = false;
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                It.IsAny<ActorId>(), "Acknowledge", It.IsAny<ActorModels.AcknowledgeRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ActorId, string, ActorModels.AcknowledgeRequest, CancellationToken>(async (_, _, _, token) =>
            {
                ackStarted.TrySetResult();
                await actorFinishesAck.Task.WaitAsync(token);
                ackApplied = true;
                return new ActorModels.AcknowledgeResponse { Success = true, ItemsAcknowledged = 1 };
            });

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });

        using var clientCall = new CancellationTokenSource();
        _mockContext.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(clientCall.Token);

        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue", "s1"));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => writer.Written.Any(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Delivered));

        reader.Add(new ConsumeSessionRequest { Ack = new ConsumeSessionAck { LockId = "lock-1" } });
        await ackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The consumer stops right after acking: the call is cancelled while the ack is in flight.
        clientCall.Cancel();
        actorFinishesAck.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ackApplied, "An Ack frame the server had already read was abandoned when the client cancelled the call");
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
    public async Task ConsumeSession_NackFrame_InvokesNackOnSessionActor()
    {
        var h = new RefillHarness(firstBatchSize: 1);
        ActorModels.NackRequest? capturedNack = null;
        h.QueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                It.IsAny<ActorId>(), "Nack", It.IsAny<ActorModels.NackRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.NackRequest, CancellationToken>((_, _, req, _) => capturedNack = req)
            .ReturnsAsync(new ActorModels.NackResponse { Success = true, DeliveryCount = 1 });

        var service = CreateService(h.QueueInvoker.Object, h.SessionInvoker.Object);
        h.Reader.Add(StartRequest("test-queue", "s1"));
        var task = service.ConsumeSession(h.Reader, h.Writer, _mockContext.Object);
        await WaitUntilAsync(() => h.DeliveredCount == 1);

        h.Nack(1, 1);
        await WaitUntilAsync(() => capturedNack != null);

        await StopAsync(h, task);

        Assert.Equal("lock-1", capturedNack!.LockId);
        Assert.Equal("lease-1", capturedNack.LeaseId);
        Assert.DoesNotContain(h.Writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Error);
    }

    [Fact]
    public async Task ConsumeSession_RejectedNack_WritesSessionError()
    {
        var h = new RefillHarness(firstBatchSize: 1);
        h.QueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                It.IsAny<ActorId>(), "Nack", It.IsAny<ActorModels.NackRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.NackResponse { Success = false, ErrorCode = "LOCK_NOT_FOUND", Message = "Lock not found" });

        var service = CreateService(h.QueueInvoker.Object, h.SessionInvoker.Object);
        h.Reader.Add(StartRequest("test-queue", "s1"));
        var task = service.ConsumeSession(h.Reader, h.Writer, _mockContext.Object);
        await WaitUntilAsync(() => h.DeliveredCount == 1);

        h.Nack(1, 1);
        await WaitUntilAsync(() => h.Writer.Written.Any(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Error));

        await StopAsync(h, task);

        var error = h.Writer.Written.First(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Error).Error;
        Assert.Equal("LOCK_NOT_FOUND", error.ErrorCode);
    }

    [Fact]
    public async Task ConsumeSession_IdleTimeoutElapsed_WritesSessionDrainedAndReleases()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });

        // Never returns anything - the session is idle for the life of the test.
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse { IsEmpty = true });

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });

        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        // Long lease (so renewal never fires) but a short idle timeout.
        reader.Add(StartRequest("test-queue", "s1", leaseSeconds: 30, prefetchCount: 5, sessionIdleTimeoutSeconds: 1));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);

        await task.WaitAsync(TimeSpan.FromSeconds(5));

        var drained = Assert.Single(writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionDrained);
        Assert.Equal("s1", drained.SessionDrained.SessionId);
        Assert.DoesNotContain(writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionLost
            || r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Error);
        mockSessionInvoker.Verify(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
            It.IsAny<ActorId>(),
            "ReleaseSession",
            It.Is<ActorModels.ReleaseSessionRequest>(r => r.SessionId == "s1" && r.LeaseId == "lease-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeSession_ItemsKeepArriving_NeverIdleDrains()
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
                return new ActorModels.DequeueLockedResponse
                {
                    Items = new List<ActorModels.DequeueLockedItem>
                    {
                        new() { ItemJson = "{}", Priority = 1, LockId = $"lock-{dequeueCallCount}", LockExpiresAt = 999 }
                    }
                };
            });

        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        var writer = new FakeServerStreamWriter<ConsumeSessionResponse>();
        reader.Add(StartRequest("test-queue", "s1", leaseSeconds: 30, prefetchCount: 1000, sessionIdleTimeoutSeconds: 1));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);

        // Items keep arriving well past the 1s idle timeout - it should never trigger.
        await Task.Delay(1500);
        reader.Complete();
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(writer.Written, r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionDrained);
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

    /// <summary>Records every write, and the most writes it ever saw running at once.</summary>
    private sealed class OverlapDetectingWriter<T> : IServerStreamWriter<T>
    {
        private readonly ConcurrentQueue<T> _written = new();
        private int _active;
        private int _maxConcurrent;

        public IReadOnlyCollection<T> Written => _written;
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public Action<T>? OnWrite { get; init; }
        public WriteOptions? WriteOptions { get; set; }

        public async Task WriteAsync(T message)
        {
            var active = Interlocked.Increment(ref _active);
            int seen;
            while (active > (seen = Volatile.Read(ref _maxConcurrent)) && Interlocked.CompareExchange(ref _maxConcurrent, active, seen) != seen)
            {
            }

            try
            {
                _written.Enqueue(message);
                OnWrite?.Invoke(message);
                await Task.Delay(20); // a write that takes a while, as a slow client's would
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static void SetupAccepted(Mock<ISessionCoordinatorActorInvoker> sessionInvoker) =>
        sessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });

    /// <summary>
    /// gRPC forbids overlapping writes on one stream. The reader writes settle errors while the
    /// delivery loop writes deliveries, so every write goes through one lock.
    /// </summary>
    [Fact]
    public async Task ConsumeSession_SettleErrorsDuringDeliveries_WritesNeverOverlap()
    {
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockSessionInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        SetupAccepted(mockSessionInvoker);

        var next = 0;
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ActorModels.DequeueLockedResponse
            {
                Items = [new() { ItemJson = "{}", Priority = 1, LockId = $"lock-{Interlocked.Increment(ref next)}", LockExpiresAt = 999 }]
            });

        // Every ack is rejected, so the reader writes an error frame for each one.
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                It.IsAny<ActorId>(), "Acknowledge", It.IsAny<ActorModels.AcknowledgeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcknowledgeResponse { Success = false, ErrorCode = "LOCK_EXPIRED", Message = "expired" });

        mockSessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });

        var reader = new FakeAsyncStreamReader<ConsumeSessionRequest>();
        // The client acks each delivery as soon as it arrives.
        var writer = new OverlapDetectingWriter<ConsumeSessionResponse>
        {
            OnWrite = r =>
            {
                if (r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Delivered)
                {
                    reader.Add(new ConsumeSessionRequest { Ack = new ConsumeSessionAck { LockId = r.Delivered.LockId } });
                }
            }
        };
        var service = CreateService(mockQueueInvoker.Object, mockSessionInvoker.Object);
        reader.Add(StartRequest("test-queue", "s1", prefetchCount: 2));

        var task = service.ConsumeSession(reader, writer, _mockContext.Object);
        await WaitUntilAsync(() => writer.Written.Count(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Error) >= 10, 5000);

        reader.Complete();
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, writer.MaxConcurrent);
    }

    // --- Prefetch refill: acks that half-drain the window wake the poll loop early ---

    private sealed class RefillHarness
    {
        public readonly Mock<IQueueActorInvoker> QueueInvoker = new();
        public readonly Mock<ISessionCoordinatorActorInvoker> SessionInvoker = new();
        public readonly ConcurrentQueue<ActorModels.DequeueLockedRequest> DequeueRequests = new();
        public readonly FakeAsyncStreamReader<ConsumeSessionRequest> Reader = new();
        public readonly FakeServerStreamWriter<ConsumeSessionResponse> Writer = new();

        public RefillHarness(int firstBatchSize)
        {
            SessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                    It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });

            // First call fills the window; every later call finds the queue empty.
            QueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                    It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ActorId _, string _, ActorModels.DequeueLockedRequest req, CancellationToken _) =>
                {
                    DequeueRequests.Enqueue(req);
                    if (DequeueRequests.Count == 1)
                    {
                        return new ActorModels.DequeueLockedResponse
                        {
                            Items = Enumerable.Range(1, firstBatchSize)
                                .Select(n => new ActorModels.DequeueLockedItem { ItemJson = "{}", Priority = 1, LockId = $"lock-{n}", LockExpiresAt = 999 })
                                .ToList()
                        };
                    }

                    return new ActorModels.DequeueLockedResponse { IsEmpty = true };
                });

            QueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                    It.IsAny<ActorId>(), "Acknowledge", It.IsAny<ActorModels.AcknowledgeRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ActorModels.AcknowledgeResponse { Success = true, ItemsAcknowledged = 1 });

            QueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                    It.IsAny<ActorId>(), "DeadLetter", It.IsAny<ActorModels.DeadLetterRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ActorModels.DeadLetterResponse { Status = "SUCCESS", DlqId = "dlq-1" });

            SessionInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                    It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });
        }

        public int DeliveredCount => Writer.Written.Count(r => r.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Delivered);

        public void Ack(int from, int to)
        {
            for (var n = from; n <= to; n++)
            {
                Reader.Add(new ConsumeSessionRequest { Ack = new ConsumeSessionAck { LockId = $"lock-{n}" } });
            }
        }

        public void Nack(int from, int to)
        {
            for (var n = from; n <= to; n++)
            {
                Reader.Add(new ConsumeSessionRequest { Nack = new ConsumeSessionNack { LockId = $"lock-{n}" } });
            }
        }

        public void DeadLetter(int from, int to)
        {
            for (var n = from; n <= to; n++)
            {
                Reader.Add(new ConsumeSessionRequest { DeadLetter = new ConsumeSessionDeadLetter { LockId = $"lock-{n}" } });
            }
        }
    }

    // Long enough that only the refill signal can explain an early second DequeueLocked.
    private static readonly TimeSpan LongPollInterval = TimeSpan.FromSeconds(5);

    private async Task<(RefillHarness Harness, Task Consume)> StartRefillSessionAsync(int prefetchCount)
    {
        var h = new RefillHarness(prefetchCount);
        var service = CreateService(h.QueueInvoker.Object, h.SessionInvoker.Object);
        service.PollInterval = LongPollInterval;
        h.Reader.Add(StartRequest("test-queue", "s1", leaseSeconds: 30, prefetchCount: prefetchCount));

        var task = service.ConsumeSession(h.Reader, h.Writer, _mockContext.Object);
        await WaitUntilAsync(() => h.DeliveredCount == prefetchCount);
        return (h, task);
    }

    private static async Task StopAsync(RefillHarness h, Task consume)
    {
        h.Reader.Complete();
        await consume.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ConsumeSession_AcksHalfDrainWindow_RefillsBeforePollInterval()
    {
        var (h, task) = await StartRefillSessionAsync(prefetchCount: 10);

        h.Ack(1, 5);
        await WaitUntilAsync(() => h.DequeueRequests.Count >= 2);

        Assert.True(h.DequeueRequests.ElementAt(1).Count >= 5);
        await StopAsync(h, task);
    }

    [Fact]
    public async Task ConsumeSession_SingleAckOfFullWindow_DoesNotRefill()
    {
        var (h, task) = await StartRefillSessionAsync(prefetchCount: 10);

        h.Ack(1, 1);
        await Task.Delay(500);

        Assert.Single(h.DequeueRequests);
        await StopAsync(h, task);
    }

    [Fact]
    public async Task ConsumeSession_PrefetchOne_RefillsOnAck()
    {
        var (h, task) = await StartRefillSessionAsync(prefetchCount: 1);

        h.Ack(1, 1);
        await WaitUntilAsync(() => h.DequeueRequests.Count >= 2);

        Assert.Equal(1, h.DequeueRequests.ElementAt(1).Count);
        await StopAsync(h, task);
    }

    [Fact]
    public async Task ConsumeSession_DeadLettersHalfDrainWindow_RefillsBeforePollInterval()
    {
        var (h, task) = await StartRefillSessionAsync(prefetchCount: 10);

        h.DeadLetter(1, 5);
        await WaitUntilAsync(() => h.DequeueRequests.Count >= 2);

        Assert.True(h.DequeueRequests.ElementAt(1).Count >= 5);
        await StopAsync(h, task);
    }

    [Fact]
    public async Task ConsumeSession_NacksHalfDrainWindow_RefillsBeforePollInterval()
    {
        var (h, task) = await StartRefillSessionAsync(prefetchCount: 10);
        h.QueueInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                It.IsAny<ActorId>(), "Nack", It.IsAny<ActorModels.NackRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.NackResponse { Success = true, DeliveryCount = 1 });

        h.Nack(1, 5);
        await WaitUntilAsync(() => h.DequeueRequests.Count >= 2);

        Assert.True(h.DequeueRequests.ElementAt(1).Count >= 5);
        await StopAsync(h, task);
    }

    [Fact]
    public async Task ConsumeSession_EmptyQueueAfterRefill_FurtherAcksDoNotDequeue()
    {
        var (h, task) = await StartRefillSessionAsync(prefetchCount: 10);

        h.Ack(1, 5);
        await WaitUntilAsync(() => h.DequeueRequests.Count >= 2);

        // Queue is now empty: draining the rest of the window must not trigger more DequeueLocked calls.
        h.Ack(6, 10);
        await Task.Delay(500);

        Assert.Equal(2, h.DequeueRequests.Count);
        await StopAsync(h, task);
    }
}
