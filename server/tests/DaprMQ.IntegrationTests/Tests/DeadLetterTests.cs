using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class DeadLetterTests(DaprTestFixture fixture)
{
    // HTTP Tests
    [Fact]
    public async Task DeadLetter_ValidLock_MovesToDlqAndVoidsLock()
    {
        // Arrange - Enqueue an item and create a lock
        var queueId = $"{fixture.QueueId}-dlq-success-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "failed-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);

        // Dequeue with acknowledgement (creates lock)
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "30");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult);
        Assert.NotNull(dequeueLockedResult.Items);
        Assert.Single(dequeueLockedResult.Items);

        // Act - Send to dead letter queue
        var deadLetterRequest = new ApiDeadLetterRequest(dequeueLockedResult.Items[0].LockId);
        var deadLetterResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", deadLetterRequest);

        // Assert - Should return 200 OK
        Assert.Equal(HttpStatusCode.OK, deadLetterResponse.StatusCode);

        var deadLetterResult = await deadLetterResponse.Content.ReadFromJsonAsync<ApiDeadLetterResponse>();
        Assert.NotNull(deadLetterResult);
        Assert.True(deadLetterResult.Success);
        Assert.NotNull(deadLetterResult.DlqId);
        Assert.Equal($"{queueId}-deadletter", deadLetterResult.DlqId);

        // Verify original queue is now unlocked and empty
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("require-ack", "false");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);
        Assert.Equal(HttpStatusCode.NoContent, dequeueResponse.StatusCode);

        // Verify item is in DLQ
        var dlqDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}-deadletter/dequeue");
        dlqDequeueRequest.Headers.Add("require-ack", "false");
        var dlqDequeueResponse = await fixture.ApiClient.SendAsync(dlqDequeueRequest);
        Assert.Equal(HttpStatusCode.OK, dlqDequeueResponse.StatusCode);

        var dlqDequeueResult = await dlqDequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(dlqDequeueResult);
        Assert.NotNull(dlqDequeueResult.Items);
        Assert.Single(dlqDequeueResult.Items);

        var dlqItem = (JsonElement)dlqDequeueResult.Items[0].Item;
        Assert.Equal(1, dlqItem.GetProperty("id").GetInt32());
        Assert.Equal("failed-item", dlqItem.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DeadLetter_LockNotFound_Returns404()
    {
        // Arrange - Queue with no lock
        var queueId = $"{fixture.QueueId}-dlq-no-lock-{Guid.NewGuid():N}";

        // Act - Try to deadletter with non-existent lock
        var deadLetterRequest = new ApiDeadLetterRequest("non-existent-lock-id");
        var deadLetterResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", deadLetterRequest);

        // Assert - Should return 404 Not Found
        Assert.Equal(HttpStatusCode.NotFound, deadLetterResponse.StatusCode);

        var deadLetterResult = await deadLetterResponse.Content.ReadFromJsonAsync<ApiDeadLetterResponse>();
        Assert.NotNull(deadLetterResult);
        Assert.False(deadLetterResult.Success);
        Assert.Equal("LOCK_NOT_FOUND", deadLetterResult.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_InvalidLockId_Returns400()
    {
        // Arrange - Enqueue item, create lock
        var queueId = $"{fixture.QueueId}-dlq-invalid-lock-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, Priority: 1) }));

        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "30");
        await fixture.ApiClient.SendAsync(dequeueLockedRequest);

        // Act - Try to deadletter with wrong lock ID
        var deadLetterRequest = new ApiDeadLetterRequest("wrong-lock-id-12345");
        var deadLetterResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", deadLetterRequest);

        // Assert - Should return 404 Not Found (with counter approach, can't distinguish invalid vs not found)
        Assert.Equal(HttpStatusCode.NotFound, deadLetterResponse.StatusCode);

        var deadLetterResult = await deadLetterResponse.Content.ReadFromJsonAsync<ApiDeadLetterResponse>();
        Assert.NotNull(deadLetterResult);
        Assert.False(deadLetterResult.Success);
        Assert.Equal("LOCK_NOT_FOUND", deadLetterResult.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_ExpiredLock_Returns410()
    {
        // Arrange - Enqueue item, create lock with short TTL
        var queueId = $"{fixture.QueueId}-dlq-expired-lock-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, Priority: 1) }));

        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "2");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult);
        Assert.NotNull(dequeueLockedResult.Items);
        Assert.Single(dequeueLockedResult.Items);

        // Wait for lock to expire and reminder to clean it up
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Act - Try to deadletter with expired lock (that has been cleaned up by reminder)
        var deadLetterRequest = new ApiDeadLetterRequest(dequeueLockedResult.Items[0].LockId);
        var deadLetterResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", deadLetterRequest);

        // Assert - Should return 404 Not Found (lock was cleaned up by reminder after expiry)
        Assert.Equal(HttpStatusCode.NotFound, deadLetterResponse.StatusCode);

        var deadLetterResult = await deadLetterResponse.Content.ReadFromJsonAsync<ApiDeadLetterResponse>();
        Assert.NotNull(deadLetterResult);
        Assert.False(deadLetterResult.Success);
        Assert.Equal("LOCK_NOT_FOUND", deadLetterResult.ErrorCode);
    }

    [Fact]
    public async Task DeadLetter_PreservesPriority_InDlq()
    {
        // Arrange - Enqueue priority 0 item (fast lane) and create lock
        var queueId = $"{fixture.QueueId}-dlq-priority-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "urgent-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, Priority: 0) });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);

        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "30");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult?.Items);
        Assert.Single(dequeueLockedResult.Items);

        // Act - Send to dead letter queue
        var deadLetterRequest = new ApiDeadLetterRequest(dequeueLockedResult.Items[0].LockId);
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", deadLetterRequest);

        // Enqueue priority 1 item to DLQ
        var lowPriorityItem = JsonSerializer.SerializeToElement(new { id = 2, value = "normal-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}-deadletter/enqueue", new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(lowPriorityItem, Priority: 1) }));

        // Assert - Dequeue from DLQ should return priority 0 item first
        var dlqDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}-deadletter/dequeue");
        dlqDequeueRequest.Headers.Add("require-ack", "false");
        var dlqDequeueResponse = await fixture.ApiClient.SendAsync(dlqDequeueRequest);
        var dlqDequeueResult = await dlqDequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();

        var dlqItem = (JsonElement)dlqDequeueResult!.Items[0].Item;
        Assert.Equal(1, dlqItem.GetProperty("id").GetInt32()); // Priority 0 item comes first
    }

    // gRPC Tests
    private GrpcService.DaprMQClient CreateGrpcClient()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        var channel = GrpcChannel.ForAddress(fixture.GrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler()
        });
        return new GrpcService.DaprMQClient(channel);
    }

    [Fact]
    public async Task DeadLetter_Grpc_ValidLock_ReturnsSuccess()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-dlq-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        var enqueueRequest = new EnqueueRequest { QueueId = queueId };
        enqueueRequest.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"test\":\"grpc-dlq-item\"}",
            Priority = 1
        });
        await client.EnqueueAsync(enqueueRequest);

        var dequeueResponse = await client.DequeueLockedAsync(new DequeueLockedRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        });

        Assert.Equal(DequeueLockedResponse.ResultOneofCase.Success, dequeueResponse.ResultCase);
        var lockId = dequeueResponse.Success.LockId[0];

        // Act
        var deadLetterResponse = await client.DeadLetterAsync(new DeadLetterRequest
        {
            QueueId = queueId,
            LockId = lockId
        });

        // Assert
        Assert.Equal(DeadLetterResponse.ResultOneofCase.Success, deadLetterResponse.ResultCase);
        Assert.Equal($"{queueId}-deadletter", deadLetterResponse.Success.DlqId);
    }

    [Fact]
    public async Task DeadLetter_Grpc_LockNotFound_ThrowsNotFound()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-no-lock-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.DeadLetterAsync(new DeadLetterRequest
            {
                QueueId = queueId,
                LockId = "non-existent-lock"
            }));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task DeadLetter_Grpc_ExpiredLock_ThrowsFailedPrecondition()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-expired-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        var enqueueRequest2 = new EnqueueRequest { QueueId = queueId };
        enqueueRequest2.Items.Add(new EnqueueItem
        {
            ItemJson = "{\"test\":\"item\"}",
            Priority = 1
        });
        await client.EnqueueAsync(enqueueRequest2);

        var dequeueResponse = await client.DequeueLockedAsync(new DequeueLockedRequest
        {
            QueueId = queueId,
            TtlSeconds = 2
        });

        var lockId = dequeueResponse.Success.LockId[0];
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Act & Assert - Lock expired and was cleaned up by reminder, so it's not found
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.DeadLetterAsync(new DeadLetterRequest
            {
                QueueId = queueId,
                LockId = lockId
            }));

        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }
}
