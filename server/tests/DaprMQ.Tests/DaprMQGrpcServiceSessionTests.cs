using Dapr.Actors;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using ActorModels = DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for DaprMQGrpcService's session surface: AcceptSession/RenewSessionLease/
/// ReleaseSession unary RPCs, and sessionId-based Enqueue routing (plan §3.5/§3.6, §6 step 6 -
/// the gRPC counterpart of QueueControllerSessionTests.cs).
/// </summary>
public class DaprMQGrpcServiceSessionTests
{
    private readonly Mock<ILogger<DaprMQGrpcService>> _mockLogger = new();
    private readonly Mock<ServerCallContext> _mockContext = new();

    private DaprMQGrpcService CreateService(
        IQueueActorInvoker? queueActorInvoker = null,
        ISessionCoordinatorActorInvoker? sessionCoordinatorActorInvoker = null) =>
        new(
            _mockLogger.Object,
            queueActorInvoker ?? new Mock<IQueueActorInvoker>().Object,
            sessionCoordinatorActorInvoker ?? new Mock<ISessionCoordinatorActorInvoker>().Object);

    // ---- AcceptSession ----

    [Fact]
    public async Task AcceptSession_Success_ReturnsLease()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var response = await service.AcceptSession(
            new ApiServer.Grpc.AcceptSessionRequest { QueueId = "test-queue", SessionId = "s1" },
            _mockContext.Object);

