using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class AcknowledgeBatchTests(DaprTestFixture fixture)
{
    private async Task EnqueueAsync(string queueId, int count, string? sessionId = null)
    {
        var items = Enumerable.Range(0, count)
            .Select(id => new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id }), Priority: 1, SessionId: sessionId))
            .ToList();
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest(items));
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<string>> DequeueLockedAsync(string queueId, int count, int ttlSeconds = 60, string? leaseId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        request.Headers.Add("require-ack", "true");
        request.Headers.Add("count", count.ToString());
        request.Headers.Add("ttl-seconds", ttlSeconds.ToString());
        request.Headers.Add("allow-competing-consumers", "true");
        if (leaseId != null)
        {
            request.Headers.Add("lease-id", leaseId);
        }

        var response = await fixture.ApiClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        return result!.Items.Select(i => i.LockId).ToList();
    }

    private async Task<HttpResponseMessage> AcknowledgeBatchAsync(string queueId, IEnumerable<string> lockIds, string? leaseId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/acknowledge-batch")
        {
            Content = JsonContent.Create(new ApiAcknowledgeBatchRequest(lockIds.ToList()))
        };
        if (leaseId != null)
        {
            request.Headers.Add("lease-id", leaseId);
        }

        return await fixture.ApiClient.SendAsync(request);
    }

    /// <summary>
    /// Without competing consumers, a locked dequeue answers 423 while any lock is outstanding, so a
    /// 204 proves LockCount is back to 0.
    /// </summary>
    private async Task AssertNoLocksAndEmptyAsync(string queueId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        request.Headers.Add("require-ack", "true");
        var response = await fixture.ApiClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AcknowledgeBatch_1000LocksFromOneBulkDequeue_SettlesAllInOneCall()
    {
        var queueId = $"{fixture.QueueId}-ackbatch-1000-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 1000);
        var lockIds = await DequeueLockedAsync(queueId, 1000);
        Assert.Equal(1000, lockIds.Count);

        var response = await AcknowledgeBatchAsync(queueId, lockIds);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiAcknowledgeBatchResponse>();
        Assert.True(result!.Success);
        Assert.Equal(1000, result.ItemsAcknowledged);
        Assert.Equal(lockIds, result.Results.Select(r => r.LockId));
        Assert.All(result.Results, r => Assert.Equal("ACKNOWLEDGED", r.Outcome));
        await AssertNoLocksAndEmptyAsync(queueId);
    }

    [Fact]
    public async Task AcknowledgeBatch_MixedBatch_ReportsEachOutcomeAndSettlesTheValidOnes()
    {
        var queueId = $"{fixture.QueueId}-ackbatch-mixed-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 3);
        var expiring = Assert.Single(await DequeueLockedAsync(queueId, 1, ttlSeconds: 1));
        var alreadyAcked = Assert.Single(await DequeueLockedAsync(queueId, 1));
        var valid = Assert.Single(await DequeueLockedAsync(queueId, 1));

        var single = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", new ApiAcknowledgeRequest(alreadyAcked));
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(3));

        var response = await AcknowledgeBatchAsync(queueId, [valid, alreadyAcked, expiring, ""]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiAcknowledgeBatchResponse>();
        Assert.Equal(1, result!.ItemsAcknowledged);
        Assert.Equal(
            ["ACKNOWLEDGED", "LOCK_NOT_FOUND", "LOCK_EXPIRED", "INVALID_LOCK_ID"],
            result.Results.Select(r => r.Outcome));

        // The expired item went back to the queue; the valid one is gone for good.
        var redelivered = await DequeueLockedAsync(queueId, 10);
        Assert.Single(redelivered);
    }

    [Fact]
    public async Task AcknowledgeBatch_SentTwice_SecondReportsLockNotFoundForAll()
    {
        var queueId = $"{fixture.QueueId}-ackbatch-twice-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 5);
        var lockIds = await DequeueLockedAsync(queueId, 5);

        Assert.Equal(HttpStatusCode.OK, (await AcknowledgeBatchAsync(queueId, lockIds)).StatusCode);
        var response = await AcknowledgeBatchAsync(queueId, lockIds);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiAcknowledgeBatchResponse>();
        Assert.Equal(0, result!.ItemsAcknowledged);
        Assert.All(result.Results, r => Assert.Equal("LOCK_NOT_FOUND", r.Outcome));
        await AssertNoLocksAndEmptyAsync(queueId);
    }

    [Fact]
    public async Task AcknowledgeBatch_SessionQueue_RequiresTheLease()
    {
        var queueId = $"{fixture.QueueId}-ackbatch-sq-{Guid.NewGuid():N}";
        var sessionId = "s1";
        var sessionActorId = $"{queueId}-session-{sessionId}";
        await EnqueueAsync(queueId, 3, sessionId);

        var accept = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/accept", new ApiAcceptSessionRequest(sessionId, 30));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var lease = await accept.Content.ReadFromJsonAsync<ApiAcceptSessionResponse>();
        var lockIds = await DequeueLockedAsync(sessionActorId, 3, leaseId: lease!.LeaseId);

        var wrongLease = await AcknowledgeBatchAsync(sessionActorId, lockIds, "not-the-real-lease-id");
        Assert.Equal(HttpStatusCode.BadRequest, wrongLease.StatusCode);

        var response = await AcknowledgeBatchAsync(sessionActorId, lockIds, lease.LeaseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiAcknowledgeBatchResponse>();
        Assert.Equal(3, result!.ItemsAcknowledged);

        // After release a lapsed session hands its indexed locks back to the front: none should come back.
        var release = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/{sessionId}/release", new ApiReleaseSessionRequest(lease.LeaseId));
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        var drain = new HttpRequestMessage(HttpMethod.Post, $"/queue/{sessionActorId}/dequeue");
        drain.Headers.Add("require-ack", "false");
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.ApiClient.SendAsync(drain)).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task AcknowledgeBatch_EmptyOrOversizedList_Returns400(int count)
    {
        var queueId = $"{fixture.QueueId}-ackbatch-size-{Guid.NewGuid():N}";

        var response = await AcknowledgeBatchAsync(queueId, Enumerable.Range(0, count).Select(i => $"lock-{i}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AcknowledgeBatch_DuplicateIds_Returns400()
    {
        var queueId = $"{fixture.QueueId}-ackbatch-dupes-{Guid.NewGuid():N}";

        var response = await AcknowledgeBatchAsync(queueId, ["lock-1", "lock-1"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Grpc_AcknowledgeBatch_SettlesAll()
    {
        var queueId = $"{fixture.QueueId}-ackbatch-grpc-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 3);
        var lockIds = await DequeueLockedAsync(queueId, 3);

        var client = new GrpcService.DaprMQClient(GrpcChannel.ForAddress(fixture.GrpcUrl));
        var request = new AcknowledgeBatchRequest { QueueId = queueId };
        request.LockIds.AddRange(lockIds);
        var response = await client.AcknowledgeBatchAsync(request);

        Assert.True(response.Success);
        Assert.Equal(3, response.ItemsAcknowledged);
        Assert.Equal(lockIds, response.Results.Select(r => r.LockId));
        Assert.All(response.Results, r => Assert.Equal("ACKNOWLEDGED", r.Outcome));
        await AssertNoLocksAndEmptyAsync(queueId);
    }
}
