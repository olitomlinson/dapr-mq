using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class LockAndAcknowledgementTests(DaprTestFixture fixture)
{
    [Fact]
    public async Task DequeueLocked_CreatesLock_BlocksRegularDequeue()
    {
        // Arrange - Enqueue an item
        var queueId = $"{fixture.QueueId}-lock-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Act - Dequeue with acknowledgement (creates lock)
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "30");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult);
        Assert.NotNull(dequeueLockedResult.Items);
        Assert.Single(dequeueLockedResult.Items);
        Assert.NotNull(dequeueLockedResult.Items[0].Item);
        Assert.NotNull(dequeueLockedResult.Items[0].LockId);
        Assert.True(dequeueLockedResult.Items[0].LockExpiresAt > 0);

        // Attempt regular Dequeue - should be blocked with HTTP 423
        var blockedDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        blockedDequeueRequest.Headers.Add("require-ack", "false");
        var blockedDequeueResponse = await fixture.ApiClient.SendAsync(blockedDequeueRequest);

        // Assert - Should return 423 Locked
        Assert.Equal(HttpStatusCode.Locked, blockedDequeueResponse.StatusCode);

        var lockedResponse = await blockedDequeueResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();
        Assert.NotNull(lockedResponse);
        Assert.NotNull(lockedResponse.Message);
        Assert.Contains("locked", lockedResponse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DequeueLocked_AfterAcknowledge_AllowsRegularDequeue()
    {
        // Arrange - Enqueue two items
        var queueId = $"{fixture.QueueId}-ack-test-{Guid.NewGuid():N}";

        var item1 = JsonSerializer.SerializeToElement(new { id = 1, value = "first-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(item1, Priority: 1) }));

        var item2 = JsonSerializer.SerializeToElement(new { id = 2, value = "second-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(item2, Priority: 1) }));

        // Act - Dequeue with acknowledgement
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "30");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult);
        Assert.NotNull(dequeueLockedResult.Items);
        Assert.Single(dequeueLockedResult.Items);

        // Acknowledge the lock
        var ackRequest = new ApiAcknowledgeRequest(dequeueLockedResult.Items[0].LockId);
        var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", ackRequest);
        ackResponse.EnsureSuccessStatusCode();

        var ackResult = await ackResponse.Content.ReadFromJsonAsync<ApiAcknowledgeResponse>();
        Assert.NotNull(ackResult);
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Now regular Dequeue should work
        var regularDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        regularDequeueRequest.Headers.Add("require-ack", "false");
        var regularDequeueResponse = await fixture.ApiClient.SendAsync(regularDequeueRequest);

        // Assert - Should return 200 OK with second item
        Assert.Equal(HttpStatusCode.OK, regularDequeueResponse.StatusCode);

        var dequeueResult = await regularDequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(dequeueResult);
        Assert.NotNull(dequeueResult.Items);
        Assert.Single(dequeueResult.Items);

        var itemElement = (JsonElement)dequeueResult.Items[0].Item;
        Assert.Equal(2, itemElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task DequeueLocked_MultipleDequeueAttempts_AllBlocked()
    {
        // Arrange - Enqueue an item
        var queueId = $"{fixture.QueueId}-multi-block-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Act - Dequeue with acknowledgement (creates lock)
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "30");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        // Attempt multiple regular Dequeues - all should be blocked
        for (int i = 0; i < 3; i++)
        {
            var blockedDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
            blockedDequeueRequest.Headers.Add("require-ack", "false");
            var blockedDequeueResponse = await fixture.ApiClient.SendAsync(blockedDequeueRequest);

            // Assert - Each should return 423 Locked
            Assert.Equal(HttpStatusCode.Locked, blockedDequeueResponse.StatusCode);

            var lockedResponse = await blockedDequeueResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();
            Assert.NotNull(lockedResponse);
            Assert.NotNull(lockedResponse.Message);
        }

        // Attempt another DequeueLocked - should also be blocked
        var blockedDequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        blockedDequeueLockedRequest.Headers.Add("require-ack", "true");
        var blockedDequeueLockedResponse = await fixture.ApiClient.SendAsync(blockedDequeueLockedRequest);

        Assert.Equal(HttpStatusCode.Locked, blockedDequeueLockedResponse.StatusCode);
    }

    [Fact]
    public async Task DequeueLocked_ExpiredLock_AllowsRegularDequeue()
    {
        // Arrange - Enqueue an item
        var queueId = $"{fixture.QueueId}-expire-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Act - Dequeue with acknowledgement with short TTL (2 seconds)
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "2");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult);
        Assert.NotNull(dequeueLockedResult.Items);
        Assert.Single(dequeueLockedResult.Items);

        // Wait for lock to expire (2 seconds + buffer)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Now regular Dequeue should work (lock expired)
        var regularDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        regularDequeueRequest.Headers.Add("require-ack", "false");
        var regularDequeueResponse = await fixture.ApiClient.SendAsync(regularDequeueRequest);

        // Assert - Should return 200 OK with the item (restored after lock expiry)
        Assert.Equal(HttpStatusCode.OK, regularDequeueResponse.StatusCode);

        var dequeueResult = await regularDequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(dequeueResult);
        Assert.NotNull(dequeueResult.Items);
        Assert.Single(dequeueResult.Items);

        var dequeuedItem = (JsonElement)dequeueResult.Items[0].Item;
        Assert.Equal(1, dequeuedItem.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Dequeue_EmptyQueue_Returns200WithNullItem()
    {
        // Arrange - Use empty queue
        var queueId = $"{fixture.QueueId}-empty-test-{Guid.NewGuid():N}";

        // Act - Try to dequeue from empty queue
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("require-ack", "false");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);

        // Assert - Should return 204 No Content (not 423) for empty queue
        Assert.Equal(HttpStatusCode.NoContent, dequeueResponse.StatusCode);
    }

    [Fact]
    public async Task DequeueLocked_WithLock_ConsistentErrorResponse()
    {
        // This test verifies that both Dequeue and DequeueLocked return consistent 423 responses
        // when the queue is locked by another operation (legacy mode - no competing consumers)

        // Arrange - Enqueue an item
        var queueId = $"{fixture.QueueId}-consistent-error-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Act - Dequeue with acknowledgement (creates lock) - explicitly disable competing consumers
        var dequeueLockedRequest1 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest1.Headers.Add("require-ack", "true");
        dequeueLockedRequest1.Headers.Add("ttl-seconds", "30");
        dequeueLockedRequest1.Headers.Add("allow-competing-consumers", "false");
        var dequeueLockedResponse1 = await fixture.ApiClient.SendAsync(dequeueLockedRequest1);
        dequeueLockedResponse1.EnsureSuccessStatusCode();

        // Attempt regular Dequeue - blocked in legacy mode
        var blockedDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        blockedDequeueRequest.Headers.Add("require-ack", "false");
        blockedDequeueRequest.Headers.Add("allow-competing-consumers", "false");
        var blockedDequeueResponse = await fixture.ApiClient.SendAsync(blockedDequeueRequest);

        Assert.Equal(HttpStatusCode.Locked, blockedDequeueResponse.StatusCode);
        var blockedDequeueBody = await blockedDequeueResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();

        // Attempt DequeueLocked - also blocked in legacy mode
        var blockedDequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        blockedDequeueLockedRequest.Headers.Add("require-ack", "true");
        blockedDequeueLockedRequest.Headers.Add("allow-competing-consumers", "false");
        var blockedDequeueLockedResponse = await fixture.ApiClient.SendAsync(blockedDequeueLockedRequest);

        Assert.Equal(HttpStatusCode.Locked, blockedDequeueLockedResponse.StatusCode);
        var blockedDequeueLockedBody = await blockedDequeueLockedResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();

        // Assert - Both should return same structure with error messages
        Assert.NotNull(blockedDequeueBody);
        Assert.NotNull(blockedDequeueLockedBody);

        Assert.NotNull(blockedDequeueBody.Message);
        Assert.NotNull(blockedDequeueLockedBody.Message);

        // Lock expiration times should be present if backend provides them
        if (blockedDequeueBody.LockExpiresAt.HasValue && blockedDequeueLockedBody.LockExpiresAt.HasValue)
        {
            // Lock expiration times should be very close (within 1 second)
            Assert.True(Math.Abs(blockedDequeueBody.LockExpiresAt.Value - blockedDequeueLockedBody.LockExpiresAt.Value) < 1);
        }
    }

    [Fact]
    public async Task ExtendLock_ValidLock_ExtendsExpiry()
    {
        // Arrange - Enqueue an item and create a lock
        var queueId = $"{fixture.QueueId}-extend-lock-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Dequeue with acknowledgement (creates lock with 10s TTL)
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "10");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult);
        Assert.NotNull(dequeueLockedResult.Items);
        Assert.Single(dequeueLockedResult.Items);

        var originalExpiresAt = dequeueLockedResult.Items[0].LockExpiresAt;

        // Act - Extend lock by 30 seconds
        var extendLockRequest = new ApiExtendLockRequest(dequeueLockedResult.Items[0].LockId, AdditionalTtlSeconds: 30);
        var extendLockResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/extend-lock", extendLockRequest);

        // Assert - Should return 200 OK with new expiry time
        Assert.Equal(HttpStatusCode.OK, extendLockResponse.StatusCode);

        var extendLockResult = await extendLockResponse.Content.ReadFromJsonAsync<ApiExtendLockResponse>();
        Assert.NotNull(extendLockResult);
        Assert.NotNull(extendLockResult.NewExpiresAt);
        Assert.Equal(dequeueLockedResult.Items[0].LockId, extendLockResult.LockId);

        // New expiry should be ~30 seconds later (within 2 seconds tolerance)
        var expectedNewExpiresAt = originalExpiresAt + 30;
        Assert.True(Math.Abs(extendLockResult.NewExpiresAt - expectedNewExpiresAt) < 2);

        // Verify queue is still locked
        var blockedDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        blockedDequeueRequest.Headers.Add("require-ack", "false");
        var blockedDequeueResponse = await fixture.ApiClient.SendAsync(blockedDequeueRequest);
        Assert.Equal(HttpStatusCode.Locked, blockedDequeueResponse.StatusCode);
    }

    [Fact]
    public async Task ExtendLock_PreventsExpiry_AllowsLaterAcknowledge()
    {
        // This test verifies that extending a lock actually prevents expiry
        // and allows acknowledgement to succeed later

        // Arrange - Enqueue an item
        var queueId = $"{fixture.QueueId}-extend-prevents-expiry-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Dequeue with acknowledgement (creates lock with 3s TTL)
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "3");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        var dequeueLockedResult = await dequeueLockedResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueLockedResult);
        Assert.NotNull(dequeueLockedResult.Items);
        Assert.Single(dequeueLockedResult.Items);

        // Wait 2 seconds (lock would expire at 3s)
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Act - Extend lock by 5 more seconds
        var extendLockRequest = new ApiExtendLockRequest(dequeueLockedResult.Items[0].LockId, AdditionalTtlSeconds: 5);
        var extendLockResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/extend-lock", extendLockRequest);
        extendLockResponse.EnsureSuccessStatusCode();

        // Wait 2 more seconds (original lock would have expired at 3s, but extension keeps it alive)
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Assert - Acknowledge should still work (lock is still valid)
        var ackRequest = new ApiAcknowledgeRequest(dequeueLockedResult.Items[0].LockId);
        var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", ackRequest);

        Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);

        var ackResult = await ackResponse.Content.ReadFromJsonAsync<ApiAcknowledgeResponse>();
        Assert.NotNull(ackResult);
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Queue should now be empty
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("require-ack", "false");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);
        Assert.Equal(HttpStatusCode.NoContent, dequeueResponse.StatusCode);
    }

    [Fact]
    public async Task ExtendLock_InvalidLock_Returns404()
    {
        // Arrange - Enqueue an item and create a lock
        var queueId = $"{fixture.QueueId}-extend-invalid-lock-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Dequeue with acknowledgement (creates lock)
        var dequeueLockedRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueLockedRequest.Headers.Add("require-ack", "true");
        dequeueLockedRequest.Headers.Add("ttl-seconds", "10");
        var dequeueLockedResponse = await fixture.ApiClient.SendAsync(dequeueLockedRequest);
        dequeueLockedResponse.EnsureSuccessStatusCode();

        // Act - Try to extend with invalid lock ID
        var extendLockRequest = new ApiExtendLockRequest("invalid-lock-id-12345", AdditionalTtlSeconds: 30);
        var extendLockResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/extend-lock", extendLockRequest);

        // Assert - Should return 404 Not Found or 400 Bad Request
        Assert.True(
            extendLockResponse.StatusCode == HttpStatusCode.NotFound ||
            extendLockResponse.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 404 or 400, got {extendLockResponse.StatusCode}"
        );
    }
}
