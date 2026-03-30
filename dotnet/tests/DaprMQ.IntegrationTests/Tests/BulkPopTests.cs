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
public class BulkPopTests(DaprTestFixture fixture)
{
    // HTTP Bulk Pop Tests

    [Fact]
    public async Task BulkPop_Count10_ReturnsAllItemsInFifoOrder()
    {
        // Arrange - Push 10 items
        var queueId = $"{fixture.QueueId}-bulk10-{Guid.NewGuid():N}";
        var expectedIds = new List<int>();

        for (int i = 0; i < 10; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
                new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(itemElement, Priority: 1) }));
            expectedIds.Add(i);
        }

        // Act - Bulk pop all 10 items
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("count", "10");
        var response = await fixture.ApiClient.SendAsync(popRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(10, result.Items.Count);

        var actualIds = result.Items.Select(popItem =>
            ((JsonElement)popItem.Item).GetProperty("id").GetInt32()).ToList();
        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task BulkPop_CrossPriority_ReturnsInPriorityOrder()
    {
        // Arrange - Push items with mixed priorities
        var queueId = $"{fixture.QueueId}-priority-bulk-{Guid.NewGuid():N}";

        // Push priority 1 items first
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(new List<ApiPushItem> {
                new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 10, priority = 1 }), Priority: 1),
                new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 11, priority = 1 }), Priority: 1)
            }));

        // Push priority 0 items (should come first)
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(new List<ApiPushItem> {
                new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 0, priority = 0 }), Priority: 0),
                new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 1, priority = 0 }), Priority: 0)
            }));

        // Push priority 2 items (should come last)
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(new List<ApiPushItem> {
                new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 20, priority = 2 }), Priority: 2)
            }));

        // Act - Bulk pop all items
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("count", "10");
        var response = await fixture.ApiClient.SendAsync(popRequest);

        // Assert - Priority 0 first, then 1, then 2
        var result = await response.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(result);
        Assert.Equal(5, result.Items!.Count);

        var actualIds = result.Items.Select(popItem =>
            ((JsonElement)popItem.Item).GetProperty("id").GetInt32()).ToList();
        Assert.Equal(new[] { 0, 1, 10, 11, 20 }, actualIds);
    }

    [Fact]
    public async Task BulkPopWithAck_CreatesMultipleLocks()
    {
        // Arrange - Push 5 items
        var queueId = $"{fixture.QueueId}-bulk-ack-{Guid.NewGuid():N}";

        for (int i = 0; i < 5; i++)
        {
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
                new ApiPushRequest(new List<ApiPushItem> {
                    new ApiPushItem(JsonSerializer.SerializeToElement(new { id = i }), Priority: 1)
                }));
        }

        // Act - Bulk pop with acknowledgement
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("count", "5");
        popRequest.Headers.Add("require_ack", "true");
        popRequest.Headers.Add("ttl_seconds", "30");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, popResponse.StatusCode);

        var result = await popResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
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
    public async Task BulkPopWithAck_AcknowledgeMultiple_RemovesAllLocks()
    {
        // Arrange - Push and lock 3 items
        var queueId = $"{fixture.QueueId}-bulk-ack-multi-{Guid.NewGuid():N}";

        for (int i = 0; i < 3; i++)
        {
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
                new ApiPushRequest(new List<ApiPushItem> {
                    new ApiPushItem(JsonSerializer.SerializeToElement(new { id = i }), Priority: 1)
                }));
        }

        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("count", "3");
        popRequest.Headers.Add("require_ack", "true");
        popRequest.Headers.Add("ttl_seconds", "30");
        var popResponse = await fixture.ApiClient.SendAsync(popRequest);
        var popResult = await popResponse.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();

        Assert.NotNull(popResult);
        Assert.NotNull(popResult.Items);
        Assert.Equal(3, popResult.Items.Count);

        // Act - Acknowledge all locks
        foreach (var item in popResult.Items)
        {
            var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge",
                new ApiAcknowledgeRequest(item.LockId));
            Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);
        }

        // Assert - Queue should be empty
        var finalPopRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        finalPopRequest.Headers.Add("count", "10");
        var finalPopResponse = await fixture.ApiClient.SendAsync(finalPopRequest);
        Assert.Equal(HttpStatusCode.NoContent, finalPopResponse.StatusCode);
    }

    [Fact]
    public async Task BulkPop_RequestMoreThanAvailable_ReturnsPartialResults()
    {
        // Arrange - Push only 3 items
        var queueId = $"{fixture.QueueId}-partial-{Guid.NewGuid():N}";

        for (int i = 0; i < 3; i++)
        {
            await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
                new ApiPushRequest(new List<ApiPushItem> {
                    new ApiPushItem(JsonSerializer.SerializeToElement(new { id = i }), Priority: 1)
                }));
        }

        // Act - Request 10 items but only 3 exist
        var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest.Headers.Add("count", "10");
        var response = await fixture.ApiClient.SendAsync(popRequest);

        // Assert - Should return 3 items with 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ApiPopResponse>();
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(3, result.Items.Count);
    }

    // gRPC Bulk Pop Tests

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
    public async Task BulkPop_Grpc_Count10_ReturnsRepeatedFields()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-bulk10-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        var pushRequest = new PushRequest { QueueId = queueId };
        for (int i = 0; i < 10; i++)
        {
            pushRequest.Items.Add(new PushItem
            {
                ItemJson = $"{{\"id\":{i}}}",
                Priority = 1
            });
        }
        await client.PushAsync(pushRequest);

        // Act
        var popResponse = await client.PopAsync(new PopRequest
        {
            QueueId = queueId,
            Count = 10
        });

        // Assert
        Assert.Equal(PopResponse.ResultOneofCase.Success, popResponse.ResultCase);
        Assert.Equal(10, popResponse.Success.ItemJson.Count);
        Assert.Equal(10, popResponse.Success.Priority.Count);

        // Verify FIFO order
        for (int i = 0; i < 10; i++)
        {
            Assert.Contains($"\"id\":{i}", popResponse.Success.ItemJson[i]);
        }
    }

    [Fact]
    public async Task BulkPopWithAck_Grpc_ReturnsMultipleLockIds()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-bulk-ack-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        var pushRequest = new PushRequest { QueueId = queueId };
        for (int i = 0; i < 5; i++)
        {
            pushRequest.Items.Add(new PushItem
            {
                ItemJson = $"{{\"id\":{i}}}",
                Priority = 1
            });
        }
        await client.PushAsync(pushRequest);

        // Act
        var popResponse = await client.PopWithAckAsync(new PopWithAckRequest
        {
            QueueId = queueId,
            TtlSeconds = 30,
            Count = 5
        });

        // Assert
        Assert.Equal(PopWithAckResponse.ResultOneofCase.Success, popResponse.ResultCase);
        Assert.Equal(5, popResponse.Success.ItemJson.Count);
        Assert.Equal(5, popResponse.Success.LockId.Count);
        Assert.Equal(5, popResponse.Success.LockExpiresAt.Count);

        // Each lock ID should be unique
        Assert.Equal(5, popResponse.Success.LockId.Distinct().Count());
    }

    [Fact]
    public async Task BulkPop_Grpc_InvalidCount_ThrowsInvalidArgument()
    {
        // Arrange
        var queueId = $"{fixture.QueueId}-grpc-invalid-count-{Guid.NewGuid():N}";
        var client = CreateGrpcClient();

        // Act & Assert - Count > 100 should throw
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.PopAsync(new PopRequest { QueueId = queueId, Count = 101 }));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        // Note: Count = 0 is valid (defaults to 1 in protobuf)
    }

    [Fact]
    public async Task BulkOperations_1000Messages_SinglePush()
    {
        // This test documents a bug where pushing 1000 items in one batch only persists 100
        var queueId = $"{fixture.QueueId}-bulk1000-single-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 1000;
        var pushItems = new List<ApiPushItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            pushItems.Add(new ApiPushItem(itemElement, Priority: 1));
        }

        // Push all 1000 items in one bulk operation
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(pushItems));
        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);

        var pushResult = await pushResponse.Content.ReadFromJsonAsync<ApiPushResponse>();
        Assert.NotNull(pushResult);
        Assert.True(pushResult.Success, $"Push failed: {pushResult.Message}");
        Assert.Equal(totalItems, pushResult.ItemsPushed); // API reports 1000 pushed

        // Act - Pop 1000 items in 10 batches of 100
        var lockIds = new List<string>();
        for (int batch = 0; batch < 10; batch++)
        {
            var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
            popRequest.Headers.Add("count", "100");
            popRequest.Headers.Add("require_ack", "true");
            popRequest.Headers.Add("ttl_seconds", "300");
            popRequest.Headers.Add("allow_competing_consumers", "true");

            var response = await fixture.ApiClient.SendAsync(popRequest);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                // BUG: Queue becomes empty after first batch even though 1000 were "pushed"
                Assert.Fail($"Queue empty after batch {batch}, only popped {lockIds.Count} items (expected 1000)");
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);

            lockIds.AddRange(result.Items.Select(item => item.LockId));
        }

        var totalPopped = lockIds.Count;

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
        Assert.Equal(totalItems, totalPopped);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 30, $"Test took {elapsedSeconds:F2}s (expected <30s, baseline for {totalItems} items)");
    }

    [Fact]
    public async Task BulkOperations_1000Messages_SinglePush_ParallelPop()
    {
        var queueId = $"{fixture.QueueId}-bulk1000-parallel-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 1000;
        var pushItems = new List<ApiPushItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            pushItems.Add(new ApiPushItem(itemElement, Priority: 1));
        }

        // Push all 1000 items in one bulk operation
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(pushItems));
        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);

        var pushResult = await pushResponse.Content.ReadFromJsonAsync<ApiPushResponse>();
        Assert.NotNull(pushResult);
        Assert.True(pushResult.Success, $"Push failed: {pushResult.Message}");
        Assert.Equal(totalItems, pushResult.ItemsPushed);

        // Act - Pop 1000 items in 10 parallel batches of 100
        var lockIds = new ConcurrentBag<string>();
        var popTasks = Enumerable.Range(0, 10).Select(async batch =>
        {
            var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
            popRequest.Headers.Add("count", "100");
            popRequest.Headers.Add("require_ack", "true");
            popRequest.Headers.Add("ttl_seconds", "300");
            popRequest.Headers.Add("allow_competing_consumers", "true");

            var response = await fixture.ApiClient.SendAsync(popRequest);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return 0;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);

            foreach (var item in result.Items)
            {
                lockIds.Add(item.LockId);
            }

            return result.Items.Count;
        });

        var poppedCounts = await Task.WhenAll(popTasks);
        var totalPopped = poppedCounts.Sum();

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
        Assert.Equal(totalItems, totalPopped);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 30, $"Test took {elapsedSeconds:F2}s (expected <30s for {totalItems} items with parallel pops)");
    }

    [Fact(Skip = "app is hardcoded to reject a batch size > 1000")]
    public async Task BulkOperations_10000Messages_SinglePush_ParallelPop()
    {
        var queueId = $"{fixture.QueueId}-bulk10000-parallel-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 10000;
        var pushItems = new List<ApiPushItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            pushItems.Add(new ApiPushItem(itemElement, Priority: 1));
        }

        // Push all 10000 items in one bulk operation
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(pushItems));
        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);

        var pushResult = await pushResponse.Content.ReadFromJsonAsync<ApiPushResponse>();
        Assert.NotNull(pushResult);
        Assert.True(pushResult.Success, $"Push failed: {pushResult.Message}");
        Assert.Equal(totalItems, pushResult.ItemsPushed);

        // Act - Pop 10000 items in 100 parallel batches of 100
        var lockIds = new ConcurrentBag<string>();
        var popTasks = Enumerable.Range(0, 100).Select(async batch =>
        {
            var popRequest = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
            popRequest.Headers.Add("count", "100");
            popRequest.Headers.Add("require_ack", "true");
            popRequest.Headers.Add("ttl_seconds", "300");
            popRequest.Headers.Add("allow_competing_consumers", "true");

            var response = await fixture.ApiClient.SendAsync(popRequest);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return 0;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
            Assert.NotNull(result);
            Assert.NotNull(result.Items);

            foreach (var item in result.Items)
            {
                lockIds.Add(item.LockId);
            }

            return result.Items.Count;
        });

        var poppedCounts = await Task.WhenAll(popTasks);
        var totalPopped = poppedCounts.Sum();

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
        Assert.Equal(totalItems, totalPopped);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 60, $"Test took {elapsedSeconds:F2}s (expected <60s for {totalItems} items with parallel pops)");
    }

    [Fact]
    public async Task BulkOperations_101Messages_TwoPushes()
    {
        // This test documents a bug where pushing 100 then 1 more only persists the first 100
        var queueId = $"{fixture.QueueId}-bulk101-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 101;

        // Push first 100 items
        var pushItems1 = new List<ApiPushItem>();
        for (int i = 0; i < 100; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            pushItems1.Add(new ApiPushItem(itemElement, Priority: 1));
        }

        var pushResponse1 = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(pushItems1));
        Assert.Equal(HttpStatusCode.OK, pushResponse1.StatusCode);

        var pushResult1 = await pushResponse1.Content.ReadFromJsonAsync<ApiPushResponse>();
        Assert.NotNull(pushResult1);
        Assert.True(pushResult1.Success, $"Push batch 1 failed: {pushResult1.Message}");
        Assert.Equal(100, pushResult1.ItemsPushed);

        // Push 101st item
        List<ApiPushItem> pushItems2 =
        [
            new ApiPushItem(JsonSerializer.SerializeToElement(new { id = 100, value = "item-100" }), Priority: 1)
        ];

        var pushResponse2 = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(pushItems2));
        Assert.Equal(HttpStatusCode.OK, pushResponse2.StatusCode);

        var pushResult2 = await pushResponse2.Content.ReadFromJsonAsync<ApiPushResponse>();
        Assert.NotNull(pushResult2);
        Assert.True(pushResult2.Success, $"Push batch 2 failed: {pushResult2.Message}");
        Assert.Equal(1, pushResult2.ItemsPushed);

        // Act - Pop first batch of 100 with acknowledgement
        var popRequest1 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest1.Headers.Add("count", "100");
        popRequest1.Headers.Add("require_ack", "true");
        popRequest1.Headers.Add("ttl_seconds", "300");
        popRequest1.Headers.Add("allow_competing_consumers", "true");

        var popResponse1 = await fixture.ApiClient.SendAsync(popRequest1);
        Assert.Equal(HttpStatusCode.OK, popResponse1.StatusCode);

        var popResult1 = await popResponse1.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popResult1);
        Assert.NotNull(popResult1.Items);
        Assert.Equal(100, popResult1.Items.Count);

        // Pop second batch - should get 1 remaining item
        var popRequest2 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest2.Headers.Add("count", "100");
        popRequest2.Headers.Add("require_ack", "true");
        popRequest2.Headers.Add("ttl_seconds", "300");
        popRequest2.Headers.Add("allow_competing_consumers", "true");

        var popResponse2 = await fixture.ApiClient.SendAsync(popRequest2);

        // BUG: Queue returns NoContent even though 101st item was pushed
        if (popResponse2.StatusCode == HttpStatusCode.NoContent)
        {
            Assert.Fail("Queue empty after first 100 items, but 101st item was pushed successfully");
        }

        Assert.Equal(HttpStatusCode.OK, popResponse2.StatusCode);

        var popResult2 = await popResponse2.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popResult2);
        Assert.NotNull(popResult2.Items);
        Assert.Single(popResult2.Items);

        // Collect all lock IDs
        var lockIds = popResult1.Items.Select(item => item.LockId)
            .Concat(popResult2.Items.Select(item => item.LockId))
            .ToList();
        var totalPopped = lockIds.Count;

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
        Assert.Equal(totalItems, totalPopped);
        Assert.Equal(totalItems, lockIds.Distinct().Count());
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 10, $"Test took {elapsedSeconds:F2}s (expected <10s, baseline for {totalItems} items)");
    }

    [Fact]
    public async Task BulkOperations_100Messages_WithTiming()
    {
        // Working baseline test: Push 100 items in one batch, pop in two batches (50 + 50)
        var queueId = $"{fixture.QueueId}-bulk100-{Guid.NewGuid():N}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        const int totalItems = 100;
        var pushItems = new List<ApiPushItem>();
        for (int i = 0; i < totalItems; i++)
        {
            var itemElement = JsonSerializer.SerializeToElement(new { id = i, value = $"item-{i}" });
            pushItems.Add(new ApiPushItem(itemElement, Priority: 1));
        }

        // Push all items
        var pushResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push",
            new ApiPushRequest(pushItems));
        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);

        var pushResult = await pushResponse.Content.ReadFromJsonAsync<ApiPushResponse>();
        Assert.NotNull(pushResult);
        Assert.True(pushResult.Success, $"Push failed: {pushResult.Message}");
        Assert.Equal(totalItems, pushResult.ItemsPushed);

        // Act - Pop first batch of 50 with acknowledgement
        var popRequest1 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest1.Headers.Add("count", "50");
        popRequest1.Headers.Add("require_ack", "true");
        popRequest1.Headers.Add("ttl_seconds", "300");
        popRequest1.Headers.Add("allow_competing_consumers", "true");

        var popResponse1 = await fixture.ApiClient.SendAsync(popRequest1);
        Assert.Equal(HttpStatusCode.OK, popResponse1.StatusCode);

        var popResult1 = await popResponse1.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popResult1);
        Assert.NotNull(popResult1.Items);
        Assert.Equal(50, popResult1.Items.Count);

        // Pop second batch of 50 remaining items
        var popRequest2 = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        popRequest2.Headers.Add("count", "50");
        popRequest2.Headers.Add("require_ack", "true");
        popRequest2.Headers.Add("ttl_seconds", "300");
        popRequest2.Headers.Add("allow_competing_consumers", "true");

        var popResponse2 = await fixture.ApiClient.SendAsync(popRequest2);
        Assert.Equal(HttpStatusCode.OK, popResponse2.StatusCode);

        var popResult2 = await popResponse2.Content.ReadFromJsonAsync<ApiPopWithAckResponse>();
        Assert.NotNull(popResult2);
        Assert.NotNull(popResult2.Items);
        Assert.Equal(50, popResult2.Items.Count);

        // Collect all lock IDs
        var lockIds = popResult1.Items.Select(item => item.LockId)
            .Concat(popResult2.Items.Select(item => item.LockId))
            .ToList();
        var totalPopped = lockIds.Count;

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
        Assert.Equal(totalItems, totalPopped);
        Assert.Equal(totalItems, lockIds.Distinct().Count()); // All lock IDs unique
        Assert.All(ackStatuses, status => Assert.Equal(HttpStatusCode.OK, status));

        // Output timing information
        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        Assert.True(elapsedSeconds < 10, $"Test took {elapsedSeconds:F2}s (expected <10s, baseline for {totalItems} items)");
    }
}
