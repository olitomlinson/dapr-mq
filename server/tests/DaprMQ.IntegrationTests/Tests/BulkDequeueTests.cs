using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;
using System.Runtime.CompilerServices;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class BulkDequeueTests(DaprTestFixture fixture)
{
    // HTTP Bulk Dequeue Tests

    [Fact]
    public async Task BulkDequeue_Count10_ReturnsAllItemsInFifoOrder()
    {
        // Arrange - Enqueue 10 items
        var queueId = $"{fixture.QueueId}-bulk10-{Guid.NewGuid():N}";
        var expectedIds = new List<int>();

        for (int i = 0; i < 10; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
                new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, Priority: 1) }));
            expectedIds.Add(i);
        }

        // Act - Bulk dequeue all 10 items
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("count", "10");
        var response = await fixture.ApiClient.SendAsync(dequeueRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(10, result.Items.Count);

        var actualIds = result.Items.Select(dequeueItem =>
            ((JsonElement)dequeueItem.Item).GetProperty("id").GetInt32()).ToList();
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task BulkDequeue_CrossPriority_ReturnsInPriorityOrder()
    {
        // Arrange - Enqueue items with mixed priorities
        var queueId = $"{fixture.QueueId}-priority-bulk-{Guid.NewGuid():N}";

        // Enqueue priority 1 items first
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(new List<ApiEnqueueItem> {
                new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = 10, priority = 1 }), Priority: 1),
                new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = 11, priority = 1 }), Priority: 1)
            }));

        // Enqueue priority 0 items (should come first)
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(new List<ApiEnqueueItem> {
                new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = 0, priority = 0 }), Priority: 0),
                new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = 1, priority = 0 }), Priority: 0)
            }));

        // Enqueue priority 2 items (should come last)
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(new List<ApiEnqueueItem> {
                new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = 20, priority = 2 }), Priority: 2)
            }));

        // Act - Bulk dequeue all items
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("count", "10");
        var response = await fixture.ApiClient.SendAsync(dequeueRequest);

        // Assert - Priority 0 first, then 1, then 2
        var result = await response.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(result);
        Assert.Equal(5, result.Items!.Count);

        var actualIds = result.Items.Select(dequeueItem =>
            ((JsonElement)dequeueItem.Item).GetProperty("id").GetInt32()).ToList();
        Assert.Equal(new[] { 0, 1, 10, 11, 20 }, actualIds);
    }

    [Fact]
    public async Task BulkDequeueLocked_CreatesMultipleLocks()
    {
        // Arrange - Enqueue 5 items
        var queueId = $"{fixture.QueueId}-bulk-ack-{Guid.NewGuid():N}";

        for (int i = 0; i < 5; i++)
        {
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
                new ApiEnqueueRequest(new List<ApiEnqueueItem> {
                    new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = i }), Priority: 1)
                }));
        }

        // Act - Bulk dequeue with acknowledgement
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("count", "5");
        dequeueRequest.Headers.Add("require-ack", "true");
        dequeueRequest.Headers.Add("ttl-seconds", "30");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, dequeueResponse.StatusCode);

        var result = await dequeueResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(5, result.Items.Count);

        // Each lock ID should be unique
        var lockIds = result.Items.Select(item => item.LockId).ToList();
        Assert.Equal(5, lockIds.Distinct().Count());

        // All locks should have expiry times
        Assert.All(result.Items, item =>
            Assert.True(item.LockExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task BulkDequeueLocked_AcknowledgeMultiple_RemovesAllLocks()
    {
        // Arrange - Enqueue and lock 3 items
        var queueId = $"{fixture.QueueId}-bulk-ack-multi-{Guid.NewGuid():N}";

        for (int i = 0; i < 3; i++)
        {
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
                new ApiEnqueueRequest(new List<ApiEnqueueItem> {
                    new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = i }), Priority: 1)
                }));
        }

        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("count", "3");
        dequeueRequest.Headers.Add("require-ack", "true");
        dequeueRequest.Headers.Add("ttl-seconds", "30");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeueRequest);
        var dequeueResult = await dequeueResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();

        Assert.NotNull(dequeueResult);
        Assert.NotNull(dequeueResult.Items);
        Assert.Equal(3, dequeueResult.Items.Count);

        // Act - Acknowledge all locks
        foreach (var item in dequeueResult.Items)
        {
            var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge",
                new ApiAcknowledgeRequest(item.LockId));
            Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);
        }

        // Assert - Queue should be empty
        var finalDequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        finalDequeueRequest.Headers.Add("count", "10");
        var finalDequeueResponse = await fixture.ApiClient.SendAsync(finalDequeueRequest);
        Assert.Equal(HttpStatusCode.NoContent, finalDequeueResponse.StatusCode);
    }

    [Fact]
    public async Task BulkDequeue_RequestMoreThanAvailable_ReturnsPartialResults()
    {
        // Arrange - Enqueue only 3 items
        var queueId = $"{fixture.QueueId}-partial-{Guid.NewGuid():N}";

        for (int i = 0; i < 3; i++)
        {
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
                new ApiEnqueueRequest(new List<ApiEnqueueItem> {
                    new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = i }), Priority: 1)
                }));
        }

        // Act - Request 10 items but only 3 exist
        var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest.Headers.Add("count", "10");
        var response = await fixture.ApiClient.SendAsync(dequeueRequest);

        // Assert - Should return 3 items with 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(3, result.Items.Count);
    }

    // gRPC Bulk Dequeue Tests

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
    public async Task BulkDequeue_Grpc_Count10_ReturnsRepeatedFields()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-bulk10-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        var enqueueRequest = new EnqueueRequest { QueueId = queueId };
        for (int i = 0; i < 10; i++)
        {
            enqueueRequest.Items.Add(new EnqueueItem
            {
                ItemJson = $"{{\"id\":{i}}}",
                Priority = 1
            });
        }
        await client.EnqueueAsync(enqueueRequest);

        // Act
        var dequeueResponse = await client.DequeueAsync(new DequeueRequest
        {
            QueueId = queueId,
            Count = 10
        });

        // Assert
        Assert.Equal(DequeueResponse.ResultOneofCase.Success, dequeueResponse.ResultCase);
        Assert.Equal(10, dequeueResponse.Success.ItemJson.Count);
        Assert.Equal(10, dequeueResponse.Success.Priority.Count);

        // Verify FIFO order
        for (int i = 0; i < 10; i++)
        {
            Assert.Contains($"\"id\":{i}", dequeueResponse.Success.ItemJson[i]);
        }
    }

    [Fact]
    public async Task BulkDequeueLocked_Grpc_ReturnsMultipleLockIds()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-bulk-ack-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        var enqueueRequest = new EnqueueRequest { QueueId = queueId };
        for (int i = 0; i < 5; i++)
        {
            enqueueRequest.Items.Add(new EnqueueItem
            {
                ItemJson = $"{{\"id\":{i}}}",
                Priority = 1
            });
        }
        await client.EnqueueAsync(enqueueRequest);

        // Act
        var dequeueResponse = await client.DequeueLockedAsync(new DequeueLockedRequest
        {
            QueueId = queueId,
            TtlSeconds = 30,
            Count = 5
        });

        // Assert
        Assert.Equal(DequeueLockedResponse.ResultOneofCase.Success, dequeueResponse.ResultCase);
        Assert.Equal(5, dequeueResponse.Success.ItemJson.Count);
        Assert.Equal(5, dequeueResponse.Success.LockId.Count);
        Assert.Equal(5, dequeueResponse.Success.LockExpiresAt.Count);

        // Each lock ID should be unique
        Assert.Equal(5, dequeueResponse.Success.LockId.Distinct().Count());
    }

    [Fact]
    public async Task BulkDequeue_Grpc_InvalidCount_ThrowsInvalidArgument()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-invalid-count-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        // Act & Assert - Count > 1000 should throw
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.DequeueAsync(new DequeueRequest { QueueId = queueId, Count = 1001 }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        // Note: Count = 0 is valid (defaults to 1 in protobuf)
    }

    [Fact]
    public async Task BulkOperations_1000Messages_SingleEnqueue()
    {
        // This test documents a bug where enqueuing 1000 items in one batch only persists 100
        var queueId = $"{fixture.QueueId}-bulk1000-single-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 1000;
        var enqueueItems = new List<ApiEnqueueItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            enqueueItems.Add(new ApiEnqueueItem(itemElement, Priority: 1));
        }

        // Enqueue all 1000 items in one bulk operation
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(enqueueItems));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        var enqueueResult = await enqueueResponse.Content.ReadFromJsonAsync<ApiEnqueueResponse>();
        Assert.NotNull(enqueueResult);
        Assert.True(enqueueResult.Success, $"Enqueue failed: {enqueueResult.Message}");
        Assert.Equal(totalItems, enqueueResult.ItemsEnqueued); // API reports 1000 enqueued

        // Act - Dequeue 1000 items in 10 batches of 100
        var lockIds = new List<string>();
        for (int batch = 0; batch < 10; batch++)
        {
            var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
            dequeueRequest.Headers.Add("count", "100");
            dequeueRequest.Headers.Add("require-ack", "true");
            dequeueRequest.Headers.Add("ttl-seconds", "300");
            dequeueRequest.Headers.Add("allow-competing-consumers", "true");

            var response = await fixture.ApiClient.SendAsync(dequeueRequest);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                // BUG: Queue becomes empty after first batch even though 1000 were "enqueued"
                Assert.Fail($"Queue empty after batch {batch}, only dequeued {lockIds.Count} items (expected 1000)");
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);

            lockIds.AddRange(result.Items.Select(item => item.LockId));
        }

        var totalDequeued = lockIds.Count;

        // Acknowledge all locks in parallel
        var ackTasks = lockIds.Select(async lockId =>
        {
            var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge",
                new ApiAcknowledgeRequest(lockId));
            return ackResponse.StatusCode;
        });

        var ackStatuses = await Task.WhenAll(ackTasks);

        stopwatch.Stop();

        // Assert
        Assert.Equal(totalItems, totalDequeued);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 30, $"Test took {elapsedSeconds:F2}s (expected <30s, baseline for {totalItems} items)");
    }

    [Fact]
    public async Task BulkOperations_1000Messages_SingleEnqueue_ParallelDequeue()
    {
        var queueId = $"{fixture.QueueId}-bulk1000-parallel-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 1000;
        var enqueueItems = new List<ApiEnqueueItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            enqueueItems.Add(new ApiEnqueueItem(itemElement, Priority: 1));
        }

        // Enqueue all 1000 items in one bulk operation
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(enqueueItems));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        var enqueueResult = await enqueueResponse.Content.ReadFromJsonAsync<ApiEnqueueResponse>();
        Assert.NotNull(enqueueResult);
        Assert.True(enqueueResult.Success, $"Enqueue failed: {enqueueResult.Message}");
        Assert.Equal(totalItems, enqueueResult.ItemsEnqueued);

        // Act - Dequeue 1000 items in 10 parallel batches of 100
        var lockIds = new ConcurrentBag<string>();
        var dequeueTasks = Enumerable.Range(0, 10).Select(async batch =>
        {
            var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
            dequeueRequest.Headers.Add("count", "100");
            dequeueRequest.Headers.Add("require-ack", "true");
            dequeueRequest.Headers.Add("ttl-seconds", "300");
            dequeueRequest.Headers.Add("allow-competing-consumers", "true");

            var response = await fixture.ApiClient.SendAsync(dequeueRequest);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return 0;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);

            foreach (var item in result.Items)
            {
                lockIds.Add(item.LockId);
            }

            return result.Items.Count;
        });

        var dequeuedCounts = await Task.WhenAll(dequeueTasks);
        var totalDequeued = dequeuedCounts.Sum();

        // Acknowledge all locks in parallel
        var ackTasks = lockIds.Select(async lockId =>
        {
            var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge",
                new ApiAcknowledgeRequest(lockId));
            return ackResponse.StatusCode;
        });

        var ackStatuses = await Task.WhenAll(ackTasks);

        stopwatch.Stop();

        // Assert
        Assert.Equal(totalItems, totalDequeued);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 30, $"Test took {elapsedSeconds:F2}s (expected <30s for {totalItems} items with parallel dequeues)");
    }

    [Fact]
    public async Task BulkOperations_10000Messages_SingleEnqueue_ParallelDequeue()
    {
        var queueId = $"{fixture.QueueId}-bulk10000-parallel-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 10000;
        var enqueueItems = new List<ApiEnqueueItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            enqueueItems.Add(new ApiEnqueueItem(itemElement, Priority: 1));
        }

        // Enqueue all 10000 items in one bulk operation
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(enqueueItems));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        var enqueueResult = await enqueueResponse.Content.ReadFromJsonAsync<ApiEnqueueResponse>();
        Assert.NotNull(enqueueResult);
        Assert.True(enqueueResult.Success, $"Enqueue failed: {enqueueResult.Message}");
        Assert.Equal(totalItems, enqueueResult.ItemsEnqueued);

        // Act - Dequeue 10000 items in 10 parallel batches of 1000
        var lockIds = new ConcurrentBag<string>();
        var dequeueTasks = Enumerable.Range(0, 10).Select(async batch =>
        {
            var dequeueRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
            dequeueRequest.Headers.Add("count", "1000");
            dequeueRequest.Headers.Add("require-ack", "true");
            dequeueRequest.Headers.Add("ttl-seconds", "300");
            dequeueRequest.Headers.Add("allow-competing-consumers", "true");

            var response = await fixture.ApiClient.SendAsync(dequeueRequest);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return 0;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);

            foreach (var item in result.Items)
            {
                lockIds.Add(item.LockId);
            }

            return result.Items.Count;
        });

        var dequeuedCounts = await Task.WhenAll(dequeueTasks);
        var totalDequeued = dequeuedCounts.Sum();

        // Acknowledge all locks in parallel
        var ackTasks = lockIds.Select(async lockId =>
        {
            var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge",
                new ApiAcknowledgeRequest(lockId));
            return ackResponse.StatusCode;
        });

        var ackStatuses = await Task.WhenAll(ackTasks);

        stopwatch.Stop();

        // Assert
        Assert.Equal(totalItems, totalDequeued);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        // Throughput ceiling is overridable (DAPRMQ_BULK_TEST_MAX_SECONDS) so slower shared CI runners
        // can loosen it without changing the local default.
        var maxSeconds = double.TryParse(Environment.GetEnvironmentVariable("DAPRMQ_BULK_TEST_MAX_SECONDS"), out var overrideSeconds) ? overrideSeconds : 60;
        Assert.True(elapsedSeconds < maxSeconds, $"Test took {elapsedSeconds:F2}s (expected <{maxSeconds}s for {totalItems} items with parallel dequeues)");
    }

    [Fact]
    public async Task BulkOperations_101Messages_TwoEnqueues()
    {
        // This test documents a bug where enqueuing 100 then 1 more only persists the first 100
        var queueId = $"{fixture.QueueId}-bulk101-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 101;

        // Enqueue first 100 items
        var enqueueItems1 = new List<ApiEnqueueItem>();
        for (int i = 0; i < 100; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            enqueueItems1.Add(new ApiEnqueueItem(itemElement, Priority: 1));
        }

        var enqueueResponse1 = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(enqueueItems1));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse1.StatusCode);

        var enqueueResult1 = await enqueueResponse1.Content.ReadFromJsonAsync<ApiEnqueueResponse>();
        Assert.NotNull(enqueueResult1);
        Assert.True(enqueueResult1.Success, $"Enqueue batch 1 failed: {enqueueResult1.Message}");
        Assert.Equal(100, enqueueResult1.ItemsEnqueued);

        // Enqueue 101st item
        List<ApiEnqueueItem> enqueueItems2 =
        [
            new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = 100, value = "item-100" }), Priority: 1)
        ];

        var enqueueResponse2 = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(enqueueItems2));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse2.StatusCode);

        var enqueueResult2 = await enqueueResponse2.Content.ReadFromJsonAsync<ApiEnqueueResponse>();
        Assert.NotNull(enqueueResult2);
        Assert.True(enqueueResult2.Success, $"Enqueue batch 2 failed: {enqueueResult2.Message}");
        Assert.Equal(1, enqueueResult2.ItemsEnqueued);

        // Act - Dequeue first batch of 100 with acknowledgement
        var dequeueRequest1 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest1.Headers.Add("count", "100");
        dequeueRequest1.Headers.Add("require-ack", "true");
        dequeueRequest1.Headers.Add("ttl-seconds", "300");
        dequeueRequest1.Headers.Add("allow-competing-consumers", "true");

        var dequeueResponse1 = await fixture.ApiClient.SendAsync(dequeueRequest1);
        Assert.Equal(HttpStatusCode.OK, dequeueResponse1.StatusCode);

        var dequeueResult1 = await dequeueResponse1.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueResult1);
        Assert.NotNull(dequeueResult1.Items);
        Assert.Equal(100, dequeueResult1.Items.Count);

        // Dequeue second batch - should get 1 remaining item
        var dequeueRequest2 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest2.Headers.Add("count", "100");
        dequeueRequest2.Headers.Add("require-ack", "true");
        dequeueRequest2.Headers.Add("ttl-seconds", "300");
        dequeueRequest2.Headers.Add("allow-competing-consumers", "true");

        var dequeueResponse2 = await fixture.ApiClient.SendAsync(dequeueRequest2);

        // BUG: Queue returns NoContent even though 101st item was enqueued
        if (dequeueResponse2.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Fail("Queue empty after first 100 items, but 101st item was enqueued successfully");
        }

        Assert.Equal(HttpStatusCode.OK, dequeueResponse2.StatusCode);

        var dequeueResult2 = await dequeueResponse2.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueResult2);
        Assert.NotNull(dequeueResult2.Items);
        Assert.Single(dequeueResult2.Items);

        // Collect all lock IDs
        var lockIds = dequeueResult1.Items.Select(item => item.LockId)
            .Concat(dequeueResult2.Items.Select(item => item.LockId))
            .ToList();
        var totalDequeued = lockIds.Count;

        // Acknowledge all locks in parallel
        var ackTasks = lockIds.Select(async lockId =>
        {
            var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge",
                new ApiAcknowledgeRequest(lockId));
            return ackResponse.StatusCode;
        });

        var ackStatuses = await Task.WhenAll(ackTasks);

        stopwatch.Stop();

        // Assert
        Assert.Equal(totalItems, totalDequeued);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 10, $"Test took {elapsedSeconds:F2}s (expected <10s, baseline for {totalItems} items)");
    }

    [Fact]
    public async Task BulkOperations_100Messages_WithTiming()
    {
        // Working baseline test: Enqueue 100 items in one batch, dequeue in two batches (50 + 50)
        var queueId = $"{fixture.QueueId}-bulk100-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 100;
        var enqueueItems = new List<ApiEnqueueItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            enqueueItems.Add(new ApiEnqueueItem(itemElement, Priority: 1));
        }

        // Enqueue all items
        var enqueueResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue",
            new ApiEnqueueRequest(enqueueItems));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        var enqueueResult = await enqueueResponse.Content.ReadFromJsonAsync<ApiEnqueueResponse>();
        Assert.NotNull(enqueueResult);
        Assert.True(enqueueResult.Success, $"Enqueue failed: {enqueueResult.Message}");
        Assert.Equal(totalItems, enqueueResult.ItemsEnqueued);

        // Act - Dequeue first batch of 50 with acknowledgement
        var dequeueRequest1 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest1.Headers.Add("count", "50");
        dequeueRequest1.Headers.Add("require-ack", "true");
        dequeueRequest1.Headers.Add("ttl-seconds", "300");
        dequeueRequest1.Headers.Add("allow-competing-consumers", "true");

        var dequeueResponse1 = await fixture.ApiClient.SendAsync(dequeueRequest1);
        Assert.Equal(HttpStatusCode.OK, dequeueResponse1.StatusCode);

        var dequeueResult1 = await dequeueResponse1.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueResult1);
        Assert.NotNull(dequeueResult1.Items);
        Assert.Equal(50, dequeueResult1.Items.Count);

        // Dequeue second batch of 50 remaining items
        var dequeueRequest2 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeueRequest2.Headers.Add("count", "50");
        dequeueRequest2.Headers.Add("require-ack", "true");
        dequeueRequest2.Headers.Add("ttl-seconds", "300");
        dequeueRequest2.Headers.Add("allow-competing-consumers", "true");

        var dequeueResponse2 = await fixture.ApiClient.SendAsync(dequeueRequest2);
        Assert.Equal(HttpStatusCode.OK, dequeueResponse2.StatusCode);

        var dequeueResult2 = await dequeueResponse2.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.NotNull(dequeueResult2);
        Assert.NotNull(dequeueResult2.Items);
        Assert.Equal(50, dequeueResult2.Items.Count);

        // Collect all lock IDs
        var lockIds = dequeueResult1.Items.Select(item => item.LockId)
            .Concat(dequeueResult2.Items.Select(item => item.LockId))
            .ToList();
        var totalDequeued = lockIds.Count;

        // Acknowledge all locks in parallel
        var ackTasks = lockIds.Select(async lockId =>
        {
            var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge",
                new ApiAcknowledgeRequest(lockId));
            return ackResponse.StatusCode;
        });

        var ackStatuses = await Task.WhenAll(ackTasks);

        stopwatch.Stop();

        // Assert
        Assert.Equal(totalItems, totalDequeued);
        Assert.Equal(totalItems, lockIds.Distinct().Count()); // All lock IDs unique
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        // Output timing information
        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 10, $"Test took {elapsedSeconds:F2}s (expected <10s, baseline for {totalItems} items)");
    }
}
