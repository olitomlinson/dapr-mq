using System.Text.Json;
using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;
using Xunit;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for QueueController to verify HTTP status code mappings
/// and response transformations from actor responses to API responses.
/// </summary>
public class QueueControllerTests
{
    private readonly Mock<ILogger<QueueController>> _mockLogger;

    public QueueControllerTests()
    {
        _mockLogger = new Mock<ILogger<QueueController>>();
    }

    [Fact]
    public async Task Push_ValidSingleItem_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<PushRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 1 });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test" });
        var request = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });

        // Act
        var result = await controller.Push("test-queue", request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiPushResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(1, response.ItemsPushed);
    }

    [Fact]
    public async Task Push_ValidMultipleItems_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<PushRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 3 });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
        var item1 = JsonSerializer.SerializeToElement(new { id = 1 });
        var item2 = JsonSerializer.SerializeToElement(new { id = 2 });
        var item3 = JsonSerializer.SerializeToElement(new { id = 3 });

        var request = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(item1, Priority: 1),
            new ApiPushItem(item2, Priority: 0),
            new ApiPushItem(item3, Priority: 1)
        });

        // Act
        var result = await controller.Push("test-queue", request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiPushResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(3, response.ItemsPushed);
    }

    [Fact]
    public async Task Push_EmptyArray_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiPushRequest(new List<ApiPushItem>());

        // Act
        var result = await controller.Push("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    [Fact]
    public async Task Push_NullItems_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiPushRequest(null!);

        // Act
        var result = await controller.Push("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    [Fact]
    public async Task Push_NegativePriority_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1 });
        var request = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: -1)
        });

        // Act
        var result = await controller.Push("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    [Fact]
    public async Task Push_ExceedsMaxSize_Returns400()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);

        var items = new List<ApiPushItem>();
        for (int i = 0; i < 1001; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i });
            items.Add(new ApiPushItem(itemElement, Priority: 1));
        }

        var request = new ApiPushRequest(items);

        // Act
        var result = await controller.Push("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var errorResponse = Assert.IsType<ApiErrorResponse>(badRequestResult.Value);
        Assert.False(errorResponse.Success);
    }

    // Note: The following tests document what we WOULD test if ActorProxy was injectable
    // These serve as documentation for future refactoring to make the controller more testable

    /// <summary>
    /// This test verifies the expected behavior for PopWithAck when successfully creating a lock.
    ///
    /// Expected behavior:
    /// - Actor returns: Locked=true, LockId="xyz123", ItemJson="...", LockExpiresAt=timestamp
    /// - Controller should return: HTTP 200 OK with ApiPopWithAckResponse containing all fields
    ///
    /// This is the bug we fixed - controller was incorrectly returning 423 for this case.
    /// NOW THIS TEST WOULD HAVE CAUGHT THE BUG!
    /// </summary>
    [Fact]
    public async Task Pop_WithRequireAck_SuccessfulLockCreation_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(),
                "PopWithAck",
                It.IsAny<PopWithAckRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                ItemJson = "{\"id\":1}",
                Locked = true,  // Successfully created lock
                LockId = "test-lock-123",  // Lock ID present - this is the KEY difference!
                LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
                IsEmpty = false,
                Message = "Item locked with ID test-lock-123"
            });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);

        // Act
        var result = await controller.Pop("test-queue", require_ack: true, ttl_seconds: 30);

        // Assert - Should return HTTP 200 OK (NOT 423!)
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiPopWithAckResponse>(okResult.Value);
        Assert.True(response.Locked);
        Assert.NotNull(response.LockId);
        Assert.Equal("test-lock-123", response.LockId);
    }

    /// <summary>
    /// This test verifies the expected behavior for PopWithAck when queue is already locked.
    ///
    /// Expected behavior:
    /// - Actor returns: Locked=true, LockId=null, ItemJson=null, Message="Queue is locked..."
    /// - Controller should return: HTTP 423 Locked with ApiLockedResponse
    /// </summary>
    [Fact]
    public async Task Pop_WithRequireAck_AlreadyLocked_Returns423()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(),
                "PopWithAck",
                It.IsAny<PopWithAckRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                ItemJson = null,
                Locked = true,  // Already locked by another operation
                LockId = null,  // No Lock ID - KEY difference from successful lock!
                LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
                IsEmpty = false,
                Message = "Queue is locked by another operation"
            });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);

        // Act
        var result = await controller.Pop("test-queue", require_ack: true, ttl_seconds: 30);

        // Assert - Should return HTTP 423 Locked
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(423, statusCodeResult.StatusCode);
        var response = Assert.IsType<ApiLockedResponse>(statusCodeResult.Value);
        Assert.Contains("locked", response.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// This test verifies the expected behavior for PopWithAck when queue is empty.
    ///
    /// Expected behavior:
    /// - Actor returns: IsEmpty=true, Locked=false, ItemJson=null
    /// - Controller should return: HTTP 204 No Content
    /// </summary>
    [Fact]
    public async Task Pop_WithRequireAck_EmptyQueue_Returns204()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(),
                "PopWithAck",
                It.IsAny<PopWithAckRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                ItemJson = null,
                Locked = false,
                IsEmpty = true,
                Message = "Queue is empty"
            });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);

        // Act
        var result = await controller.Pop("test-queue", require_ack: true, ttl_seconds: 30);

        // Assert - Should return HTTP 204 No Content
        Assert.IsType<NoContentResult>(result);
    }

    /// <summary>
    /// This test verifies the expected behavior for regular Pop when queue is locked.
    ///
    /// Expected behavior:
    /// - Actor returns: Locked=true, ItemJson=null
    /// - Controller should return: HTTP 423 Locked
    /// </summary>
    [Fact]
    public async Task Pop_WithoutRequireAck_WhenLocked_Returns423()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PopResponse>(
                It.IsAny<ActorId>(),
                "Pop",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PopResponse
            {
                ItemJson = null,
                Locked = true,
                IsEmpty = false,
                Message = "Queue is locked by another operation",
                LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
            });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);

        // Act
        var result = await controller.Pop("test-queue", require_ack: false);

        // Assert - Should return HTTP 423 Locked
        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(423, statusCodeResult.StatusCode);
    }

    /// <summary>
    /// This test verifies the expected behavior for regular Pop when successful.
    ///
    /// Expected behavior:
    /// - Actor returns: ItemJson="...", Locked=false, IsEmpty=false
    /// - Controller should return: HTTP 200 OK with ApiPopResponse
    /// </summary>
    [Fact]
    public async Task Pop_WithoutRequireAck_Success_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PopResponse>(
                It.IsAny<ActorId>(),
                "Pop",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PopResponse
            {
                ItemJson = "{\"id\":1}",
                Locked = false,
                IsEmpty = false
            });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);

        // Act
        var result = await controller.Pop("test-queue", require_ack: false);

        // Assert - Should return HTTP 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiPopResponse>(okResult.Value);
        Assert.NotNull(response.Item);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
    /// - Actor returns: Status="SUCCESS", DlqActorId="queue-id-deadletter"
    /// - Controller should return: HTTP 200 OK
    /// </summary>
    [Fact]
    public async Task DeadLetter_ValidLock_Returns200()
    {
        // Arrange
        var mockInvoker = new Mock<IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<DeadLetterRequest, DeadLetterResponse>(
                It.IsAny<ActorId>(),
                "DeadLetter",
                It.IsAny<DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeadLetterResponse
            {
                Status = "SUCCESS",
                DlqActorId = "test-queue-deadletter",
                Message = "Item moved to dead letter queue"
            });

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiDeadLetterRequest("valid-lock-123");

        // Act
        var result = await controller.DeadLetter("test-queue", request);

        // Assert - Should return HTTP 200 OK
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiDeadLetterResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("test-queue-deadletter", response.DlqActorId);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<IActorInvoker>();
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

        var controller = new QueueController(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiDeadLetterRequest("wrong-lock-id");

        // Act
        var result = await controller.DeadLetter("test-queue", request);

        // Assert - Should return HTTP 400 Bad Request
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
