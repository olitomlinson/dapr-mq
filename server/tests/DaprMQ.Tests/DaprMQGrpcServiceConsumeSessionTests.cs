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
        ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker) =>
        new(_mockLogger.Object, queueActorInvoker, sessionCoordinatorActorInvoker);

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
