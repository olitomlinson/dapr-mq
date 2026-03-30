using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class FifoOrderingTests(DaprTestFixture fixture)
{

    [Fact]
    public async Task Push10Items_BulkPop_ReturnsInCorrectFifoOrder()
    {
        // Arrange - Push 10 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var pushRequest = new ApiPushRequest(new List<ApiPushItem>
            {
                new ApiPushItem(itemElement, Priority: 1)
            });

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/push", pushRequest);
            var content = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Push #{i} failed: {response.StatusCode} - {content}");

            expectedIds.Add(i);
        }

        // Act - Bulk pop all 10 items
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
        popRequest.Headers.Add("count", "10");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);
        popResponse.EnsureSuccessStatusCode();

        var result = await popResponse.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(10, result.Items.Count);

        var actualIds = result.Items.Select(popItem =>
            ((JsonElement)popItem.Item).GetProperty("id").GetInt32()).ToList();

        // Assert - Verify FIFO ordering
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task Push100Items_BulkPop_ReturnsInCorrectFifoOrder()
    {
        // Arrange - Push 100 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 100; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var pushRequest = new ApiPushRequest(new List<ApiPushItem>
            {
                new ApiPushItem(itemElement, Priority: 1)
            });

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/push", pushRequest);
            response.EnsureSuccessStatusCode();

            expectedIds.Add(i);
        }

        // Act - Bulk pop all 100 items (test max count)
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
        popRequest.Headers.Add("count", "100");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);
        popResponse.EnsureSuccessStatusCode();

        var result = await popResponse.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(100, result.Items.Count);

        var actualIds = result.Items.Select(popItem =>
            ((JsonElement)popItem.Item).GetProperty("id").GetInt32()).ToList();

        // Assert - Verify FIFO ordering
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task PushItems_PopEmpty_ReturnsEmptyResult()
    {
        // Arrange - Don't push anything

        // Act - Try to pop from empty queue
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
        request.Headers.Add("require_ack", "false");
        var response = await fixture.ApiClient.SendAsync(request);

        // Assert - Should return 204 No Content for empty queue
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task PushOneItem_PopTwice_SecondPopReturnsEmpty()
    {
        var unique = Guid.NewGuid();
        // Arrange - Push 1 item
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "single-item" });
        var pushRequest = new ApiPushRequest(new List<ApiPushItem>
        {
            new ApiPushItem(itemElement, Priority: 1)
        });
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}-{unique}/push", pushRequest);
        pushResponse.EnsureSuccessStatusCode();

        // Act - Pop twice
        var firstPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}-{unique}/pop");
        firstPopRequest.Headers.Add("require_ack", "false");
        var firstPop = await fixture.ApiClient.SendAsync(firstPopRequest);
        firstPop.EnsureSuccessStatusCode();
        var firstResult = await firstPop.Content.ReadFromJsonAsync<ApiPopResponse>();

        var secondPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}-{unique}/pop");
        secondPopRequest.Headers.Add("require_ack", "false");
        var secondPop = await fixture.ApiClient.SendAsync(secondPopRequest);

        // Assert
        Assert.NotNull(firstResult);
        Assert.NotNull(firstResult.Items);
        Assert.Single(firstResult.Items);

        // Second pop should return 204 No Content for empty queue
        Assert.Equal(HttpStatusCode.NoContent, secondPop.StatusCode);
    }

    [Fact]
    public async Task Push300Items_BulkPop_VerifiesOffloadLoadCycle_MaintainsFifoOrder()
    {
        // This test validates the byte[] serialization optimization for offload/load
        // With 300 items and default buffer_segments=1, segments beyond the buffer zone
        // will be offloaded to external state store using SaveByteStateAsync
        // When popping, segments are loaded back using GetByteStateAsync

        // Arrange - Push 300 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 300; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var pushRequest = new ApiPushRequest(new List<ApiPushItem>
            {
                new ApiPushItem(itemElement, Priority: 1)
            });

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/push", pushRequest);
            response.EnsureSuccessStatusCode();

            expectedIds.Add(i);
        }

        // Act - Bulk pop in batches (3 batches of 100 to test segment loading)
        var actualIds = new List<int>();
        for (int batch = 0; batch < 3; batch++)
        {
            var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
            popRequest.Headers.Add("count", "100");
            var popResponse = await fixture.ApiClient.SendAsync(popRequest);
            popResponse.EnsureSuccessStatusCode();

            var result = await popResponse.Content.ReadFromJsonAsync<ApiPopResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);
            Assert.Equal(100, result.Items.Count);

            var batchIds = result.Items.Select(popItem =>
                ((JsonElement)popItem.Item).GetProperty("id").GetInt32()).ToList();
            actualIds.AddRange(batchIds);
        }

        // Assert - Verify FIFO ordering maintained through offload/load cycle
        Assert.Equal(expectedIds, actualIds);

        // Verify queue is now empty
        var emptyPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
        var emptyPopResponse = await fixture.ApiClient.SendAsync(emptyPopRequest);
        Assert.Equal(HttpStatusCode.NoContent, emptyPopResponse.StatusCode);
    }

}
