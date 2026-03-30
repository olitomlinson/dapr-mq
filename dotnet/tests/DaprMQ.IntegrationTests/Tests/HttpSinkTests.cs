using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;
using Microsoft.AspNetCore.Mvc.Authorization;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class HttpSinkTests(DaprTestFixture fixture) : IAsyncLifetime
{
    private HttpClient _wireMockClient = null!;

    public async Task InitializeAsync()
    {
        // Initialize HttpClient for WireMock admin API
        _wireMockClient = new HttpClient { BaseAddress = new Uri(fixture.WireMockUrl) };

        // Reset WireMock state before each test
        await _wireMockClient.DeleteAsync("/__admin/mappings");
        await _wireMockClient.DeleteAsync("/__admin/requests");
    }

    public Task DisposeAsync()
    {
        _wireMockClient?.Dispose();
        return Task.CompletedTask;
    }

    private async Task CreateWireMockStub(string path, int statusCode, string body = "OK")
    {
        await _wireMockClient.PostAsync("/__admin/reset", null);

        var mapping = new
        {
            request = new
            {
                method = "POST",
                url = path
            },
            response = new
            {
                status = statusCode,
                body
            }
        };

        await _wireMockClient.PostAsJsonAsync("/__admin/mappings", mapping);
    }

    private async Task<List<WireMockRequest>> GetWireMockRequests()
    {
        var response = await _wireMockClient.GetAsync("/__admin/requests");
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<WireMockRequestsResponse>();
        return result?.Requests ?? new List<WireMockRequest>();
    }

    private record WireMockRequestsResponse(List<WireMockRequest> Requests);
    private record WireMockRequest(WireMockRequestDetails Request);
    private record WireMockRequestDetails(string Url, string Method, string? bodyAsBase64, string loggedDateString);

    [Fact]
    public async Task HttpSink_200OK_AutoAcknowledgesAndEmptiesQueue()
    {
        // Arrange - Create unique queue and push 3 items
        var queueId = $"{fixture.QueueId}-httpsink-200ok-{Guid.NewGuid():N}";

        var items = new List<ApiPushItem>
        {
            new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 1, value = "item-1" }), Priority: 1),
            new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 2, value = "item-2" }), Priority: 1),
            new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 3, value = "item-3" }), Priority: 1)
        };

        var pushRequest = new ApiPushRequest(items);
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Configure WireMock stub: POST /webhook → 200 OK
        await CreateWireMockStub($"/webhook-{queueId}", 200);

        // Act - Register HTTP sink with URL pointing to WireMock
        var registerRequest = new ApiRegisterHttpSinkRequest(
            Url: $"{fixture.WireMockInternalUrl}/webhook-{queueId}",
            MaxConcurrency: 5,
            LockTtlSeconds: 30
        );
        var registerResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        // Wait for first poll (polling interval + buffer)
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Assert - Verify WireMock received exactly 1 POST request
        var requests = await GetWireMockRequests();
        var webhookRequests = requests.Where(r => r.Request.Url == $"/webhook-{queueId}").ToList();

        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.Single(webhookRequests);

        // Verify request body contains 3 items
        var requestBody64 = webhookRequests[0].Request.bodyAsBase64;
        var requestBody = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(requestBody64));
        Assert.NotNull(requestBody);

        var bodyJson = JsonSerializer.Deserialize<List<JsonElement>>(requestBody);
        Assert.NotNull(bodyJson);
        Assert.Equal(3, bodyJson.Count);

        // Verify queue is empty (items were auto-acknowledged)
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("require_ack", "false");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);

        Assert.Equal(System.Net.HttpStatusCode.NoContent, popResponse.StatusCode);
    }

    [Fact]
    public async Task HttpSink_Unregister_StopsPolling()
    {
        // Arrange - Push 1 item
        var queueId = $"{fixture.QueueId}-httpsink-unregister-{Guid.NewGuid():N}";

        var item = new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" }), Priority: 1);
        var pushRequest = new ApiPushRequest(new List<ApiPushItem> { item });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Configure WireMock stub: POST /webhook → 200 OK
        await CreateWireMockStub($"/webhook-{queueId}", 200);

        // Register sink with 2 second polling interval
        var registerRequest = new ApiRegisterHttpSinkRequest(
            Url: $"{fixture.WireMockInternalUrl}/webhook-{queueId}",
            MaxConcurrency: 5,
            LockTtlSeconds: 30
        );
        var registerResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        // Wait for first poll to happen
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Get baseline request count
        var requestsBeforeUnregister = await GetWireMockRequests();
        var countBefore = requestsBeforeUnregister.Count(r => r.Request.Url == $"/webhook-{queueId}");
        Assert.Equal(1, countBefore); // Should have received 1 poll

        // Act - Unregister sink
        var unregisterResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/unregister", new { });
        unregisterResponse.EnsureSuccessStatusCode();

        // Wait past next poll cycle (5 seconds > 2 second interval)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert - No new requests (count unchanged)
        var requestsAfterUnregister = await GetWireMockRequests();
        var countAfter = requestsAfterUnregister.Count(r => r.Request.Url == $"/webhook-{queueId}");
        Assert.Equal(countBefore, countAfter); // Count should be unchanged
    }

    [Fact]
    public async Task HttpSink_202Accepted_DeferredAcknowledgement()
    {
        // Arrange - Push 2 items
        var queueId = $"{fixture.QueueId}-httpsink-202-{Guid.NewGuid():N}";

        var items = new List<ApiPushItem>
            {
                new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 1, value = "item-1" }), Priority: 1),
                new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 2, value = "item-2" }), Priority: 1)
            };

        var pushRequest = new ApiPushRequest(items);
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Configure WireMock stub: POST /webhook → 202 Accepted
        await CreateWireMockStub($"/webhook-{queueId}", 202, "Accepted");

        // Act - Register HTTP sink
        var registerRequest = new ApiRegisterHttpSinkRequest(
            Url: $"{fixture.WireMockInternalUrl}/webhook-{queueId}",
            MaxConcurrency: 5,
            LockTtlSeconds: 30
        );
        var registerResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        // Wait for first poll
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Assert - Verify WireMock received POST with 2 items
        var requests = await GetWireMockRequests();
        var webhookRequests = requests.Where(r => r.Request.Url == $"/webhook-{queueId}").ToList();

        Assert.Single(webhookRequests);

        var requestBody64 = webhookRequests[0].Request.bodyAsBase64;
        var requestBody = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(requestBody64));
        Assert.NotNull(requestBody);

        var bodyJson = JsonSerializer.Deserialize<List<JsonElement>>(requestBody);
        Assert.NotNull(bodyJson);
        Assert.Equal(2, bodyJson.Count);

        // Verify queue still has 2 locked items (NOT acknowledged by sink)
        // PopWithAck should return empty because items are locked
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("require_ack", "true");
        popRequest.Headers.Add("ttl_seconds", "30");
        popRequest.Headers.Add("allow_competing_consumers", "true");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);

        // Should get 204 No Content (no items available - all locked)
        Assert.Equal(System.Net.HttpStatusCode.NoContent, popResponse.StatusCode);
    }

    [Fact]
    public async Task HttpSink_500Error_LocksExpireAndItemsReappear()
    {
        // Arrange - Push 1 item
        var queueId = $"{fixture.QueueId}-httpsink-500-{Guid.NewGuid():N}";

        var item = new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 1, value = "test-item" }), Priority: 1);
        var pushRequest = new ApiPushRequest(new List<ApiPushItem> { item });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Configure WireMock stub: POST /webhook → 500 Internal Server Error
        await CreateWireMockStub($"/webhook-{queueId}", 500, "Internal Server Error");

        // Act - Register HTTP sink with SHORT lock TTL (3 seconds)
        var registerRequest = new ApiRegisterHttpSinkRequest(
            Url: $"{fixture.WireMockInternalUrl}/webhook-{queueId}",
            MaxConcurrency: 5,
            LockTtlSeconds: 3
        );
        var registerResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        // Wait for first poll (delivery will fail, lock created)
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Unregister the sink so it doesn't keep polling
        await fixture.ApiClient.PostAsync($"/queue/{queueId}/sink/http/unregister", null);

        // Assert - Verify WireMock received POST request
        var requests = await GetWireMockRequests();
        var webhookRequests = requests.Where(r => r.Request.Url == $"/webhook-{queueId}").ToList();
        Assert.True(webhookRequests.Count > 0);

        // Wait for lock to expire (3s TTL + 2s buffer)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Verify item reappears in queue (can be popped again)
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("require_ack", "false");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);

        Assert.Equal(System.Net.HttpStatusCode.OK, popResponse.StatusCode);

        var popResult = await popResponse.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(popResult);
        Assert.NotNull(popResult.Items);
        Assert.Single(popResult.Items);

        var poppedItem = (JsonElement)popResult.Items[0].Item;
        Assert.Equal(1, poppedItem.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task HttpSink_MaxConcurrency_LimitsItemsRetrieved()
    {
        // Arrange - Push 10 items
        var queueId = $"{fixture.QueueId}-httpsink-maxconcurrency-{Guid.NewGuid():N}";

        var items = new List<ApiPushItem>();
        for (int i = 1; i <= 10; i++)
        {
            items.Add(new ApiPushItem(JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" }), Priority: 1));
        }

        var pushRequest = new ApiPushRequest(items);
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Configure WireMock stub: POST /webhook → 202 Accepted (defers ack, keeps locks)
        await CreateWireMockStub($"/webhook-{queueId}", 202, "Accepted");

        // Act - Register HTTP sink with MaxConcurrency=3
        var registerRequest = new ApiRegisterHttpSinkRequest(
            Url: $"{fixture.WireMockInternalUrl}/webhook-{queueId}",
            MaxConcurrency: 3,
            LockTtlSeconds: 30
        );
        var registerResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        // Wait for first poll
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Assert - Verify first poll retrieved ≤3 items
        var requests = await GetWireMockRequests();
        var webhookRequests = requests.Where(r => r.Request.Url == $"/webhook-{queueId}").ToList();

        Assert.Single(webhookRequests); // One poll so far

        var requestBody64 = webhookRequests[0].Request.bodyAsBase64;
        var requestBody = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(requestBody64));
        Assert.NotNull(requestBody);

        var bodyJson = JsonSerializer.Deserialize<List<JsonElement>>(requestBody);
        Assert.NotNull(bodyJson);
        Assert.True(bodyJson.Count <= 3, $"Expected ≤3 items, got {bodyJson.Count}");

        // Wait for second poll cycle
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Verify no new POST (MaxConcurrency reached, all 3 slots locked)
        var requestsAfterSecondCycle = await GetWireMockRequests();
        var webhookRequestsAfter = requestsAfterSecondCycle.Where(r => r.Request.Url == $"/webhook-{queueId}").ToList();

        // Should still be 1 request (no new poll because max concurrency reached)
        Assert.Single(webhookRequestsAfter);
    }

    [Fact]
    public async Task HttpSink_DynamicPolling_FastWhenItemsAvailable()
    {
        // Arrange - Push 6 items (enough for 2 polls)
        var queueId = $"{fixture.QueueId}-httpsink-timing-{Guid.NewGuid():N}";

        var items = new List<ApiPushItem>();
        for (int i = 1; i <= 6; i++)
        {
            items.Add(new ApiPushItem(JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" }), Priority: 1));
        }

        var pushRequest = new ApiPushRequest(items);
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Configure WireMock stub: POST /webhook → 200 OK (auto-acknowledge)
        await CreateWireMockStub($"/webhook-{queueId}", 200);

        // Act - Register HTTP sink (dynamic polling starts at 1s, resets to 1s when items found)
        var registerRequest = new ApiRegisterHttpSinkRequest(
            Url: $"{fixture.WireMockInternalUrl}/webhook-{queueId}",
            MaxConcurrency: 3,
            LockTtlSeconds: 30
        );
        var registerResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        // Wait for 4 seconds to capture 2 fast polls (1s interval when items available)
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Assert - Verify we got at least 2 polls (should get 3 with MaxConcurrency=3 each)
        var requests = await GetWireMockRequests();
        var webhookRequests = requests.Where(r => r.Request.Url == $"/webhook-{queueId}").ToList();

        Assert.True(webhookRequests.Count >= 2, $"Expected at least 2 polls, got {webhookRequests.Count}");

        // Verify timing: with dynamic polling, requests should be ~1s apart when items available
        var sortedRequests = webhookRequests
            .OrderBy(r => DateTime.Parse(r.Request.loggedDateString))
            .Take(2)  // Check first two polls
            .ToList();

        var firstPollTime = DateTime.Parse(sortedRequests[0].Request.loggedDateString);
        var secondPollTime = DateTime.Parse(sortedRequests[1].Request.loggedDateString);
        var intervalSeconds = (secondPollTime - firstPollTime).TotalSeconds;

        // With dynamic polling and items available, interval should be ~1s (±0.5s tolerance)
        Assert.True(intervalSeconds >= 0.5 && intervalSeconds <= 2,
            $"Expected fast polling ~1s (±0.5s) when items available, got {intervalSeconds:F2}s");
    }

    [Fact]
    public async Task HttpSink_EmptyQueue_DoesNotSendRequests()
    {
        // Arrange - Empty queue (no items pushed)
        var queueId = $"{fixture.QueueId}-httpsink-empty-{Guid.NewGuid():N}";

        // Configure WireMock stub: POST /webhook → 200 OK
        await CreateWireMockStub($"/webhook-{queueId}", 200);

        // Act - Register HTTP sink on empty queue
        var registerRequest = new ApiRegisterHttpSinkRequest(
            Url: $"{fixture.WireMockInternalUrl}/webhook-{queueId}",
            MaxConcurrency: 5,
            LockTtlSeconds: 30
        );
        var registerResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sink/http/register", registerRequest);
        registerResponse.EnsureSuccessStatusCode();

        // Wait for 5 seconds (enough for 2 poll cycles)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Assert - Verify WireMock received 0 requests
        var requests = await GetWireMockRequests();
        var webhookRequests = requests.Where(r => r.Request.Url == $"/webhook-{queueId}").ToList();

        Assert.Empty(webhookRequests); // No HTTP POSTs should be sent when queue is empty
    }
}
