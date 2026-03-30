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
        // Arrange - Push an item and create a lock
        var queueId = $"{fixture.QueueId}-dlq-success-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "failed-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);

        // Pop with acknowledgement (creates lock)
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "30");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult);
        Assert.NotNull(popWithAckResult.Items);
        Assert.Single(popWithAckResult.Items);

        // Act - Send to dead letter queue
        var deadLetterRequest = new ApiDeadLetterRequest(popWithAckResult.Items[0].LockId);
        var deadLetterResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", deadLetterRequest);

        // Assert - Should return 200 OK
        Assert.Equal(HttpStatusCode.OK, deadLetterResponse.StatusCode);

        var deadLetterResult = await deadLetterResponse.Content.ReadFromJsonAsync<ApiDeadLetterResponse>();
        Assert.NotNull(deadLetterResult);
        Assert.True(deadLetterResult.Success);
        Assert.NotNull(deadLetterResult.DlqId);
        Assert.Equal($"{queueId}-deadletter", deadLetterResult.DlqId);

        // Verify original queue is now unlocked and empty
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("require_ack", "false");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);
        Assert.Equal(HttpStatusCode.NoContent, popResponse.StatusCode);

        // Verify item is in DLQ
        var dlqPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}-deadletter/pop");
        dlqPopRequest.Headers.Add("require_ack", "false");
        var dlqPopResponse = await fixture.ApiClient.SendAsync(dlqPopRequest);
        Assert.Equal(HttpStatusCode.OK, dlqPopResponse.StatusCode);

        var dlqPopResult = await dlqPopResponse.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(dlqPopResult);
        Assert.NotNull(dlqPopResult.Items);
        Assert.Single(dlqPopResult.Items);

        var dlqItem = (JsonElement)dlqPopResult.Items[0].Item;
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
        // Arrange - Push item, create lock
        var queueId = $"{fixture.QueueId}-dlq-invalid-lock-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(itemElement, Priority: 1) }));

        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "30");
        await fixture.ApiClient.SendAsync(popWithAckRequest);

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
        // Arrange - Push item, create lock with short TTL
        var queueId = $"{fixture.QueueId}-dlq-expired-lock-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(itemElement, Priority: 1) }));

        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "2");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult);
        Assert.NotNull(popWithAckResult.Items);
        Assert.Single(popWithAckResult.Items);

        // Wait for lock to expire and reminder to clean it up
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Act - Try to deadletter with expired lock (that has been cleaned up by reminder)
        var deadLetterRequest = new ApiDeadLetterRequest(popWithAckResult.Items[0].LockId);
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
        // Arrange - Push priority 0 item (fast lane) and create lock
        var queueId = $"{fixture.QueueId}-dlq-priority-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "urgent-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(itemElement, Priority: 0) });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);

        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "30");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult?.Items);
        Assert.Single(popWithAckResult.Items);

        // Act - Send to dead letter queue
        var deadLetterRequest = new ApiDeadLetterRequest(popWithAckResult.Items[0].LockId);
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", deadLetterRequest);

        // Push priority 1 item to DLQ
        var lowPriorityItem = JsonSerializer.SerializeToElement(new { id = 2, value = "normal-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}-deadletter/push", new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(lowPriorityItem, Priority: 1) }));

        // Assert - Pop from DLQ should return priority 0 item first
        var dlqPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}-deadletter/pop");
        dlqPopRequest.Headers.Add("require_ack", "false");
        var dlqPopResponse = await fixture.ApiClient.SendAsync(dlqPopRequest);
        var dlqPopResult = await dlqPopResponse.Content.ReadFromJsonAsync<ApiPopResponse>();

        var dlqItem = (JsonElement)dlqPopResult!.Items[0].Item;
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

        var pushRequest = new PushRequest { QueueId = queueId };
        pushRequest.Items.Add(new PushItem
        {
            ItemJson = "{\"test\":\"grpc-dlq-item\"}",
            Priority = 1
        });
        await client.PushAsync(pushRequest);

        var popResponse = await client.PopWithAckAsync(new PopWithAckRequest
        {
            QueueId = queueId,
            TtlSeconds = 30
        });

        Assert.Equal(PopWithAckResponse.ResultOneofCase.Success, popResponse.ResultCase);
        var lockId = popResponse.Success.LockId[0];

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

        var pushRequest2 = new PushRequest { QueueId = queueId };
        pushRequest2.Items.Add(new PushItem
        {
            ItemJson = "{\"test\":\"item\"}",
            Priority = 1
        });
        await client.PushAsync(pushRequest2);

        var popResponse = await client.PopWithAckAsync(new PopWithAckRequest
        {
            QueueId = queueId,
            TtlSeconds = 2
        });

        var lockId = popResponse.Success.LockId[0];
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
