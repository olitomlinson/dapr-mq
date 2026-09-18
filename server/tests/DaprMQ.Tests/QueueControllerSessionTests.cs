using System.Text.Json;
using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for QueueController's session surface: sessions/accept, sessions/{id}/renew,
/// sessions/{id}/release, and sessionId-based enqueue routing (plan §3.5/§3.6, §6 step 5).
/// </summary>
public class QueueControllerSessionTests
{
    private readonly Mock<ILogger<QueueController>> _mockLogger = new();
    private readonly Mock<IHttpSinkActorInvoker> _mockHttpSinkActorInvoker = new();
    private readonly Mock<Dapr.Actors.Client.IActorProxyFactory> _mockActorProxyFactory = new();
    private readonly Mock<IObjectStore> _mockObjectStore = new();
    private readonly IObjectClaimTokenIssuer _objectClaimTokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
    {
        SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
        TokenTtl = TimeSpan.FromMinutes(5)
    });
    private readonly Mock<IBlobReaperActorInvoker> _mockBlobReaperActorInvoker = new();
    private readonly BlobReapConfig _blobReapConfig = new() { BackstopSeconds = 86400, PostDownloadSeconds = 86400 };

    private QueueController CreateController(
        IQueueActorInvoker? actorInvoker = null,
        ISessionCoordinatorActorInvoker? sessionCoordinatorActorInvoker = null) =>
        new(
            _mockLogger.Object,
            (actorInvoker ?? new Mock<IQueueActorInvoker>().Object),
            _mockHttpSinkActorInvoker.Object,
            _mockActorProxyFactory.Object,
            _mockObjectStore.Object,
            _objectClaimTokenIssuer,
            _mockBlobReaperActorInvoker.Object,
            _blobReapConfig,
            (sessionCoordinatorActorInvoker ?? new Mock<ISessionCoordinatorActorInvoker>().Object));

    // ---- AcceptSession ----

    [Fact]
    public async Task AcceptSession_Success_Returns200WithLease()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcceptSessionRequest, AcceptSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.AcceptSession, It.IsAny<AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 12345 });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest("s1"));

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiAcceptSessionResponse>(okResult.Value);
        Assert.Equal("s1", response.SessionId);
        Assert.Equal("lease-1", response.LeaseId);
        Assert.Equal(12345, response.LeaseExpiresAt);
    }

    [Fact]
    public async Task AcceptSession_TargetsCorrectActorId()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        ActorId? capturedActorId = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcceptSessionRequest, AcceptSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.AcceptSession, It.IsAny<AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, AcceptSessionRequest, CancellationToken>((id, _, _, _) => capturedActorId = id)
            .ReturnsAsync(new AcceptSessionResponse { Success = true, SessionId = "s1", LeaseId = "lease-1", LeaseExpiresAt = 1 });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest("s1"));

        // The coordinator is addressed by the plain queueId (a different Dapr actor type, same id
        // string, per plan §2.1.1) - not a derived "-session-" id, which only the QueueActor uses.
        Assert.Equal(new ActorId("test-queue"), capturedActorId);
    }

    [Fact]
    public async Task AcceptSession_SessionNotFound_Returns404()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcceptSessionRequest, AcceptSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.AcceptSession, It.IsAny<AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcceptSessionResponse { Success = false, ErrorCode = "SESSION_NOT_FOUND", ErrorMessage = "not found" });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest("missing"));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task AcceptSession_SessionLocked_Returns423()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcceptSessionRequest, AcceptSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.AcceptSession, It.IsAny<AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcceptSessionResponse { Success = false, ErrorCode = "SESSION_LOCKED", ErrorMessage = "locked" });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest("s1"));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(423, objectResult.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_NoSessionsAvailable_Returns204()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcceptSessionRequest, AcceptSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.AcceptSession, It.IsAny<AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcceptSessionResponse { Success = false, ErrorCode = "NO_SESSIONS_AVAILABLE" });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.AcceptSession("test-queue", null);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task AcceptSession_SessionActorUnavailable_Returns502()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcceptSessionRequest, AcceptSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.AcceptSession, It.IsAny<AcceptSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcceptSessionResponse { Success = false, ErrorCode = "SESSION_ACTOR_UNAVAILABLE", ErrorMessage = "unavailable" });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest("s1"));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objectResult.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_EmptySessionId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest(""));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AcceptSession_OverlongSessionId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest(new string('a', 257)));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task AcceptSession_LeaseSecondsOutOfRange_Returns400()
    {
        var controller = CreateController();
        var result = await controller.AcceptSession("test-queue", new ApiAcceptSessionRequest("s1", LeaseSeconds: 0));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- RenewSessionLease ----

    [Fact]
    public async Task RenewSessionLease_Success_Returns200WithNewExpiry()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<RenewSessionLeaseRequest, RenewSessionLeaseResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.RenewSessionLease, It.IsAny<RenewSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewSessionLeaseResponse { Success = true, NewExpiresAt = 999 });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.RenewSessionLease("test-queue", "s1", new ApiRenewSessionLeaseRequest("lease-1"));

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiRenewSessionLeaseResponse>(okResult.Value);
        Assert.Equal(999, response.NewExpiresAt);
    }

    [Fact]
    public async Task RenewSessionLease_Expired_Returns410()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<RenewSessionLeaseRequest, RenewSessionLeaseResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.RenewSessionLease, It.IsAny<RenewSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewSessionLeaseResponse { Success = false, ErrorCode = "SESSION_LEASE_EXPIRED", ErrorMessage = "expired" });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.RenewSessionLease("test-queue", "s1", new ApiRenewSessionLeaseRequest("lease-1"));

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, objectResult.StatusCode);
    }

    [Fact]
    public async Task RenewSessionLease_InvalidLeaseId_Returns400()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<RenewSessionLeaseRequest, RenewSessionLeaseResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.RenewSessionLease, It.IsAny<RenewSessionLeaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RenewSessionLeaseResponse { Success = false, ErrorCode = "INVALID_LEASE_ID", ErrorMessage = "mismatch" });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.RenewSessionLease("test-queue", "s1", new ApiRenewSessionLeaseRequest("wrong-lease"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RenewSessionLease_EmptyLeaseId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.RenewSessionLease("test-queue", "s1", new ApiRenewSessionLeaseRequest(""));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- ReleaseSession ----

    [Fact]
    public async Task ReleaseSession_Success_Returns200()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ReleaseSessionRequest, ReleaseSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.ReleaseSession, It.IsAny<ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseSessionResponse { Success = true });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.ReleaseSession("test-queue", "s1", new ApiReleaseSessionRequest("lease-1"));

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiReleaseSessionResponse>(okResult.Value);
        Assert.True(response.Success);
    }

    [Fact]
    public async Task ReleaseSession_InvalidLeaseId_Returns400()
    {
        var mockInvoker = new Mock<ISessionCoordinatorActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ReleaseSessionRequest, ReleaseSessionResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.ReleaseSession, It.IsAny<ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseSessionResponse { Success = false, ErrorCode = "INVALID_LEASE_ID", ErrorMessage = "mismatch" });

        var controller = CreateController(sessionCoordinatorActorInvoker: mockInvoker.Object);
        var result = await controller.ReleaseSession("test-queue", "s1", new ApiReleaseSessionRequest("wrong-lease"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- Enqueue routing ----

    [Fact]
    public async Task Enqueue_WithSessionId_RoutesToSessionActorId()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorId? capturedActorId = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((id, _, _, _) => capturedActorId = id)
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { value = "test" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, SessionId: "s1")
        });

        var result = await controller.Enqueue("test-queue", request);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new ActorId("test-queue-session-s1"), capturedActorId);
    }

    [Fact]
    public async Task Enqueue_WithoutSessionId_RoutesToPlainQueueActorId()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorId? capturedActorId = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((id, _, _, _) => capturedActorId = id)
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { value = "test" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement) });

        await controller.Enqueue("test-queue", request);

        Assert.Equal(new ActorId("test-queue"), capturedActorId);
    }

    [Fact]
    public async Task Enqueue_MixedSessionAndPlainItems_IssuesOneCallPerTarget()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var callsByActorId = new Dictionary<ActorId, EnqueueRequest>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((id, _, req, _) => callsByActorId[id] = req)
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { value = "test" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, SessionId: "s1"),
            new ApiEnqueueItem(itemElement),
            new ApiEnqueueItem(itemElement, SessionId: "s2")
        });

        var result = await controller.Enqueue("test-queue", request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiEnqueueResponse>(okResult.Value);
        Assert.Equal(3, response.ItemsEnqueued);
        Assert.Equal(3, callsByActorId.Count);
        Assert.Contains(new ActorId("test-queue-session-s1"), callsByActorId.Keys);
        Assert.Contains(new ActorId("test-queue-session-s2"), callsByActorId.Keys);
        Assert.Contains(new ActorId("test-queue"), callsByActorId.Keys);
    }

    [Fact]
    public async Task Enqueue_WithSessionId_NeverCallsSessionCoordinator()
    {
        // Registration is self-driven by the session QueueActor on its own first activation
        // (plan §2.2) - the controller's routing never touches SessionCoordinatorActor at all.
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        mockQueueInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Enqueue, It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });
        var mockSessionCoordinatorInvoker = new Mock<ISessionCoordinatorActorInvoker>();

        var controller = CreateController(actorInvoker: mockQueueInvoker.Object, sessionCoordinatorActorInvoker: mockSessionCoordinatorInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { value = "test" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, SessionId: "s1") });

        await controller.Enqueue("test-queue", request);

        mockSessionCoordinatorInvoker.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Enqueue_InvalidSessionId_Returns400()
    {
        var controller = CreateController();
        var itemElement = JsonSerializer.SerializeToElement(new { value = "test" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, SessionId: new string('a', 257))
        });

        var result = await controller.Enqueue("test-queue", request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ---- Lease-id header threading (Dequeue/Acknowledge/ExtendLock/DeadLetter) ----

    [Fact]
    public async Task Dequeue_WithLeaseIdHeader_ForwardsLeaseIdOnDequeueRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        DequeueRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Dequeue, It.IsAny<DequeueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, DequeueRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new DequeueResponse { IsEmpty = true });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        await controller.Dequeue("test-queue-session-s1", lease_id: "lease-1");

        Assert.Equal("lease-1", captured!.LeaseId);
    }

    [Fact]
    public async Task Dequeue_SessionLeaseExpired_Returns410()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Dequeue, It.IsAny<DequeueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse { ErrorCode = "SESSION_LEASE_EXPIRED", Message = "expired" });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        var result = await controller.Dequeue("test-queue-session-s1", lease_id: "stale-lease");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, objectResult.StatusCode);
    }

    [Fact]
    public async Task Dequeue_InvalidLeaseId_Returns400()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Dequeue, It.IsAny<DequeueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse { ErrorCode = "INVALID_LEASE_ID", Message = "mismatch" });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        var result = await controller.Dequeue("test-queue-session-s1", lease_id: "wrong-lease");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Acknowledge_WithLeaseIdHeader_ForwardsLeaseIdOnAcknowledgeRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        AcknowledgeRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Acknowledge, It.IsAny<AcknowledgeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, AcknowledgeRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new AcknowledgeResponse { Success = true, ItemsAcknowledged = 1 });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        await controller.Acknowledge("test-queue-session-s1", new ApiAcknowledgeRequest("lock-1"), lease_id: "lease-1");

        Assert.Equal("lease-1", captured!.LeaseId);
    }

    [Fact]
    public async Task ExtendLock_WithLeaseIdHeader_ForwardsLeaseIdOnExtendLockRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ExtendLockRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ExtendLockRequest, ExtendLockResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.ExtendLock, It.IsAny<ExtendLockRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ExtendLockRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new ExtendLockResponse { Success = true, NewExpiresAt = 100 });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        await controller.ExtendLock("test-queue-session-s1", new ApiExtendLockRequest("lock-1"), lease_id: "lease-1");

        Assert.Equal("lease-1", captured!.LeaseId);
    }

    [Fact]
    public async Task DeadLetter_WithLeaseIdHeader_ForwardsLeaseIdOnDeadLetterRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        DeadLetterRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<DeadLetterRequest, DeadLetterResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.DeadLetter, It.IsAny<DeadLetterRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, DeadLetterRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new DeadLetterResponse { Status = "SUCCESS", DlqId = "dlq-1" });

        var controller = CreateController(actorInvoker: mockInvoker.Object);
        await controller.DeadLetter("test-queue-session-s1", new ApiDeadLetterRequest("lock-1"), lease_id: "lease-1");

        Assert.Equal("lease-1", captured!.LeaseId);
    }
}
