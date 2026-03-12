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
    public async Task Push10Items_PopAll_ReturnsInCorrectFifoOrder()
    {
        // Arrange - Push 10 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var pushRequest = new ApiPushRequest(itemElement, Priority: 1);

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/push", pushRequest);
            var content = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Push #{i} failed: {response.StatusCode} - {content}");

            expectedIds.Add(i);
        }

        // Act - Pop all 10 items (actors initialize synchronously on first operation)
        var actualIds = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
            request.Headers.Add("require_ack", "false");
            var response = await fixture.ApiClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ApiPopResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Item);

            var item = (JsonElement)result.Item;
            actualIds.Add(item.GetProperty("id").GetInt32());
        }

        // Assert - Verify FIFO ordering
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task Push100Items_PopAll_ReturnsInCorrectFifoOrder()
    {
        // Arrange - Push 100 items with sequential IDs
        var expectedIds = new List<int>();
        for (int i = 0; i < 100; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var pushRequest = new ApiPushRequest(itemElement, Priority: 1);

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/push", pushRequest);
            response.EnsureSuccessStatusCode();

            expectedIds.Add(i);
        }

        // Act - Pop all 100 items
        var actualIds = new List<int>();
        for (int i = 0; i < 100; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
            request.Headers.Add("require_ack", "false");
            var response = await fixture.ApiClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ApiPopResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Item);

            var item = (JsonElement)result.Item;
            actualIds.Add(item.GetProperty("id").GetInt32());
        }

        // Assert - Verify FIFO ordering
        Assert.Equal(expectedIds, actualIds);

        // Also verify via Dapr Actor HTTP API that queue is empty
        // Note: ActorMetadata check would require deserializing the internal state structure
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
        var pushRequest = new ApiPushRequest(itemElement, Priority: 1);
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
        Assert.NotNull(firstResult.Item);

        // Second pop should return 204 No Content for empty queue
        Assert.Equal(HttpStatusCode.NoContent, secondPop.StatusCode);
    }

    [Fact]
    public async Task Push300Items_PopAll_VerifiesOffloadLoadCycle_MaintainsFifoOrder()
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
            var pushRequest = new ApiPushRequest(itemElement, Priority: 1);

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{fixture.QueueId}/push", pushRequest);
            response.EnsureSuccessStatusCode();

            expectedIds.Add(i);
        }

        // Act - Pop all 300 items (will trigger segment loading from external store)
        var actualIds = new List<int>();
        for (int i = 0; i < 300; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
            request.Headers.Add("require_ack", "false");
            var response = await fixture.ApiClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ApiPopResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Item);

            var item = (JsonElement)result.Item;
            actualIds.Add(item.GetProperty("id").GetInt32());
        }

        // Assert - Verify FIFO ordering maintained through offload/load cycle
        Assert.Equal(expectedIds, actualIds);

        // Verify queue is now empty
        var emptyPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{fixture.QueueId}/pop");
        emptyPopRequest.Headers.Add("require_ack", "false");
        var emptyPopResponse = await fixture.ApiClient.SendAsync(emptyPopRequest);
        Assert.Equal(HttpStatusCode.NoContent, emptyPopResponse.StatusCode);
    }

    [Fact]
    public async Task Push1000Items_Pop800Items_VerifiesSoftDeleteCleanup()
    {
        // This test validates the soft-delete mechanism:
        // 1. Offloaded segments are queued for deletion (not immediately deleted)
        // 2. Cleanup timer runs and deletes segments after retention period
        // 3. Segments are removed from external state store

        // With 1000 items (10 segments of 100 each) and BufferSegments=1:
        // - Segments 0-1 remain in actor state (headSegment + bufferSegments zone)
        // - Segments 2-8 are offloaded to external storage (minOffload=2, maxOffload=9)
        // - Segment 9 (tail) remains in actor state
        // When we pop 800 items, we'll load segments 2-8 from external storage

        var queueId = $"{fixture.QueueId}-segment-delete-test-{Guid.NewGuid():N}";

        // Arrange - Push 1000 items to trigger offloading
        for (int i = 0; i < 1000; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            var pushRequest = new ApiPushRequest(itemElement, Priority: 1);

            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", pushRequest);
            response.EnsureSuccessStatusCode();
        }

        // At this point, segments 2-8 should be offloaded to external state store
        // With BufferSegments=1, headSegment=0, tailSegment=9:
        //   minOffload = 0 + 1 + 1 = 2
        //   maxOffload = 9
        // So segments 0-1 are in buffer (actor state), segments 2-8 are offloaded, segment 9 is tail (actor state)
        var offloadedSegmentKey2 = $"offloaded_queue_1_seg_2_{queueId}";
        var offloadedSegmentKey5 = $"offloaded_queue_1_seg_5_{queueId}";
        var offloadedSegmentKey8 = $"offloaded_queue_1_seg_8_{queueId}";

        // Verify segments exist in external state store before popping
        var segment2Before = await GetStateStoreValueAsync(offloadedSegmentKey2);
        var segment5Before = await GetStateStoreValueAsync(offloadedSegmentKey5);
        var segment8Before = await GetStateStoreValueAsync(offloadedSegmentKey8);
        Assert.NotNull(segment2Before!); // Segment 2 should exist
        Assert.NotNull(segment5Before!); // Segment 5 should exist
        Assert.NotNull(segment8Before!); // Segment 8 should exist

        // Act - Pop 800 items (will load segments 2-8 from external storage, queue them for deletion)
        for (int i = 0; i < 800; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
            request.Headers.Add("require_ack", "false");
            var response = await fixture.ApiClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        // Immediately after popping, segments should still exist in external storage
        // (they're queued for deletion, not deleted immediately)
        var segment2AfterPop = await GetStateStoreValueAsync(offloadedSegmentKey2);
        var segment5AfterPop = await GetStateStoreValueAsync(offloadedSegmentKey5);
        var segment8AfterPop = await GetStateStoreValueAsync(offloadedSegmentKey8);
        Assert.NotNull(segment2AfterPop); // Should still exist (queued, not deleted)
        Assert.NotNull(segment5AfterPop); // Should still exist (queued, not deleted)
        Assert.NotNull(segment8AfterPop); // Should still exist (queued, not deleted)

        // Verify segments are in the deletion queue
        var deletionMetadata = await fixture.ActorClient.GetActorStateAsync<dynamic>(queueId, "segment_deletion_metadata");
        Assert.NotNull(deletionMetadata);
        // Note: We can't easily deserialize the dynamic type structure, but we verified it exists

        // Poll for segment deletion (cleanup timer fires based on SEGMENT_CLEANUP_SCAN_INTERVAL_SECONDS)
        // With SEGMENT_DELETION_RETENTION_SECONDS=5 and SEGMENT_CLEANUP_SCAN_INTERVAL_SECONDS=2,
        // segments should be deleted after 5+ seconds retention period
        // The 5-second retention prevents race conditions where cleanup deletes segments before test validation
        // Poll every 2 seconds with 20 second timeout
        var timeout = TimeSpan.FromSeconds(20);
        var pollInterval = TimeSpan.FromSeconds(2);
        var startTime = DateTime.UtcNow;

        string? segment2After = null;
        string? segment5After = null;
        string? segment8After = null;

        while (DateTime.UtcNow - startTime < timeout)
        {
            segment2After = await GetStateStoreValueAsync(offloadedSegmentKey2);
            segment5After = await GetStateStoreValueAsync(offloadedSegmentKey5);
            segment8After = await GetStateStoreValueAsync(offloadedSegmentKey8);

            if (segment2After == null && segment5After == null && segment8After == null)
            {
                // All segments deleted successfully
                break;
            }

            await Task.Delay(pollInterval);
        }

        // Assert - Segments 2-8 should now be deleted from external state store
        Assert.Null(segment2After); // Should be deleted
        Assert.Null(segment5After); // Should be deleted
        Assert.Null(segment8After); // Should be deleted

        // Verify deletion queue is empty
        var deletionMetadataAfter = await fixture.ActorClient.GetActorStateAsync<dynamic>(queueId, "segment_deletion_metadata");
        // Queue should either be empty or not exist
        var isEmptyOrNull = deletionMetadataAfter is null ||
                           deletionMetadataAfter.ToString().Contains("[]") ||
                           deletionMetadataAfter.ToString().Contains("\"PendingDeletions\":[]");
        Assert.True(isEmptyOrNull,
            $"Expected deletion queue to be empty, but got: {deletionMetadataAfter?.ToString() ?? "null"}");
    }

    /// <summary>
    /// Query Dapr state store directly using State Management API
    /// GET /v1.0/state/{storeName}/{key}
    /// </summary>
    private async Task<string?> GetStateStoreValueAsync(string key)
    {
        var url = $"/v1.0/state/statestore/{key}";
        var response = await fixture.DaprSidecarClient.GetAsync(url);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
            response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
