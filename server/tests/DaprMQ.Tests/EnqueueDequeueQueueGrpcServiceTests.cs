using Dapr.Actors;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using ActorModels = DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for DaprMQGrpcService to verify gRPC status code mappings
/// and response transformations from actor responses to gRPC messages.
/// </summary>
public class DaprMQGrpcServiceTests
{
    private readonly Mock<ILogger<DaprMQGrpcService>> _mockLogger;
    private readonly Mock<ServerCallContext> _mockContext;
    private readonly Mock<ISessionCoordinatorActorInvoker> _mockSessionCoordinatorActorInvoker;

    public DaprMQGrpcServiceTests()
    {
        _mockLogger = new Mock<ILogger<DaprMQGrpcService>>();
        _mockContext = new Mock<ServerCallContext>();
        _mockSessionCoordinatorActorInvoker = new Mock<ISessionCoordinatorActorInvoker>();
    }

    [Fact]
    public async Task Enqueue_ValidRequest_ReturnsSuccess()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest
        {
            QueueId = "test-queue"
        };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem
        {
            ItemJson = "{\"id\":1,\"value\":\"test\"}",
            Priority = 1
        });

        // Act
        var response = await service.Enqueue(request, _mockContext.Object);

        // Assert
        Assert.True(response.Success);
        Assert.Equal(1, response.ItemsEnqueued);
        Assert.NotEmpty(response.Message);
    }

    [Fact]
    public async Task Enqueue_WithIdempotencyKey_MapsToActorEnqueueItem()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorModels.EnqueueRequest? capturedRequest = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.EnqueueRequest, CancellationToken>((_, _, req, _) => capturedRequest = req)
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest { QueueId = "test-queue" };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem
        {
            ItemJson = "{\"id\":1}",
            Priority = 1,
            IdempotencyKey = "grpc-key-789"
        });

        // Act
        await service.Enqueue(request, _mockContext.Object);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Equal("grpc-key-789", capturedRequest!.Items[0].IdempotencyKey);
    }

    [Fact]
    public async Task Enqueue_WithoutIdempotencyKey_LeavesKeyNull()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        ActorModels.EnqueueRequest? capturedRequest = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.EnqueueRequest, CancellationToken>((_, _, req, _) => capturedRequest = req)
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1 });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest { QueueId = "test-queue" };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 });

        // Act
        await service.Enqueue(request, _mockContext.Object);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest!.Items[0].IdempotencyKey);
    }

    [Fact]
    public async Task Enqueue_ResponseIncludesItemsDeduplicated()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.EnqueueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1, ItemsDeduplicated = 2 });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest { QueueId = "test-queue" };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 });

        // Act
        var response = await service.Enqueue(request, _mockContext.Object);

        // Assert
        Assert.Equal(2, response.ItemsDeduplicated);
    }

    [Fact]
    public async Task Enqueue_NegativePriority_ThrowsInvalidArgument()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.EnqueueRequest
        {
            QueueId = "test-queue"
        };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem
        {
            ItemJson = "{\"id\":1}",
            Priority = -1
        });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await service.Enqueue(request, _mockContext.Object));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Dequeue_EmptyQueue_ReturnsEmpty()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueResponse { IsEmpty = true, Message = "Queue is empty" });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DequeueRequest { QueueId = "test-queue" };

        // Act
        var response = await service.Dequeue(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.DequeueResponse.ResultOneofCase.Empty, response.ResultCase);
        Assert.NotNull(response.Empty);
    }

    [Fact]
    public async Task Dequeue_ItemLocked_ReturnsLocked()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueResponse
            {
                Locked = true,
                Message = "Item is locked",
                LockExpiresAt = 1234567890.0
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DequeueRequest { QueueId = "test-queue" };

        // Act
        var response = await service.Dequeue(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.DequeueResponse.ResultOneofCase.Locked, response.ResultCase);
        Assert.NotNull(response.Locked);
        Assert.Equal(1234567890.0, response.Locked.LockExpiresAt);
    }

    [Fact]
    public async Task DequeueLocked_SuccessfulLockCreation_ReturnsSuccessWithLockId()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DequeueLockedRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse
            {
                Items = new List<ActorModels.DequeueLockedItem>
                {
                    new ActorModels.DequeueLockedItem
                    {
                        ItemJson = "{\"id\":1}",
                        Priority = 1,
                        LockId = "test-lock-123",
                        LockExpiresAt = 1234567890.0
                    }
                },
                Locked = false
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DequeueLockedRequest
        {
            QueueId = "test-queue",
            TtlSeconds = 30
        };

        // Act
        var response = await service.DequeueLocked(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.DequeueLockedResponse.ResultOneofCase.Success, response.ResultCase);
        Assert.Single(response.Success.LockId);
        Assert.Equal("test-lock-123", response.Success.LockId[0]);
        Assert.Equal(1234567890.0, response.Success.LockExpiresAt[0]);
    }

    [Fact]
    public async Task Acknowledge_InvalidLockId_ThrowsInvalidArgument()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.AcknowledgeRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcknowledgeResponse
            {
                Success = false,
                ErrorCode = "INVALID_LOCK_ID",
                Message = "Invalid lock ID"
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.AcknowledgeRequest
        {
            QueueId = "test-queue",
            LockId = "invalid"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await service.Acknowledge(request, _mockContext.Object));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Acknowledge_LockExpired_ThrowsFailedPrecondition()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.AcknowledgeRequest, ActorModels.AcknowledgeResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.AcknowledgeRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.AcknowledgeResponse
            {
                Success = false,
                ErrorCode = "LOCK_EXPIRED",
                Message = "Lock has expired"
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.AcknowledgeRequest
        {
            QueueId = "test-queue",
            LockId = "expired-lock"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await service.Acknowledge(request, _mockContext.Object));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task ExtendLock_Success_ReturnsNewExpiry()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.ExtendLockRequest, ActorModels.ExtendLockResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.ExtendLockRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.ExtendLockResponse
            {
                Success = true,
                NewExpiresAt = 1234567920.0
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.ExtendLockRequest
        {
            QueueId = "test-queue",
            LockId = "valid-lock",
            AdditionalTtlSeconds = 30
        };

        // Act
        var response = await service.ExtendLock(request, _mockContext.Object);

        // Assert
        Assert.True(response.Success);
        Assert.Equal(1234567920.0, response.NewExpiresAt);
    }

    [Fact]
    public async Task DeadLetter_Success_ReturnsDlqId()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DeadLetterResponse
            {
                Status = "SUCCESS",
                DlqId = "test-queue-deadletter",
                Message = "Item moved to dead letter queue"
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DeadLetterRequest
        {
            QueueId = "test-queue",
            LockId = "valid-lock"
        };

        // Act
        var response = await service.DeadLetter(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.DeadLetterResponse.ResultOneofCase.Success, response.ResultCase);
        Assert.Equal("test-queue-deadletter", response.Success.DlqId);
    }

    [Fact]
    public async Task DeadLetter_LockNotFound_ThrowsNotFound()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DeadLetterResponse
            {
                Status = "ERROR",
                ErrorCode = "LOCK_NOT_FOUND",
                Message = "No active lock found"
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DeadLetterRequest
        {
            QueueId = "test-queue",
            LockId = "nonexistent-lock"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await service.DeadLetter(request, _mockContext.Object));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task DeadLetter_LockExpired_ThrowsFailedPrecondition()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DeadLetterResponse
            {
                Status = "ERROR",
                ErrorCode = "LOCK_EXPIRED",
                Message = "Lock has expired"
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DeadLetterRequest
        {
            QueueId = "test-queue",
            LockId = "expired-lock"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await service.DeadLetter(request, _mockContext.Object));
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task DeadLetter_InvalidLockId_ThrowsInvalidArgument()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DeadLetterResponse
            {
                Status = "ERROR",
                ErrorCode = "INVALID_LOCK_ID",
                Message = "Invalid lock ID provided"
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DeadLetterRequest
        {
            QueueId = "test-queue",
            LockId = "invalid-lock"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await service.DeadLetter(request, _mockContext.Object));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ===== Bulk Dequeue gRPC Tests =====

    /// <summary>
    /// This test verifies gRPC Dequeue with count parameter returns repeated fields.
    ///
    /// Expected behavior:
    /// - Actor returns: Items=[item1, item2, item3]
    /// - gRPC should return: Success with repeated item_json and priority arrays
    /// </summary>
    [Fact]
    public async Task Dequeue_WithCount_ReturnsRepeatedFields()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DequeueRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueResponse
            {
                Items = new List<ActorModels.DequeueItem>
                {
                    new ActorModels.DequeueItem { ItemJson = "{\"id\":1}", Priority = 1 },
                    new ActorModels.DequeueItem { ItemJson = "{\"id\":2}", Priority = 1 },
                    new ActorModels.DequeueItem { ItemJson = "{\"id\":3}", Priority = 0 }
                },
                Locked = false,
                IsEmpty = false
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DequeueRequest
        {
            QueueId = "test-queue",
            Count = 3
        };

        // Act
        var response = await service.Dequeue(request, _mockContext.Object);

        // Assert - Should return Success with repeated fields
        Assert.Equal(ApiServer.Grpc.DequeueResponse.ResultOneofCase.Success, response.ResultCase);
        Assert.NotNull(response.Success);
        Assert.Equal(3, response.Success.ItemJson.Count);
        Assert.Equal(3, response.Success.Priority.Count);
        Assert.Equal("{\"id\":1}", response.Success.ItemJson[0]);
        Assert.Equal("{\"id\":2}", response.Success.ItemJson[1]);
        Assert.Equal("{\"id\":3}", response.Success.ItemJson[2]);
        Assert.Equal(1, response.Success.Priority[0]);
        Assert.Equal(1, response.Success.Priority[1]);
        Assert.Equal(0, response.Success.Priority[2]);
    }

    /// <summary>
    /// This test verifies gRPC DequeueLocked with count parameter returns repeated lock fields.
    ///
    /// Expected behavior:
    /// - Actor returns: Items=[item1, item2, item3] with lock IDs
    /// - gRPC should return: Success with repeated item_json, priority, lock_id, lock_expires_at arrays
    /// </summary>
    [Fact]
    public async Task DequeueLocked_WithCount_ReturnsRepeatedLockFields()
    {
        // Arrange
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var expiresAt = 1234567890.0;
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DequeueLockedRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse
            {
                Items = new List<ActorModels.DequeueLockedItem>
                {
                    new ActorModels.DequeueLockedItem
                    {
                        ItemJson = "{\"id\":1}",
                        Priority = 1,
                        LockId = "lock-1",
                        LockExpiresAt = expiresAt
                    },
                    new ActorModels.DequeueLockedItem
                    {
                        ItemJson = "{\"id\":2}",
                        Priority = 1,
                        LockId = "lock-2",
                        LockExpiresAt = expiresAt + 1
                    },
                    new ActorModels.DequeueLockedItem
                    {
                        ItemJson = "{\"id\":3}",
                        Priority = 0,
                        LockId = "lock-3",
                        LockExpiresAt = expiresAt + 2
                    }
                },
                Locked = false,
                IsEmpty = false
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object, _mockSessionCoordinatorActorInvoker.Object);
        var request = new ApiServer.Grpc.DequeueLockedRequest
        {
            QueueId = "test-queue",
            TtlSeconds = 30,
            Count = 3
        };

        // Act
        var response = await service.DequeueLocked(request, _mockContext.Object);

        // Assert - Should return Success with repeated fields including lock metadata
        Assert.Equal(ApiServer.Grpc.DequeueLockedResponse.ResultOneofCase.Success, response.ResultCase);
        Assert.NotNull(response.Success);
        Assert.Equal(3, response.Success.ItemJson.Count);
        Assert.Equal(3, response.Success.Priority.Count);
        Assert.Equal(3, response.Success.LockId.Count);
        Assert.Equal(3, response.Success.LockExpiresAt.Count);
        Assert.Equal("{\"id\":1}", response.Success.ItemJson[0]);
        Assert.Equal("{\"id\":2}", response.Success.ItemJson[1]);
        Assert.Equal("{\"id\":3}", response.Success.ItemJson[2]);
        Assert.Equal("lock-1", response.Success.LockId[0]);
        Assert.Equal("lock-2", response.Success.LockId[1]);
        Assert.Equal("lock-3", response.Success.LockId[2]);
        Assert.Equal(expiresAt, response.Success.LockExpiresAt[0]);
        Assert.Equal(expiresAt + 1, response.Success.LockExpiresAt[1]);
        Assert.Equal(expiresAt + 2, response.Success.LockExpiresAt[2]);
    }
}