        Assert.Equal("s1", response.SessionId);
        Assert.Equal("lease-1", response.LeaseId);
        Assert.Equal(12345, response.LeaseExpiresAt);
    }

    [Fact]
    public async Task AcceptSession_TargetsPlainQueueIdActor()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        ActorId? capturedActorId = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.AcceptSessionRequest, CancellationToken>((id, _, _, _) => capturedActorId = id)
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 1 });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        await service.AcceptSession(new ApiServer.Grpc.AcceptSessionRequest { QueueId = "test-queue", SessionId = "s1" }, _mockContext.Object);

        Assert.Equal(new ActorId("test-queue"), capturedActorId);
    }

    [Fact]
    public async Task AcceptSession_SessionNotFound_ThrowsNotFound()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = false, ErrorCode = "SESSION_NOT_FOUND", ErrorMessage = "not found" });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.AcceptSession(new ApiServer.Grpc.AcceptSessionRequest { QueueId = "test-queue", SessionId = "missing" }, _mockContext.Object));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_SessionLocked_ThrowsFailedPrecondition()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = false, ErrorCode = "SESSION_LOCKED", ErrorMessage = "locked" });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.AcceptSession(new ApiServer.Grpc.AcceptSessionRequest { QueueId = "test-queue", SessionId = "s1" }, _mockContext.Object));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_NoSessionsAvailable_ThrowsNotFound()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = false, ErrorCode = "NO_SESSIONS_AVAILABLE" });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.AcceptSession(new ApiServer.Grpc.AcceptSessionRequest { QueueId = "test-queue" }, _mockContext.Object));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_SessionActorUnavailable_ThrowsUnavailable()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcceptSessionRequest, ActorModels.AcceptSessionResponse>(
                It.IsAny<ActorId>(), "AcceptSession", It.IsAny<ActorModels.AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcceptSessionResponse { Success = false, ErrorCode = "SESSION_ACTOR_UNAVAILABLE", ErrorMessage = "unavailable" });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.AcceptSession(new ApiServer.Grpc.AcceptSessionRequest { QueueId = "test-queue", SessionId = "s1" }, _mockContext.Object));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    // ---- RenewSessionLease ----

    [Fact]
    public async Task RenewSessionLease_Success_ReturnsNewExpiry()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.RenewSessionLeaseRequest, ActorModels.RenewSessionLeaseResponse>(
                It.IsAny<ActorId>(), "RenewSessionLease", It.IsAny<ActorModels.RenewSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.RenewSessionLeaseResponse { Success = true, NewExpiresAt = 999 });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var response = await service.RenewSessionLease(
            new ApiServer.Grpc.RenewSessionLeaseRequest { QueueId = "test-queue", SessionId = "s1", LeaseId = "lease-1" },
            _mockContext.Object);

        Assert.Equal(999, response.NewExpiresAt);
    }

    [Fact]
    public async Task RenewSessionLease_Expired_ThrowsFailedPrecondition()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.RenewSessionLeaseRequest, ActorModels.RenewSessionLeaseResponse>(
                It.IsAny<ActorId>(), "RenewSessionLease", It.IsAny<ActorModels.RenewSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.RenewSessionLeaseResponse { Success = false, ErrorCode = "SESSION_LEASE_EXPIRED", ErrorMessage = "expired" });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.RenewSessionLease(new ApiServer.Grpc.RenewSessionLeaseRequest { QueueId = "test-queue", SessionId = "s1", LeaseId = "lease-1" }, _mockContext.Object));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task RenewSessionLease_InvalidLeaseId_ThrowsInvalidArgument()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.RenewSessionLeaseRequest, ActorModels.RenewSessionLeaseResponse>(
                It.IsAny<ActorId>(), "RenewSessionLease", It.IsAny<ActorModels.RenewSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.RenewSessionLeaseResponse { Success = false, ErrorCode = "INVALID_LEASE_ID", ErrorMessage = "mismatch" });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.RenewSessionLease(new ApiServer.Grpc.RenewSessionLeaseRequest { QueueId = "test-queue", SessionId = "s1", LeaseId = "wrong" }, _mockContext.Object));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ---- ReleaseSession ----

    [Fact]
    public async Task ReleaseSession_Success_ReturnsSuccess()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = true });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var response = await service.ReleaseSession(
            new ApiServer.Grpc.ReleaseSessionRequest { QueueId = "test-queue", SessionId = "s1", LeaseId = "lease-1" },
            _mockContext.Object);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task ReleaseSession_InvalidLeaseId_ThrowsInvalidArgument()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ReleaseSessionRequest, ActorModels.ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ActorModels.ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ReleaseSessionResponse { Success = false, ErrorCode = "INVALID_LEASE_ID", ErrorMessage = "mismatch" });

        var service = CreateService(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.ReleaseSession(new ApiServer.Grpc.ReleaseSessionRequest { QueueId = "test-queue", SessionId = "s1", LeaseId = "wrong" }, _mockContext.Object));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ---- Enqueue routing ----

    [Fact]
    public async Task Enqueue_WithSessionId_RoutesToSessionActorId()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorId? capturedActorId = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.EnqueueRequest, CancellationToken>((id, _, _, _) => capturedActorId = id)
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest { QueueId = "test-queue" };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{}", Priority = 1, SessionId = "s1" });

        await service.Enqueue(request, _mockContext.Object);

        Assert.Equal(new ActorId("test-queue-session-s1"), capturedActorId);
    }

    [Fact]
    public async Task Enqueue_WithoutSessionId_RoutesToPlainQueueActorId()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorId? capturedActorId = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.EnqueueRequest, CancellationToken>((id, _, _, _) => capturedActorId = id)
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest { QueueId = "test-queue" };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{}", Priority = 1 });

        await service.Enqueue(request, _mockContext.Object);

        Assert.Equal(new ActorId("test-queue"), capturedActorId);
    }

    [Fact]
    public async Task Enqueue_MixedSessionAndPlainItems_IssuesOneCallPerTarget()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var callsByActorId = new Dictionary<ActorId, ActorModels.EnqueueRequest>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.EnqueueRequest, CancellationToken>((id, _, req, _) => callsByActorId[id] = req)
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest { QueueId = "test-queue" };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{}", Priority = 1, SessionId = "s1" });
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{}", Priority = 1 });
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{}", Priority = 1, SessionId = "s2" });

        var response = await service.Enqueue(request, _mockContext.Object);

        Assert.Equal(3, response.ItemsEnqueued);
        Assert.Equal(3, callsByActorId.Count);
        Assert.Contains(new ActorId("test-queue-session-s1"), callsByActorId.Keys);
        Assert.Contains(new ActorId("test-queue-session-s2"), callsByActorId.Keys);
        Assert.Contains(new ActorId("test-queue"), callsByActorId.Keys);
    }

    // ---- lease_id threading ----

    [Fact]
    public async Task Dequeue_WithLeaseId_ForwardsLeaseIdOnDequeueRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorModels.DequeueRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.DequeueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.DequeueRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new ActorModels.DequeueResponse { IsEmpty = true });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        await service.Dequeue(new ApiServer.Grpc.DequeueRequest { QueueId = "test-queue-session-s1", LeaseId = "lease-1" }, _mockContext.Object);

        Assert.Equal("lease-1", captured!.LeaseId);
    }

    [Fact]
    public async Task Dequeue_SessionLeaseExpired_ThrowsFailedPrecondition()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.DequeueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueResponse { ErrorCode = "SESSION_LEASE_EXPIRED", Message = "expired" });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.Dequeue(new ApiServer.Grpc.DequeueRequest { QueueId = "test-queue-session-s1", LeaseId = "stale" }, _mockContext.Object));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task Acknowledge_WithLeaseId_ForwardsLeaseIdOnAcknowledgeRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorModels.AcknowledgeRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.AcknowledgeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.AcknowledgeRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new ActorModels.AcknowledgeResponse { Success = true, ItemsAcknowledged = 1 });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        await service.Acknowledge(new ApiServer.Grpc.AcknowledgeRequest { QueueId = "test-queue-session-s1", LockId = "lock-1", LeaseId = "lease-1" }, _mockContext.Object);

        Assert.Equal("lease-1", captured!.LeaseId);
    }

    [Fact]
    public async Task ExtendLock_WithLeaseId_ForwardsLeaseIdOnExtendLockRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorModels.ExtendLockRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ExtendLockRequest, ActorModels.ExtendLockResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.ExtendLockRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.ExtendLockRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new ActorModels.ExtendLockResponse { Success = true, NewExpiresAt = 100 });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        await service.ExtendLock(new ApiServer.Grpc.ExtendLockRequest { QueueId = "test-queue-session-s1", LockId = "lock-1", LeaseId = "lease-1" }, _mockContext.Object);

        Assert.Equal("lease-1", captured!.LeaseId);
    }

    [Fact]
    public async Task DeadLetter_WithLeaseId_ForwardsLeaseIdOnDeadLetterRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorModels.DeadLetterRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.DeadLetterRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.DeadLetterRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new ActorModels.DeadLetterResponse { Status = "SUCCESS", DlqId = "dlq-1" });

        var service = CreateService(queueActorInvoker: mockInvoker.Object);
        await service.DeadLetter(new ApiServer.Grpc.DeadLetterRequest { QueueId = "test-queue-session-s1", LockId = "lock-1", LeaseId = "lease-1" }, _mockContext.Object);

        Assert.Equal("lease-1", captured!.LeaseId);
    }
}
