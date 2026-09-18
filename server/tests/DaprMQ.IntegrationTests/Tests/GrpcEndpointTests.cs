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
    public async Task Enqueue_ValidSingleItem_ReturnsSuccess()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new EnqueueRequest
        {
            QueueId = queueId
        };
        request.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"test\":\"data\"}",
            Priority = 1
        });

        // Act
        var response = await client.EnqueueAsync(request);

        // Assert
        Assert.True(response.Success);
        Assert.Equal(1, response.ItemsEnqueued);
        Assert.NotEmpty(response.Message);
    }

    [Fact]
    public async Task Enqueue_ValidMultipleItems_ReturnsSuccess()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new EnqueueRequest
        {
            QueueId = queueId
        };
        request.Items.Add(new EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 });
        request.Items.Add(new EnqueueItem { ItemJson = "{\"id\":2}", Priority = 0 });
        request.Items.Add(new EnqueueItem { ItemJson = "{\"id\":3}", Priority = 1 });

        // Act
        var response = await client.EnqueueAsync(request);

        // Assert
        Assert.True(response.Success);
        Assert.Equal(3, response.ItemsEnqueued);
    }

    [Fact]
    public async Task Enqueue_EmptyArray_ThrowsInvalidArgument()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new EnqueueRequest { QueueId = queueId };
        // Don't add any items

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.EnqueueAsync(request));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Enqueue_NegativePriority_ThrowsInvalidArgument()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new EnqueueRequest { QueueId = queueId };
        request.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"test\":\"data\"}",
            Priority = -1
        });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.EnqueueAsync(request));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Dequeue_EmptyQueue_ReturnsEmpty()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new DequeueRequest { QueueId = queueId };

        // Act
        var response = await client.DequeueAsync(request);

        // Assert
        Assert.Equal(DequeueResponse.ResultOneofCase.Empty, response.ResultCase);
        Assert.NotNull(response.Empty);
    }

    [Fact]
    public async Task DequeueLocked_EmptyQueue_ReturnsEmpty()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var request = new DequeueLockedRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };

        // Act
        var response = await client.DequeueLockedAsync(request);

        // Assert
        Assert.Equal(DequeueLockedResponse.ResultOneofCase.Empty, response.ResultCase);
        Assert.NotNull(response.Empty);
    }

    [Fact]
    public async Task EnqueueAndDequeue_SingleItem_ReturnsItemInFifoOrder()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var enqueueRequest = new EnqueueRequest { QueueId = queueId };
        enqueueRequest.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"id\":42,\"name\":\"test-item\"}",
            Priority = 1
        });

        // Act - Enqueue
        var enqueueResponse = await client.EnqueueAsync(enqueueRequest);
        Assert.True(enqueueResponse.Success);

        // Act - Dequeue
        var dequeueRequest = new DequeueRequest { QueueId = queueId };
        var dequeueResponse = await client.DequeueAsync(dequeueRequest);

        // Assert
        Assert.Equal(DequeueResponse.ResultOneofCase.Success, dequeueResponse.ResultCase);
        Assert.Single(dequeueResponse.Success.ItemJson);
        Assert.Equal("{\"id\":42,\"name\":\"test-item\"}", dequeueResponse.Success.ItemJson[0]);
    }

    [Fact]
    public async Task EnqueueAndDequeueLocked_ReturnsLockId()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var enqueueRequest = new EnqueueRequest { QueueId = queueId };
        enqueueRequest.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"test\":\"ack-flow\"}",
            Priority = 1
        });

        // Act - Enqueue
        await client.EnqueueAsync(enqueueRequest);

        // Act - DequeueLocked
        var dequeueRequest = new DequeueLockedRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };
        var dequeueResponse = await client.DequeueLockedAsync(dequeueRequest);

        // Assert
        Assert.Equal(DequeueLockedResponse.ResultOneofCase.Success, dequeueResponse.ResultCase);
        Assert.Single(dequeueResponse.Success.LockId);
        Assert.NotEmpty(dequeueResponse.Success.LockId[0]);
        Assert.True(dequeueResponse.Success.LockExpiresAt[0] > 0);
    }

    [Fact]
    public async Task Acknowledge_ValidLockId_RemovesItem()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-{Guid.NewGuid()}";
        var client = CreateGrpcClient();
        var enqueueRequest = new EnqueueRequest { QueueId = queueId };
        enqueueRequest.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"test\":\"acknowledge\"}",
            Priority = 1
        });

        await client.EnqueueAsync(enqueueRequest);

        var dequeueRequest = new DequeueLockedRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };
        var dequeueResponse = await client.DequeueLockedAsync(dequeueRequest);
        var lockId = dequeueResponse.Success.LockId[0];

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
        var enqueueRequest = new EnqueueRequest { QueueId = queueId };
        enqueueRequest.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"test\":\"extend\"}",
            Priority = 1
        });

        await client.EnqueueAsync(enqueueRequest);

        var dequeueRequest = new DequeueLockedRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        };
        var dequeueResponse = await client.DequeueLockedAsync(dequeueRequest);
        var lockId = dequeueResponse.Success.LockId[0];
        var originalExpiry = dequeueResponse.Success.LockExpiresAt[0];

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
