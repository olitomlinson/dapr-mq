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
    public async Task PopWithAck_CreatesLock_BlocksRegularPop()
    {
        // Arrange - Push an item
        var queueId = $"{fixture.QueueId}-lock-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Act - Pop with acknowledgement (creates lock)
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "30");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult);
        Assert.NotNull(popWithAckResult.Items);
        Assert.Single(popWithAckResult.Items);
        Assert.NotNull(popWithAckResult.Items[0].Item);
        Assert.NotNull(popWithAckResult.Items[0].LockId);
        Assert.True(popWithAckResult.Items[0].LockExpiresAt > 0);

        // Attempt regular Pop - should be blocked with HTTP 423
        var blockedPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        blockedPopRequest.Headers.Add("require_ack", "false");
        var blockedPopResponse = await fixture.ApiClient.SendAsync(blockedPopRequest);

        // Assert - Should return 423 Locked
        Assert.Equal(HttpStatusCode.Locked, blockedPopResponse.StatusCode);

        var lockedResponse = await blockedPopResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();
        Assert.NotNull(lockedResponse);
        Assert.NotNull(lockedResponse.Message);
        Assert.Contains("locked", lockedResponse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PopWithAck_AfterAcknowledge_AllowsRegularPop()
    {
        // Arrange - Push two items
        var queueId = $"{fixture.QueueId}-ack-test-{Guid.NewGuid():N}";

        var item1 = JsonSerializer.SerializeToElement(new { id = 1, value = "first-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(item1, Priority: 1) }));

        var item2 = JsonSerializer.SerializeToElement(new { id = 2, value = "second-item" });
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(item2, Priority: 1) }));

        // Act - Pop with acknowledgement
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "30");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult);
        Assert.NotNull(popWithAckResult.Items);
        Assert.Single(popWithAckResult.Items);

        // Acknowledge the lock
        var ackRequest = new ApiAcknowledgeRequest(popWithAckResult.Items[0].LockId);
        var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", ackRequest);
        ackResponse.EnsureSuccessStatusCode();

        var ackResult = await ackResponse.Content.ReadFromJsonAsync<ApiAcknowledgeResponse>();
        Assert.NotNull(ackResult);
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Now regular Pop should work
        var regularPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        regularPopRequest.Headers.Add("require_ack", "false");
        var regularPopResponse = await fixture.ApiClient.SendAsync(regularPopRequest);

        // Assert - Should return 200 OK with second item
        Assert.Equal(HttpStatusCode.OK, regularPopResponse.StatusCode);

        var popResult = await regularPopResponse.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(popResult);
        Assert.NotNull(popResult.Items);
        Assert.Single(popResult.Items);

        var itemElement = (JsonElement)popResult.Items[0].Item;
        Assert.Equal(2, itemElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task PopWithAck_MultiplePopAttempts_AllBlocked()
    {
        // Arrange - Push an item
        var queueId = $"{fixture.QueueId}-multi-block-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Act - Pop with acknowledgement (creates lock)
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "30");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        // Attempt multiple regular Pops - all should be blocked
        for (int i = 0; i < 3; i++)
        {
            var blockedPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
            blockedPopRequest.Headers.Add("require_ack", "false");
            var blockedPopResponse = await fixture.ApiClient.SendAsync(blockedPopRequest);

            // Assert - Each should return 423 Locked
            Assert.Equal(HttpStatusCode.Locked, blockedPopResponse.StatusCode);

            var lockedResponse = await blockedPopResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();
            Assert.NotNull(lockedResponse);
            Assert.NotNull(lockedResponse.Message);
        }

        // Attempt another PopWithAck - should also be blocked
        var blockedPopWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        blockedPopWithAckRequest.Headers.Add("require_ack", "true");
        var blockedPopWithAckResponse = await fixture.ApiClient.SendAsync(blockedPopWithAckRequest);

        Assert.Equal(HttpStatusCode.Locked, blockedPopWithAckResponse.StatusCode);
    }

    [Fact]
    public async Task PopWithAck_ExpiredLock_AllowsRegularPop()
    {
        // Arrange - Push an item
        var queueId = $"{fixture.QueueId}-expire-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Act - Pop with acknowledgement with short TTL (2 seconds)
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "2");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult);
        Assert.NotNull(popWithAckResult.Items);
        Assert.Single(popWithAckResult.Items);

        // Wait for lock to expire (2 seconds + buffer)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Now regular Pop should work (lock expired)
        var regularPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        regularPopRequest.Headers.Add("require_ack", "false");
        var regularPopResponse = await fixture.ApiClient.SendAsync(regularPopRequest);

        // Assert - Should return 200 OK with the item (restored after lock expiry)
        Assert.Equal(HttpStatusCode.OK, regularPopResponse.StatusCode);

        var popResult = await regularPopResponse.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(popResult);
        Assert.NotNull(popResult.Items);
        Assert.Single(popResult.Items);

        var poppedItem = (JsonElement)popResult.Items[0].Item;
        Assert.Equal(1, poppedItem.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Pop_EmptyQueue_Returns200WithNullItem()
    {
        // Arrange - Use empty queue
        var queueId = $"{fixture.QueueId}-empty-test-{Guid.NewGuid():N}";

        // Act - Try to pop from empty queue
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("require_ack", "false");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);

        // Assert - Should return 204 No Content (not 423) for empty queue
        Assert.Equal(HttpStatusCode.NoContent, popResponse.StatusCode);
    }

    [Fact]
    public async Task PopWithAck_WithLock_ConsistentErrorResponse()
    {
        // This test verifies that both Pop and PopWithAck return consistent 423 responses
        // when the queue is locked by another operation (legacy mode - no competing consumers)

        // Arrange - Push an item
        var queueId = $"{fixture.QueueId}-consistent-error-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Act - Pop with acknowledgement (creates lock) - explicitly disable competing consumers
        var popWithAckRequest1 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest1.Headers.Add("require_ack", "true");
        popWithAckRequest1.Headers.Add("ttl_seconds", "30");
        popWithAckRequest1.Headers.Add("allow_competing_consumers", "false");
        var popWithAckResponse1 = await fixture.ApiClient.SendAsync(popWithAckRequest1);
        popWithAckResponse1.EnsureSuccessStatusCode();

        // Attempt regular Pop - blocked in legacy mode
        var blockedPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        blockedPopRequest.Headers.Add("require_ack", "false");
        blockedPopRequest.Headers.Add("allow_competing_consumers", "false");
        var blockedPopResponse = await fixture.ApiClient.SendAsync(blockedPopRequest);

        Assert.Equal(HttpStatusCode.Locked, blockedPopResponse.StatusCode);
        var popLockedResponse = await blockedPopResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();

        // Attempt PopWithAck - also blocked in legacy mode
        var blockedPopWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        blockedPopWithAckRequest.Headers.Add("require_ack", "true");
        blockedPopWithAckRequest.Headers.Add("allow_competing_consumers", "false");
        var blockedPopWithAckResponse = await fixture.ApiClient.SendAsync(blockedPopWithAckRequest);

        Assert.Equal(HttpStatusCode.Locked, blockedPopWithAckResponse.StatusCode);
        var popWithAckLockedResponse = await blockedPopWithAckResponse.Content.ReadFromJsonAsync<ApiLockedResponse>();

        // Assert - Both should return same structure with error messages
        Assert.NotNull(popLockedResponse);
        Assert.NotNull(popWithAckLockedResponse);

        Assert.NotNull(popLockedResponse.Message);
        Assert.NotNull(popWithAckLockedResponse.Message);

        // Lock expiration times should be present if backend provides them
        if (popLockedResponse.LockExpiresAt.HasValue && popWithAckLockedResponse.LockExpiresAt.HasValue)
        {
            // Lock expiration times should be very close (within 1 second)
            Assert.True(Math.Abs(popLockedResponse.LockExpiresAt.Value - popWithAckLockedResponse.LockExpiresAt.Value) < 1);
        }
    }

    [Fact]
    public async Task ExtendLock_ValidLock_ExtendsExpiry()
    {
        // Arrange - Push an item and create a lock
        var queueId = $"{fixture.QueueId}-extend-lock-test-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Pop with acknowledgement (creates lock with 10s TTL)
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "10");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult);
        Assert.NotNull(popWithAckResult.Items);
        Assert.Single(popWithAckResult.Items);

        var originalExpiresAt = popWithAckResult.Items[0].LockExpiresAt;

        // Act - Extend lock by 30 seconds
        var extendLockRequest = new ApiExtendLockRequest(popWithAckResult.Items[0].LockId, AdditionalTtlSeconds: 30);
        var extendLockResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/extend-lock", extendLockRequest);

        // Assert - Should return 200 OK with new expiry time
        Assert.Equal(HttpStatusCode.OK, extendLockResponse.StatusCode);

        var extendLockResult = await extendLockResponse.Content.ReadFromJsonAsync<ApiExtendLockResponse>();
        Assert.NotNull(extendLockResult);
        Assert.NotNull(extendLockResult.NewExpiresAt);
        Assert.Equal(popWithAckResult.Items[0].LockId, extendLockResult.LockId);

        // New expiry should be ~30 seconds later (within 2 seconds tolerance)
        var expectedNewExpiresAt = originalExpiresAt + 30;
        Assert.True(Math.Abs(extendLockResult.NewExpiresAt - expectedNewExpiresAt) < 2);

        // Verify queue is still locked
        var blockedPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        blockedPopRequest.Headers.Add("require_ack", "false");
        var blockedPopResponse = await fixture.ApiClient.SendAsync(blockedPopRequest);
        Assert.Equal(HttpStatusCode.Locked, blockedPopResponse.StatusCode);
    }

    [Fact]
    public async Task ExtendLock_PreventsExpiry_AllowsLaterAcknowledge()
    {
        // This test verifies that extending a lock actually prevents expiry
        // and allows acknowledgement to succeed later

        // Arrange - Push an item
        var queueId = $"{fixture.QueueId}-extend-prevents-expiry-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Pop with acknowledgement (creates lock with 3s TTL)
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "3");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

        var popWithAckResult = await popWithAckResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popWithAckResult);
        Assert.NotNull(popWithAckResult.Items);
        Assert.Single(popWithAckResult.Items);

        // Wait 2 seconds (lock would expire at 3s)
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Act - Extend lock by 5 more seconds
        var extendLockRequest = new ApiExtendLockRequest(popWithAckResult.Items[0].LockId, AdditionalTtlSeconds: 5);
        var extendLockResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/extend-lock", extendLockRequest);
        extendLockResponse.EnsureSuccessStatusCode();

        // Wait 2 more seconds (original lock would have expired at 3s, but extension keeps it alive)
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Assert - Acknowledge should still work (lock is still valid)
        var ackRequest = new ApiAcknowledgeRequest(popWithAckResult.Items[0].LockId);
        var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", ackRequest);

        Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);

        var ackResult = await ackResponse.Content.ReadFromJsonAsync<ApiAcknowledgeResponse>();
        Assert.NotNull(ackResult);
        Assert.True(ackResult.Success);
        Assert.Equal(1, ackResult.ItemsAcknowledged);

        // Queue should now be empty
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("require_ack", "false");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);
        Assert.Equal(HttpStatusCode.NoContent, popResponse.StatusCode);
    }

    [Fact]
    public async Task ExtendLock_InvalidLock_Returns404()
    {
        // Arrange - Push an item and create a lock
        var queueId = $"{fixture.QueueId}-extend-invalid-lock-{Guid.NewGuid():N}";
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Pop with acknowledgement (creates lock)
        var popWithAckRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popWithAckRequest.Headers.Add("require_ack", "true");
        popWithAckRequest.Headers.Add("ttl_seconds", "10");
        var popWithAckResponse = await fixture.ApiClient.SendAsync(popWithAckRequest);
        popWithAckResponse.EnsureSuccessStatusCode();

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
