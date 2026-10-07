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
public class NackTests(DaprTestFixture fixture)
{
    // Matches the server default (DAPRMQ_MAX_DELIVERY_COUNT unset).
    private const int MaxDeliveryCount = 10;

    private async Task EnqueueAsync(string queueId, params int[] ids)
    {
        var items = ids.Select(id => new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id }), Priority: 1)).ToList();
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest(items));
        response.EnsureSuccessStatusCode();
    }

    private async Task<ApiDequeueLockedItem> DequeueLockedAsync(string queueId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        request.Headers.Add("require-ack", "true");
        request.Headers.Add("ttl-seconds", "30");
        var response = await fixture.ApiClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        return Assert.Single(result!.Items);
    }

    private static int IdOf(ApiDequeueLockedItem item) => ((JsonElement)item.Item).GetProperty("id").GetInt32();

    [Fact]
    public async Task Nack_ReturnsItemToItsOriginalPosition()
    {
        var queueId = $"{fixture.QueueId}-nack-position-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 1, 2);

        var first = await DequeueLockedAsync(queueId);
        Assert.Equal(1, IdOf(first));

        var nackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/nack", new ApiNackRequest(first.LockId));
        Assert.Equal(HttpStatusCode.OK, nackResponse.StatusCode);

        var nack = await nackResponse.Content.ReadFromJsonAsync<ApiNackResponse>();
        Assert.True(nack!.Success);
        Assert.False(nack.DeadLettered);
        Assert.Equal(1, nack.DeliveryCount);

        // Redelivered ahead of item 2, not behind it.
        var redelivered = await DequeueLockedAsync(queueId);
        Assert.Equal(1, IdOf(redelivered));
    }

    [Fact]
    public async Task Nack_PastMaxDeliveryCount_DeadLetters()
    {
        var queueId = $"{fixture.QueueId}-nack-dlq-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 1);

        ApiNackResponse? nack = null;
        for (int i = 0; i <= MaxDeliveryCount; i++)
        {
            var locked = await DequeueLockedAsync(queueId);
            var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/nack", new ApiNackRequest(locked.LockId));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            nack = await response.Content.ReadFromJsonAsync<ApiNackResponse>();
        }

        Assert.True(nack!.DeadLettered);
        Assert.Equal($"{queueId}-deadletter", nack.DlqId);

        var empty = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        empty.Headers.Add("require-ack", "false");
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.ApiClient.SendAsync(empty)).StatusCode);

        var dlq = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}-deadletter/dequeue");
        dlq.Headers.Add("require-ack", "false");
        Assert.Equal(HttpStatusCode.OK, (await fixture.ApiClient.SendAsync(dlq)).StatusCode);
    }

    [Fact]
    public async Task Nack_LockNotFound_Returns404()
    {
        var queueId = $"{fixture.QueueId}-nack-no-lock-{Guid.NewGuid():N}";

        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/nack", new ApiNackRequest("nonexistent"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Grpc_Nack_ReturnsItemToItsOriginalPosition()
    {
        var queueId = $"{fixture.QueueId}-nack-grpc-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 1, 2);

        var first = await DequeueLockedAsync(queueId);

        var client = new GrpcService.DaprMQClient(GrpcChannel.ForAddress(fixture.GrpcUrl));
        var nack = await client.NackAsync(new NackRequest { QueueId = queueId, LockId = first.LockId });

        Assert.True(nack.Success);
        Assert.False(nack.DeadLettered);
        Assert.Equal(1, nack.DeliveryCount);
        Assert.Equal(1, IdOf(await DequeueLockedAsync(queueId)));
    }
}
