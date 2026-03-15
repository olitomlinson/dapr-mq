using Dapr.Actors;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;
using DaprMQ.ApiServer.Services;
using ActorModels = DaprMQ.Interfaces;
using Xunit;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for DaprMQGrpcService to verify gRPC status code mappings
/// and response transformations from actor responses to gRPC messages.
/// </summary>
public class DaprMQGrpcServiceTests
{
    private readonly Mock<ILogger<DaprMQGrpcService>> _mockLogger;
    private readonly Mock<ServerCallContext> _mockContext;

    public DaprMQGrpcServiceTests()
    {
        _mockLogger = new Mock<ILogger<DaprMQGrpcService>>();
        _mockContext = new Mock<ServerCallContext>();
    }

    [Fact]
    public async Task Push_ValidRequest_ReturnsSuccess()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.PushRequest, ActorModels.PushResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.PushRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.PushResponse { Success = true, ItemsPushed = 1 });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiServer.Grpc.PushRequest
        {
            QueueId = "test-queue"
        };
        request.Items.Add(new ApiServer.Grpc.PushItem
        {
            ItemJson = "{\"id\":1,\"value\":\"test\"}",
            Priority = 1
        });

        // Act
        var response = await service.Push(request, _mockContext.Object);

        // Assert
        Assert.True(response.Success);
        Assert.Equal(1, response.ItemsPushed);
        Assert.NotEmpty(response.Message);
    }

    [Fact]
    public async Task Push_NegativePriority_ThrowsInvalidArgument()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiServer.Grpc.PushRequest
        {
            QueueId = "test-queue"
        };
        request.Items.Add(new ApiServer.Grpc.PushItem
        {
            ItemJson = "{\"id\":1}",
            Priority = -1
        });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await service.Push(request, _mockContext.Object));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Pop_EmptyQueue_ReturnsEmpty()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.PopResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.PopResponse { IsEmpty = true, Message = "Queue is empty" });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiServer.Grpc.PopRequest { QueueId = "test-queue" };

        // Act
        var response = await service.Pop(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.PopResponse.ResultOneofCase.Empty, response.ResultCase);
        Assert.NotNull(response.Empty);
    }

    [Fact]
    public async Task Pop_ItemLocked_ReturnsLocked()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.PopResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.PopResponse
            {
                Locked = true,
                Message = "Item is locked",
                LockExpiresAt = 1234567890.0
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiServer.Grpc.PopRequest { QueueId = "test-queue" };

        // Act
        var response = await service.Pop(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.PopResponse.ResultOneofCase.Locked, response.ResultCase);
        Assert.NotNull(response.Locked);
        Assert.Equal(1234567890.0, response.Locked.LockExpiresAt);
    }

    [Fact]
    public async Task PopWithAck_SuccessfulLockCreation_ReturnsSuccessWithLockId()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.PopWithAckRequest, ActorModels.PopWithAckResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.PopWithAckRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.PopWithAckResponse
            {
                ItemJson = "{\"id\":1}",
                Locked = true,
                LockId = "test-lock-123",
                LockExpiresAt = 1234567890.0
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiServer.Grpc.PopWithAckRequest
        {
            QueueId = "test-queue",
            TtlSeconds = 30
        };

        // Act
        var response = await service.PopWithAck(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.PopWithAckResponse.ResultOneofCase.Success, response.ResultCase);
        Assert.Equal("test-lock-123", response.Success.LockId);
        Assert.Equal(1234567890.0, response.Success.LockExpiresAt);
    }

    [Fact]
    public async Task Acknowledge_InvalidLockId_ThrowsInvalidArgument()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
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

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
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

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
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

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
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
    public async Task DeadLetter_Success_ReturnsDlqActorId()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ActorModels.DeadLetterRequest, ActorModels.DeadLetterResponse>(
                It.IsAny<ActorId>(),
                It.IsAny<string>(),
                It.IsAny<ActorModels.DeadLetterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DeadLetterResponse
            {
                Status = "SUCCESS",
                DlqActorId = "test-queue-deadletter",
                Message = "Item moved to dead letter queue"
            });

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
        var request = new ApiServer.Grpc.DeadLetterRequest
        {
            QueueId = "test-queue",
            LockId = "valid-lock"
        };

        // Act
        var response = await service.DeadLetter(request, _mockContext.Object);

        // Assert
        Assert.Equal(ApiServer.Grpc.DeadLetterResponse.ResultOneofCase.Success, response.ResultCase);
        Assert.Equal("test-queue-deadletter", response.Success.DlqActorId);
    }

    [Fact]
    public async Task DeadLetter_LockNotFound_ThrowsNotFound()
    {
        // Arrange
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
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

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
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

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
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
        var mockInvoker = new Mock<ActorModels.IActorInvoker>();
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

        var service = new DaprMQGrpcService(_mockLogger.Object, mockInvoker.Object);
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
}
