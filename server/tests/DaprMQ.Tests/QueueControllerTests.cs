using System.Text.Json;
using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for QueueController to verify HTTP status code mappings
/// and response transformations from actor responses to API responses.
/// </summary>
public class QueueControllerTests
{
    private readonly Mock<ILogger<QueueController>> _mockLogger;
    private readonly Mock<IHttpSinkActorInvoker> _mockHttpSinkActorInvoker;
    private readonly Mock<Dapr.Actors.Client.IActorProxyFactory> _mockActorProxyFactory;
    private readonly Mock<IObjectStore> _mockObjectStore;
    private readonly IObjectClaimTokenIssuer _objectClaimTokenIssuer;
    private readonly Mock<IBlobReaperActorInvoker> _mockBlobReaperActorInvoker;
    private readonly BlobReapConfig _blobReapConfig;
    private readonly Mock<ISessionCoordinatorActorInvoker> _mockSessionCoordinatorActorInvoker;

    public QueueControllerTests()
    {
        _mockLogger = new Mock<ILogger<QueueController>>();
        _mockHttpSinkActorInvoker = new Mock<IHttpSinkActorInvoker>();
        _mockActorProxyFactory = new Mock<Dapr.Actors.Client.IActorProxyFactory>();
        _mockObjectStore = new Mock<IObjectStore>();
        _objectClaimTokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
        {
            SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
            TokenTtl = TimeSpan.FromMinutes(5)
        });
        _mockBlobReaperActorInvoker = new Mock<IBlobReaperActorInvoker>();
        _blobReapConfig = new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 };
        _mockSessionCoordinatorActorInvoker = new Mock<ISessionCoordinatorActorInvoker>();
    }

    private QueueController CreateController(IQueueActorInvoker actorInvoker) =>
        new QueueController(
            _mockLogger.Object,
            actorInvoker,
            _mockHttpSinkActorInvoker.Object,
            _mockActorProxyFactory.Object,
            _mockObjectStore.Object,
            _objectClaimTokenIssuer,
            _mockBlobReaperActorInvoker.Object,
            _blobReapConfig,
            _mockSessionCoordinatorActorInvoker.Object);

    private QueueController CreateController(IQueueActorInvoker actorInvoker, ISessionCoordinatorActorInvoker sessionCoordinatorActorInvoker) =>
        new QueueController(
            _mockLogger.Object,
            actorInvoker,
            _mockHttpSinkActorInvoker.Object,
            _mockActorProxyFactory.Object,
            _mockObjectStore.Object,
            _objectClaimTokenIssuer,
            _mockBlobReaperActorInvoker.Object,
            _blobReapConfig,
            sessionCoordinatorActorInvoker);

    [Fact]
    public async Task Enqueue_ValidSingleItem_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var controller = CreateController(mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });

        // Act
        var result = await controller.Enqueue("test-queue", request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiEnqueueResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(1, response.ItemsEnqueued);
    }

    [Fact]
    public async Task Enqueue_ValidMultipleItems_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 3 });

        var controller = CreateController(mockInvoker.Object);
        var item1 = JsonSerializer.SerializeToElement(new { id = 1 });
        var item2 = JsonSerializer.SerializeToElement(new { id = 2 });
        var item3 = JsonSerializer.SerializeToElement(new { id = 3 });

        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(item1, Priority: 1),
            new ApiEnqueueItem(item2, Priority: 0),
            new ApiEnqueueItem(item3, Priority: 1)
        });

        // Act
        var result = await controller.Enqueue("test-queue", request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiEnqueueResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(3, response.ItemsEnqueued);
    }

    [Fact]
    public async Task Enqueue_WithIdempotencyKey_MapsToActorEnqueueItem()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        EnqueueRequest? capturedRequest = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((_, _, req, _) => capturedRequest = req)
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var controller = CreateController(mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1, IdempotencyKey: "my-key-123")
        });

        // Act
        await controller.Enqueue("test-queue", request);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Equal("my-key-123", capturedRequest!.Items[0].IdempotencyKey);
    }

    [Fact]
    public async Task Enqueue_ResponseIncludesItemsDeduplicated()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1, ItemsDeduplicated = 2 });

        var controller = CreateController(mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1 });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, Priority: 1) });

        // Act
        var result = await controller.Enqueue("test-queue", request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiEnqueueResponse>(okResult.Value);
        Assert.Equal(2, response.ItemsDeduplicated);
    }

    [Fact]
    public async Task Enqueue_EmptyArray_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var controller = CreateController(mockInvoker.Object);
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>());

        // Act
        var result = await controller.Enqueue("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    [Fact]
    public async Task Enqueue_NullItems_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var controller = CreateController(mockInvoker.Object);
        var request = new ApiEnqueueRequest(null!);

        // Act
        var result = await controller.Enqueue("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    [Fact]
    public async Task Enqueue_NegativePriority_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var controller = CreateController(mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1 });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: -1)
        });

        // Act
        var result = await controller.Enqueue("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    [Fact]
    public async Task Enqueue_ExceedsMaxSize_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var controller = CreateController(mockInvoker.Object);

        var items = new List<ApiEnqueueItem>();
        for (int i = 0; i < 10001; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i });
            items.Add(new ApiEnqueueItem(itemElement, Priority: 1));
        }

        var request = new ApiEnqueueRequest(items);

        // Act
        var result = await controller.Enqueue("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    // Note: The following tests document what we WOULD test if ActorProxy was injectable
    // These serve as documentation for future refactoring to make the controller more testable

    /// <summary>
    /// This test verifies the expected behavior for DequeueLocked when successfully creating a lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Locked=true, LockId="xyz123", ItemJson="...", LockExpiresAt=timestamp
    /// - Controller should return: HTTP 200 OK with ApiDequeueLockedResponse containing all fields
    ///
    /// This is the bug we fixed - controller was incorrectly returning 423 for this case.
    /// NOW THIS TEST WOULD HAVE CAUGHT THE BUG!
    /// </summary>
    [Fact]
    public async Task Dequeue_WithRequireAck_SuccessfulLockCreation_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
                It.IsAny<ActorId>(),
                "DequeueLocked",
                It.IsAny<DequeueLockedRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueLockedResponse
            {
                Items = new List<DequeueLockedItem>
                {
                    new DequeueLockedItem
                    {
                        ItemJson = "{\"id\":1}",
                        Priority = 1,
                        LockId = "test-lock-123",
                        LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
                    }
                },
                Locked = false,  // Successfully created lock (not blocked)
                IsEmpty = false,
                Message = "Item locked with ID test-lock-123"
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: true, ttl_seconds: 30);

        // Assert - Should return HTTP 200 OK (NOT 423!)
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiDequeueLockedResponse>(okResult.Value);
        Assert.NotNull(response.Items);
        Assert.Single(response.Items);
        Assert.Equal("test-lock-123", response.Items[0].LockId);
        Assert.Equal(1, response.Items[0].Priority);
    }

    /// <summary>
    /// This test verifies the expected behavior for DequeueLocked when queue is already locked.
    ///
    /// Expected behavior:
    /// - Actor returns: Locked=true, LockId=null, ItemJson=null, Message="Queue is locked..."
    /// - Controller should return: HTTP 423 Locked with ApiLockedResponse
    /// </summary>
    [Fact]
    public async Task Dequeue_WithRequireAck_AlreadyLocked_Returns423()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
                It.IsAny<ActorId>(),
                "DequeueLocked",
                It.IsAny<DequeueLockedRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueLockedResponse
            {
                Items = new List<DequeueLockedItem>(),  // Empty - no items returned when locked
                Locked = true,  // Already locked by another operation
                IsEmpty = false,
                Message = "Queue is locked by another operation"
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: true, ttl_seconds: 30);

        // Assert - Should return HTTP 423 Locked
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(423, statusCodeResult.StatusCode);
        var response = Assert.IsType<ApiLockedResponse>(statusCodeResult.Value);
        Assert.Contains("locked", response.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// This test verifies the expected behavior for DequeueLocked when queue is empty.
    ///
    /// Expected behavior:
    /// - Actor returns: IsEmpty=true, Locked=false, ItemJson=null
    /// - Controller should return: HTTP 204 No Content
    /// </summary>
    [Fact]
    public async Task Dequeue_WithRequireAck_EmptyQueue_Returns204()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
                It.IsAny<ActorId>(),
                "DequeueLocked",
                It.IsAny<DequeueLockedRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueLockedResponse
            {
                Items = new List<DequeueLockedItem>(),  // Empty - queue is empty
                Locked = false,
                IsEmpty = true,
                Message = "Queue is empty"
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: true, ttl_seconds: 30);

        // Assert - Should return HTTP 204 No Content
        Assert.IsType<NoContentResult>(result);
    }

    /// <summary>
    /// This test verifies the expected behavior for regular Dequeue when queue is locked.
    ///
    /// Expected behavior:
    /// - Actor returns: Locked=true, Items=[]
    /// - Controller should return: HTTP 423 Locked
    /// </summary>
    [Fact]
    public async Task Dequeue_WithoutRequireAck_WhenLocked_Returns423()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(),
                "Dequeue",
                It.IsAny<DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse
            {
                Items = new List<DequeueItem>(),
                Locked = true,
                IsEmpty = false,
                Message = "Queue is locked by another operation",
                LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: false);

        // Assert - Should return HTTP 423 Locked
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(423, statusCodeResult.StatusCode);
    }

    /// <summary>
    /// This test verifies the expected behavior for regular Dequeue when successful.
    ///
    /// Expected behavior:
    /// - Actor returns: Items=[{ItemJson="...", Priority=1}], Locked=false, IsEmpty=false
    /// - Controller should return: HTTP 200 OK with ApiDequeueResponse
    /// </summary>
    [Fact]
    public async Task Dequeue_WithoutRequireAck_Success_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(),
                "Dequeue",
                It.IsAny<DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse
            {
                Items = new List<DequeueItem>
                {
                    new DequeueItem { ItemJson = "{\"id\":1}", Priority = 1 }
                },
                Locked = false,
                IsEmpty = false
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: false);

        // Assert - Should return HTTP 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiDequeueResponse>(okResult.Value);
        Assert.NotNull(response.Items);
        Assert.Single(response.Items);
        Assert.Equal(1, response.Items[0].Priority);
    }

    /// <summary>
    /// This test verifies the expected behavior for Acknowledge with valid lock ID.
    ///
    /// Expected behavior:
    /// - Actor returns: Success=true, ItemsAcknowledged=1
    /// - Controller should return: HTTP 200 OK
    /// </summary>
    [Fact]
    public async Task Acknowledge_ValidLockId_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(),
                "Acknowledge",
                It.IsAny<AcknowledgeRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeResponse
            {
                Success = true,
                Message = "Items acknowledged",
                ItemsAcknowledged = 1
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiAcknowledgeRequest("test-lock-123");

        // Act
        var result = await controller.Acknowledge("test-queue", request);

        // Assert - Should return HTTP 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiAcknowledgeResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(1, response.ItemsAcknowledged);
    }

    /// <summary>
    /// This test verifies the expected behavior for Acknowledge with expired lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Success=false, ErrorCode="LOCK_EXPIRED"
    /// - Controller should return: HTTP 410 Gone
    /// </summary>
    [Fact]
    public async Task Acknowledge_ExpiredLock_Returns410()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(),
                "Acknowledge",
                It.IsAny<AcknowledgeRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeResponse
            {
                Success = false,
                Message = "Lock has expired",
                ErrorCode = "LOCK_EXPIRED",
                ItemsAcknowledged = 0
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiAcknowledgeRequest("expired-lock");

        // Act
        var result = await controller.Acknowledge("test-queue", request);

        // Assert - Should return HTTP 410 Gone
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusCodeResult.StatusCode);
    }

    /// <summary>
    /// This test verifies the expected behavior for Acknowledge with invalid lock ID.
    ///
    /// Expected behavior:
    /// - Actor returns: Success=false, ErrorCode="LOCK_NOT_FOUND"
    /// - Controller should return: HTTP 404 Not Found
    /// </summary>
    [Fact]
    public async Task Acknowledge_InvalidLockId_Returns404()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(),
                "Acknowledge",
                It.IsAny<AcknowledgeRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeResponse
            {
                Success = false,
                Message = "Lock not found",
                ErrorCode = "LOCK_NOT_FOUND",
                ItemsAcknowledged = 0
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiAcknowledgeRequest("invalid-lock");

        // Act
        var result = await controller.Acknowledge("test-queue", request);

        // Assert - Should return HTTP 404 Not Found
        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// This test verifies the expected behavior for ExtendLock with valid lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Success=true, NewExpiresAt=timestamp
    /// - Controller should return: HTTP 200 OK
    /// </summary>
    [Fact]
    public async Task ExtendLock_Success_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var newExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ExtendLockRequest, ExtendLockResponse>(
                It.IsAny<ActorId>(),
                "ExtendLock",
                It.IsAny<ExtendLockRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtendLockResponse
            {
                Success = true,
                NewExpiresAt = newExpiresAt,
                ErrorCode = null,
                ErrorMessage = null
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiExtendLockRequest("test-lock-123", AdditionalTtlSeconds: 30);

        // Act
        var result = await controller.ExtendLock("test-queue", request);

        // Assert - Should return HTTP 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiExtendLockResponse>(okResult.Value);
        Assert.Equal("test-lock-123", response.LockId);
        Assert.Equal((long)newExpiresAt, response.NewExpiresAt);
    }

    /// <summary>
    /// This test verifies the expected behavior for ExtendLock with non-existent lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Success=false, ErrorCode="LOCK_NOT_FOUND"
    /// - Controller should return: HTTP 404 Not Found
    /// </summary>
    [Fact]
    public async Task ExtendLock_LockNotFound_Returns404()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ExtendLockRequest, ExtendLockResponse>(
                It.IsAny<ActorId>(),
                "ExtendLock",
                It.IsAny<ExtendLockRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtendLockResponse
            {
                Success = false,
                NewExpiresAt = 0,
                ErrorCode = "LOCK_NOT_FOUND",
                ErrorMessage = "Lock not found"
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiExtendLockRequest("nonexistent-lock", AdditionalTtlSeconds: 30);

        // Act
        var result = await controller.ExtendLock("test-queue", request);

        // Assert - Should return HTTP 404 Not Found
        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// This test verifies the expected behavior for ExtendLock with expired lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Success=false, ErrorCode="LOCK_EXPIRED"
    /// - Controller should return: HTTP 410 Gone
    /// </summary>
    [Fact]
    public async Task ExtendLock_LockExpired_Returns410()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ExtendLockRequest, ExtendLockResponse>(
                It.IsAny<ActorId>(),
                "ExtendLock",
                It.IsAny<ExtendLockRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtendLockResponse
            {
                Success = false,
                NewExpiresAt = 0,
                ErrorCode = "LOCK_EXPIRED",
                ErrorMessage = "Lock has expired"
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiExtendLockRequest("expired-lock", AdditionalTtlSeconds: 30);

        // Act
        var result = await controller.ExtendLock("test-queue", request);

        // Assert - Should return HTTP 410 Gone
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusCodeResult.StatusCode);
    }

    /// <summary>
    /// This test verifies the expected behavior for ExtendLock with invalid lock ID.
    ///
    /// Expected behavior:
    /// - Actor returns: Success=false, ErrorCode="INVALID_LOCK_ID"
    /// - Controller should return: HTTP 400 Bad Request
    /// </summary>
    [Fact]
    public async Task ExtendLock_InvalidLockId_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ExtendLockRequest, ExtendLockResponse>(
                It.IsAny<ActorId>(),
                "ExtendLock",
                It.IsAny<ExtendLockRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtendLockResponse
            {
                Success = false,
                NewExpiresAt = 0,
                ErrorCode = "INVALID_LOCK_ID",
                ErrorMessage = "Invalid lock ID"
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiExtendLockRequest("", AdditionalTtlSeconds: 30);

        // Act
        var result = await controller.ExtendLock("test-queue", request);

        // Assert - Should return HTTP 400 Bad Request
        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// This test verifies the expected behavior for DeadLetter with valid lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Status="SUCCESS", DlqId="queue-id-deadletter"
    /// - Controller should return: HTTP 200 OK
    /// </summary>
    [Fact]
    public async Task DeadLetter_ValidLock_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DeadLetterRequest, DeadLetterResponse>(
                It.IsAny<ActorId>(),
                "DeadLetter",
                It.IsAny<DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadLetterResponse
            {
                Status = "SUCCESS",
                DlqId = "test-queue-deadletter",
                Message = "Item moved to dead letter queue"
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiDeadLetterRequest("valid-lock-123");

        // Act
        var result = await controller.DeadLetter("test-queue", request);

        // Assert - Should return HTTP 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiDeadLetterResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("test-queue-deadletter", response.DlqId);
    }

    /// <summary>
    /// This test verifies the expected behavior for DeadLetter with lock not found.
    ///
    /// Expected behavior:
    /// - Actor returns: Status="ERROR", ErrorCode="LOCK_NOT_FOUND"
    /// - Controller should return: HTTP 404 Not Found
    /// </summary>
    [Fact]
    public async Task DeadLetter_LockNotFound_Returns404()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DeadLetterRequest, DeadLetterResponse>(
                It.IsAny<ActorId>(),
                "DeadLetter",
                It.IsAny<DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadLetterResponse
            {
                Status = "ERROR",
                ErrorCode = "LOCK_NOT_FOUND",
                Message = "No active lock found"
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiDeadLetterRequest("nonexistent-lock");

        // Act
        var result = await controller.DeadLetter("test-queue", request);

        // Assert - Should return HTTP 404 Not Found
        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// This test verifies the expected behavior for DeadLetter with expired lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Status="ERROR", ErrorCode="LOCK_EXPIRED"
    /// - Controller should return: HTTP 410 Gone
    /// </summary>
    [Fact]
    public async Task DeadLetter_LockExpired_Returns410()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DeadLetterRequest, DeadLetterResponse>(
                It.IsAny<ActorId>(),
                "DeadLetter",
                It.IsAny<DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadLetterResponse
            {
                Status = "ERROR",
                ErrorCode = "LOCK_EXPIRED",
                Message = "Lock has expired"
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiDeadLetterRequest("expired-lock");

        // Act
        var result = await controller.DeadLetter("test-queue", request);

        // Assert - Should return HTTP 410 Gone
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusCodeResult.StatusCode);
    }

    /// <summary>
    /// This test verifies the expected behavior for DeadLetter with invalid lock ID.
    ///
    /// Expected behavior:
    /// - Actor returns: Status="ERROR", ErrorCode="INVALID_LOCK_ID"
    /// - Controller should return: HTTP 400 Bad Request
    /// </summary>
    [Fact]
    public async Task DeadLetter_InvalidLockId_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DeadLetterRequest, DeadLetterResponse>(
                It.IsAny<ActorId>(),
                "DeadLetter",
                It.IsAny<DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadLetterResponse
            {
                Status = "ERROR",
                ErrorCode = "INVALID_LOCK_ID",
                Message = "Invalid lock ID provided"
            });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiDeadLetterRequest("wrong-lock-id");

        // Act
        var result = await controller.DeadLetter("test-queue", request);

        // Assert - Should return HTTP 400 Bad Request
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ===== Bulk Dequeue Controller Tests =====

    /// <summary>
    /// This test verifies controller handling of bulk dequeue with multiple items.
    ///
    /// Expected behavior:
    /// - Actor returns: Items=[item1, item2, item3]
    /// - Controller should return: HTTP 200 OK with multiple items
    /// </summary>
    [Fact]
    public async Task Dequeue_BulkWithMultipleItems_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(),
                "Dequeue",
                It.IsAny<DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse
            {
                Items = new List<DequeueItem>
                {
                    new DequeueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                    new DequeueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                    new DequeueItem { ItemJson = "{\"id\":3}", Priority = 1 }
                },
                Locked = false,
                IsEmpty = false
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: false, count: 3);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiDequeueResponse>(okResult.Value);
        Assert.NotNull(response.Items);
        Assert.Equal(3, response.Items.Count);
    }

    /// <summary>
    /// This test verifies controller validation for count parameter.
    ///
    /// Expected behavior:
    /// - Count exceeds max (1000)
    /// - Controller should return: HTTP 400 Bad Request
    /// </summary>
    [Fact]
    public async Task Dequeue_CountExceedsMax_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var controller = CreateController(mockInvoker.Object);

        // Act - Request more than 1000 items
        var result = await controller.Dequeue("test-queue", require_ack: false, count: 1001);

        // Assert - Should return HTTP 400 Bad Request
        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// This test verifies controller validation for negative count.
    ///
    /// Expected behavior:
    /// - Count is negative
    /// - Controller should return: HTTP 400 Bad Request
    /// </summary>
    [Fact]
    public async Task Dequeue_NegativeCount_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var controller = CreateController(mockInvoker.Object);

        // Act - Request negative count
        var result = await controller.Dequeue("test-queue", require_ack: false, count: -1);

        // Assert - Should return HTTP 400 Bad Request
        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// This test verifies empty queue returns 204 even with bulk dequeue.
    ///
    /// Expected behavior:
    /// - Actor returns: Items=[], IsEmpty=true
    /// - Controller should return: HTTP 204 No Content
    /// </summary>
    [Fact]
    public async Task Dequeue_BulkEmptyQueue_Returns204()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(),
                "Dequeue",
                It.IsAny<DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse
            {
                Items = new List<DequeueItem>(),
                Locked = false,
                IsEmpty = true,
                Message = "Queue is empty"
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: false, count: 10);

        // Assert - Should return HTTP 204 No Content
        Assert.IsType<NoContentResult>(result);
    }

    /// <summary>
    /// This test verifies locked queue returns 423 even with bulk dequeue.
    ///
    /// Expected behavior:
    /// - Actor returns: Items=[], Locked=true
    /// - Controller should return: HTTP 423 Locked
    /// </summary>
    [Fact]
    public async Task Dequeue_BulkLockedQueue_Returns423()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(),
                "Dequeue",
                It.IsAny<DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse
            {
                Items = new List<DequeueItem>(),
                Locked = true,
                IsEmpty = false,
                Message = "Queue is locked by another operation",
                LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: false, count: 5);

        // Assert - Should return HTTP 423 Locked
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(423, statusCodeResult.StatusCode);
    }

    /// <summary>
    /// This test verifies DequeueLocked with count parameter returns multiple items with lock IDs.
    ///
    /// Expected behavior:
    /// - Actor returns: Items=[item1, item2, item3] with lock IDs
    /// - Controller should return: HTTP 200 OK with multiple items and lock metadata
    /// </summary>
    [Fact]
    public async Task DequeueLocked_WithCount_ReturnsArrayWithLockIds()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
                It.IsAny<ActorId>(),
                "DequeueLocked",
                It.IsAny<DequeueLockedRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueLockedResponse
            {
                Items = new List<DequeueLockedItem>
                {
                    new DequeueLockedItem
                    {
                        ItemJson = "{\"id\":1}",
                        Priority = 1,
                        LockId = "lock-1",
                        LockExpiresAt = expiresAt
                    },
                    new DequeueLockedItem
                    {
                        ItemJson = "{\"id\":2}",
                        Priority = 1,
                        LockId = "lock-2",
                        LockExpiresAt = expiresAt
                    },
                    new DequeueLockedItem
                    {
                        ItemJson = "{\"id\":3}",
                        Priority = 0,
                        LockId = "lock-3",
                        LockExpiresAt = expiresAt
                    }
                },
                Locked = false,
                IsEmpty = false,
                Message = "Items locked"
            });

        var controller = CreateController(mockInvoker.Object);

        // Act
        var result = await controller.Dequeue("test-queue", require_ack: true, ttl_seconds: 30, count: 3);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiDequeueLockedResponse>(okResult.Value);

        // Verify we got Items array with lock IDs
        Assert.NotNull(response.Items);
        Assert.Equal(3, response.Items.Count);
        Assert.Equal("lock-1", response.Items[0].LockId);
        Assert.Equal("lock-2", response.Items[1].LockId);
        Assert.Equal("lock-3", response.Items[2].LockId);
        Assert.Equal(1, response.Items[0].Priority);
        Assert.Equal(1, response.Items[1].Priority);
        Assert.Equal(0, response.Items[2].Priority);
    }
}
