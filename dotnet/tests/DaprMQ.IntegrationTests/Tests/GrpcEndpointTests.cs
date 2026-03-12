using Grpc.Core;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class GrpcEndpointTests(DaprTestFixture fixture)
{
    private GrpcService.DaprMQClient CreateGrpcClient()
    {
        // Configure gRPC channel to use HTTP/2 without TLS (h2c protocol)
        // Use dedicated gRPC port (5001) which only supports HTTP/2
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        var channel = GrpcChannel.ForAddress(fixture.GrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler()
        });
        return new GrpcService.DaprMQClient(channel);
    }

    [Fact]
    public async Task Push_ValidItem_ReturnsSuccess()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new PushRequest
        {
            QueueId = queueId,
            ItemJson = "{\"test\":\"data\"}",
            Priority = 1
        };

        // Act
        var response = await client.PushAsync(request);

        // Assert
        Assert.True(response.Success);
        Assert.NotEmpty(response.Message);
    }

    [Fact]
    public async Task Push_NegativePriority_ThrowsInvalidArgument()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new PushRequest
        {
            QueueId = queueId,
            ItemJson = "{\"test\":\"data\"}",
            Priority = -1
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.PushAsync(request));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Pop_EmptyQueue_ReturnsEmpty()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new PopRequest { QueueId = queueId };

        // Act
        var response = await client.PopAsync(request);

        // Assert
        Assert.Equal(PopResponse.ResultOneofCase.Empty, response.ResultCase);
        Assert.NotNull(response.Empty);
    }

    [Fact]
    public async Task PopWithAck_EmptyQueue_ReturnsEmpty()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new PopWithAckRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };

        // Act
        var response = await client.PopWithAckAsync(request);

        // Assert
        Assert.Equal(PopWithAckResponse.ResultOneofCase.Empty, response.ResultCase);
        Assert.NotNull(response.Empty);
    }

    [Fact]
    public async Task PushAndPop_SingleItem_ReturnsItemInFifoOrder()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var pushRequest = new PushRequest
        {
            QueueId = queueId,
            ItemJson = "{\"id\":42,\"name\":\"test-item\"}",
            Priority = 1
        };

        // Act - Push
        var pushResponse = await client.PushAsync(pushRequest);
        Assert.True(pushResponse.Success);

        // Act - Pop
        var popRequest = new PopRequest { QueueId = queueId };
        var popResponse = await client.PopAsync(popRequest);

        // Assert
        Assert.Equal(PopResponse.ResultOneofCase.Success, popResponse.ResultCase);
        Assert.Equal("{\"id\":42,\"name\":\"test-item\"}", popResponse.Success.ItemJson);
    }

    [Fact]
    public async Task PushAndPopWithAck_ReturnsLockId()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var pushRequest = new PushRequest
        {
            QueueId = queueId,
            ItemJson = "{\"test\":\"ack-flow\"}",
            Priority = 1
        };

        // Act - Push
        await client.PushAsync(pushRequest);

        // Act - PopWithAck
        var popRequest = new PopWithAckRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };
        var popResponse = await client.PopWithAckAsync(popRequest);

        // Assert
        Assert.Equal(PopWithAckResponse.ResultOneofCase.Success, popResponse.ResultCase);
        Assert.NotEmpty(popResponse.Success.LockId);
        Assert.True(popResponse.Success.LockExpiresAt > 0);
    }

    [Fact]
    public async Task Acknowledge_ValidLockId_RemovesItem()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var pushRequest = new PushRequest
        {
            QueueId = queueId,
            ItemJson = "{\"test\":\"acknowledge\"}",
            Priority = 1
        };

        await client.PushAsync(pushRequest);

        var popRequest = new PopWithAckRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };
        var popResponse = await client.PopWithAckAsync(popRequest);
        var lockId = popResponse.Success.LockId;

        // Act
        var ackRequest = new AcknowledgeRequest
        {
            QueueId = queueId,
            LockId = lockId
        };
        var ackResponse = await client.AcknowledgeAsync(ackRequest);

        // Assert
        Assert.True(ackResponse.Success);
        Assert.Equal(1, ackResponse.ItemsAcknowledged);
        Assert.Empty(ackResponse.ErrorCode);
    }

    [Fact]
    public async Task Acknowledge_InvalidLockId_ThrowsNotFound()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new AcknowledgeRequest
        {
            QueueId = queueId,
            LockId = "invalid-lock-id"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.AcknowledgeAsync(request));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task ExtendLock_ValidLockId_ExtendsExpiry()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var pushRequest = new PushRequest
        {
            QueueId = queueId,
            ItemJson = "{\"test\":\"extend\"}",
            Priority = 1
        };

        await client.PushAsync(pushRequest);

        var popRequest = new PopWithAckRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };
        var popResponse = await client.PopWithAckAsync(popRequest);
        var lockId = popResponse.Success.LockId;
        var originalExpiry = popResponse.Success.LockExpiresAt;

        // Act
        var extendRequest = new ExtendLockRequest
        {
            QueueId = queueId,
            LockId = lockId,
            AdditionalTtlSeconds = 30
        };
        var extendResponse = await client.ExtendLockAsync(extendRequest);

        // Assert
        Assert.True(extendResponse.Success);
        Assert.True(extendResponse.NewExpiresAt > originalExpiry);
        Assert.Empty(extendResponse.ErrorCode);
    }

    [Fact]
    public async Task ExtendLock_InvalidLockId_ThrowsNotFound()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new ExtendLockRequest
        {
            QueueId = queueId,
            LockId = "invalid-lock-id",
            AdditionalTtlSeconds = 30
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.ExtendLockAsync(request));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }
}
