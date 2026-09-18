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
    public async Task Enqueue10Items_BulkDequeue_ReturnsInCorrectFifoOrder()
    {
        // Arrange - Enqueue 10 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
            {
                new ApiEnqueueItem(itemElement, Priority: 1)
            });

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/enqueue", enqueueRequest);
            var content = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Enqueue #{i} failed: {response.StatusCode} - {content}");

            expectedIds.Add(i);
        }

        // Act - Bulk dequeue all 10 items
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/dequeue");
        dequeueRequest.Headers.Add("count", "10");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);
        dequeueResponse.EnsureSuccessStatusCode();

        var result = await dequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(10, result.Items.Count);

        var actualIds = result.Items.Select(dequeueItem =>
            ((JsonElement)dequeueItem.Item).GetProperty("id").GetInt32()).ToList();

        // Assert - Verify FIFO ordering
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task Enqueue100Items_BulkDequeue_ReturnsInCorrectFifoOrder()
    {
        // Arrange - Enqueue 100 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 100; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
            {
                new ApiEnqueueItem(itemElement, Priority: 1)
            });

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/enqueue", enqueueRequest);
            response.EnsureSuccessStatusCode();

            expectedIds.Add(i);
        }

        // Act - Bulk dequeue all 100 items (test max count)
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/dequeue");
        dequeueRequest.Headers.Add("count", "100");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);
        dequeueResponse.EnsureSuccessStatusCode();

        var result = await dequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(100, result.Items.Count);

        var actualIds = result.Items.Select(dequeueItem =>
            ((JsonElement)dequeueItem.Item).GetProperty("id").GetInt32()).ToList();

        // Assert - Verify FIFO ordering
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task EnqueueItems_DequeueEmpty_ReturnsEmptyResult()
    {
        // Arrange - Don't enqueue anything

        // Act - Try to dequeue from empty queue
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/dequeue");
        request.Headers.Add("require-ack", "false");
        var response = await fixture.ApiClient.SendAsync(request);

        // Assert - Should return 204 No Content for empty queue
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task EnqueueOneItem_DequeueTwice_SecondDequeueReturnsEmpty()
    {
        var unique = Guid.NewGuid();
        // Arrange - Enqueue 1 item
        var itemElement = JsonSerializer.SerializeToElement(new { id = 1, value = "single-item" });
        var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
        {
            new ApiEnqueueItem(itemElement, Priority: 1)
        });
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}-{unique}/enqueue", enqueueRequest);
        enqueueResponse.EnsureSuccessStatusCode();

        // Act - Dequeue twice
        var firstDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}-{unique}/dequeue");
        firstDequeueRequest.Headers.Add("require-ack", "false");
        var firstDequeue = await fixture.ApiClient.SendAsync(firstDequeueRequest);
        firstDequeue.EnsureSuccessStatusCode();
        var firstResult = await firstDequeue.Content.ReadFromJsonAsync<ApiDequeueResponse>();

        var secondDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}-{unique}/dequeue");
        secondDequeueRequest.Headers.Add("require-ack", "false");
        var secondDequeue = await fixture.ApiClient.SendAsync(secondDequeueRequest);

        // Assert
        Assert.NotNull(firstResult);
        Assert.NotNull(firstResult.Items);
        Assert.Single(firstResult.Items);

        // Second dequeue should return 204 No Content for empty queue
        Assert.Equal(HttpStatusCode.NoContent, secondDequeue.StatusCode);
    }

    [Fact]
    public async Task Enqueue300Items_BulkDequeue_VerifiesOffloadLoadCycle_MaintainsFifoOrder()
    {
        // This test validates the byte[] serialization optimization for offload/load
        // With 300 items and default buffer_segments=1, segments beyond the buffer zone
        // will be offloaded to external state store using SaveByteStateAsync
        // When dequeuing, segments are loaded back using GetByteStateAsync

        // Arrange - Enqueue 300 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 300; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var enqueueRequest = new ApiEnqueueRequest(new List<ApiEnqueueItem>
            {
                new ApiEnqueueItem(itemElement, Priority: 1)
            });

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/enqueue", enqueueRequest);
            response.EnsureSuccessStatusCode();

            expectedIds.Add(i);
        }

        // Act - Bulk dequeue in batches (3 batches of 100 to test segment loading)
        var actualIds = new List<int>();
        for (int batch = 0; batch < 3; batch++)
        {
            var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/dequeue");
            dequeueRequest.Headers.Add("count", "100");
            var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);
            dequeueResponse.EnsureSuccessStatusCode();

            var result = await dequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);
            Assert.Equal(100, result.Items.Count);

            var batchIds = result.Items.Select(dequeueItem =>
                ((JsonElement)dequeueItem.Item).GetProperty("id").GetInt32()).ToList();
            actualIds.AddRange(batchIds);
        }

        // Assert - Verify FIFO ordering maintained through offload/load cycle
        Assert.Equal(expectedIds, actualIds);

        // Verify queue is now empty
        var emptyDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/dequeue");
        var emptyDequeueResponse = await fixture.ApiClient.SendAsync(emptyDequeueRequest);
        Assert.Equal(HttpStatusCode.NoContent, emptyDequeueResponse.StatusCode);
    }

}
