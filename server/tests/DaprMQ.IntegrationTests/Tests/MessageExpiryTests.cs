using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;
using Rpc = DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// Per-message expiry over HTTP and gRPC (proposals/message-expiry.md). The test stack sets
/// DAPRMQ_MIN_EXPIRY_TTL_SECONDS to 1 so an item can expire within a test.
/// </summary>
[Collection("Dapr Collection")]
public class MessageExpiryTests(DaprTestFixture fixture)
{
    // Matches the server default (DAPRMQ_DEFAULT_EXPIRY_TTL_SECONDS unset).
    private const int DefaultExpiryTtlSeconds = 31_536_000;
    private const int MaxExpiryTtlSeconds = 315_360_000;
    // Matches the server default (DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS unset).
    private const int DeadLetterExpiryTtlSeconds = 2_419_200;

    private async Task<HttpResponseMessage> EnqueueAsync(string queueId, int id, int? expiryTtlSeconds)
    {
        var item = new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id }), Priority: 1, ExpiryTtlSeconds: expiryTtlSeconds);
        return await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest([item]));
    }

    private Task<HttpResponseMessage> DequeueAsync(string queueId, bool requireAck)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        request.Headers.Add("require-ack", requireAck ? "true" : "false");
        request.Headers.Add("ttl-seconds", "30");
        return fixture.ApiClient.SendAsync(request);
    }

    private static int IdOf(object item) => ((JsonElement)item).GetProperty("id").GetInt32();

    [Fact]
    public async Task ExpiredItem_IsNotDelivered()
    {
        var queueId = $"{fixture.QueueId}-expiry-skip-{Guid.NewGuid():N}";
        (await EnqueueAsync(queueId, 1, expiryTtlSeconds: 1)).EnsureSuccessStatusCode();
        (await EnqueueAsync(queueId, 2, expiryTtlSeconds: 1)).EnsureSuccessStatusCode();

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var response = await DequeueAsync(queueId, requireAck: false);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredItem_IsSkipped_AndTheNextLiveItemIsDelivered()
    {
        var queueId = $"{fixture.QueueId}-expiry-next-{Guid.NewGuid():N}";
        (await EnqueueAsync(queueId, 1, expiryTtlSeconds: 1)).EnsureSuccessStatusCode();
        (await EnqueueAsync(queueId, 2, expiryTtlSeconds: null)).EnsureSuccessStatusCode();

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var response = await DequeueAsync(queueId, requireAck: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        Assert.Equal(2, IdOf(Assert.Single(result!.Items).Item));
    }

    [Fact]
    public async Task ItemLockedBeforeExpiry_CanBeAcknowledgedAfterIt()
    {
        var queueId = $"{fixture.QueueId}-expiry-ack-{Guid.NewGuid():N}";
        (await EnqueueAsync(queueId, 1, expiryTtlSeconds: 1)).EnsureSuccessStatusCode();

        var dequeue = await DequeueAsync(queueId, requireAck: true);
        Assert.Equal(HttpStatusCode.OK, dequeue.StatusCode);
        var locked = Assert.Single((await dequeue.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>())!.Items);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var ack = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", new ApiAcknowledgeRequest(locked.LockId));
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
    }

    [Fact]
    public async Task DequeuedItems_CarryExpiryAtAndExpiryTtlSeconds()
    {
        var queueId = $"{fixture.QueueId}-expiry-fields-{Guid.NewGuid():N}";
        double before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (await EnqueueAsync(queueId, 1, expiryTtlSeconds: 3600)).EnsureSuccessStatusCode();
        (await EnqueueAsync(queueId, 2, expiryTtlSeconds: null)).EnsureSuccessStatusCode();

        var plain = await DequeueAsync(queueId, requireAck: false);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        var plainItem = Assert.Single((await plain.Content.ReadFromJsonAsync<ApiDequeueResponse>())!.Items);
        Assert.Equal(3600, plainItem.ExpiryTtlSeconds);
        Assert.InRange(plainItem.ExpiryAt!.Value, before + 3600 - 5, before + 3600 + 30);
        Assert.Null(plainItem.OriginalExpiryTtlSeconds);

        var locked = await DequeueAsync(queueId, requireAck: true);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var lockedItem = Assert.Single((await locked.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>())!.Items);
        Assert.Equal(DefaultExpiryTtlSeconds, lockedItem.ExpiryTtlSeconds);
        Assert.InRange(lockedItem.ExpiryAt!.Value, before + DefaultExpiryTtlSeconds - 5, before + DefaultExpiryTtlSeconds + 30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MaxExpiryTtlSeconds + 1)]
    public async Task Enqueue_ExpiryTtlSecondsOutOfRange_Returns400(int expiryTtlSeconds)
    {
        var queueId = $"{fixture.QueueId}-expiry-invalid-{Guid.NewGuid():N}";
        var response = await EnqueueAsync(queueId, 1, expiryTtlSeconds);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private GrpcService.DaprMQClient CreateGrpcClient()
    {
        // The dedicated gRPC port speaks HTTP/2 without TLS (h2c).
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return new(GrpcChannel.ForAddress(fixture.GrpcUrl, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() }));
    }

    private static Rpc.EnqueueRequest GrpcEnqueue(string queueId, int id, int? expiryTtlSeconds)
    {
        var item = new Rpc.EnqueueItem { ItemJson = $"{{\"id\":{id}}}", Priority = 1 };
        if (expiryTtlSeconds is int ttl)
        {
            item.ExpiryTtlSeconds = ttl;
        }
        return new Rpc.EnqueueRequest { QueueId = queueId, Items = { item } };
    }

    [Fact]
    public async Task Grpc_ExpiredItem_IsNotDelivered()
    {
        var client = CreateGrpcClient();
        var queueId = $"{fixture.QueueId}-expiry-grpc-skip-{Guid.NewGuid():N}";
        await client.EnqueueAsync(GrpcEnqueue(queueId, 1, expiryTtlSeconds: 1));

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var response = await client.DequeueAsync(new Rpc.DequeueRequest { QueueId = queueId });
        Assert.Equal(Rpc.DequeueResponse.ResultOneofCase.Empty, response.ResultCase);
    }

    [Fact]
    public async Task Grpc_DequeuedItems_CarryExpiryAtAndExpiryTtlSeconds()
    {
        var client = CreateGrpcClient();
        var queueId = $"{fixture.QueueId}-expiry-grpc-fields-{Guid.NewGuid():N}";
        double before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await client.EnqueueAsync(GrpcEnqueue(queueId, 1, expiryTtlSeconds: 3600));

        var response = await client.DequeueLockedAsync(new Rpc.DequeueLockedRequest { QueueId = queueId, TtlSeconds = 30 });

        Assert.Equal(3600, Assert.Single(response.Success.ExpiryTtlSeconds));
        Assert.InRange(Assert.Single(response.Success.ExpiryAt), before + 3600 - 5, before + 3600 + 30);
        Assert.Equal(0, Assert.Single(response.Success.OriginalExpiryTtlSeconds));
    }

    [Fact]
    public async Task Grpc_Enqueue_ExpiryTtlSecondsOutOfRange_IsInvalidArgument()
    {
        var client = CreateGrpcClient();
        var queueId = $"{fixture.QueueId}-expiry-grpc-invalid-{Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<RpcException>(async () => await client.EnqueueAsync(GrpcEnqueue(queueId, 1, expiryTtlSeconds: 0)));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    private async Task<ApiDequeueLockedItem> DequeueLockedAsync(string queueId)
    {
        var response = await DequeueAsync(queueId, requireAck: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>())!.Items);
    }

    [Fact]
    public async Task DeadLetteredItems_GetTheDeadLetterExpiry_AndCarryTheOriginalTimeToLive()
    {
        var queueId = $"{fixture.QueueId}-expiry-dlq-{Guid.NewGuid():N}";
        double before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (await EnqueueAsync(queueId, 1, expiryTtlSeconds: 3600)).EnsureSuccessStatusCode();
        var locked = await DequeueLockedAsync(queueId);

        var deadLetter = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", new ApiDeadLetterRequest(locked.LockId));
        Assert.Equal(HttpStatusCode.OK, deadLetter.StatusCode);

        var response = await DequeueAsync($"{queueId}-deadletter", requireAck: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<ApiDequeueResponse>())!.Items);
        Assert.Equal(DeadLetterExpiryTtlSeconds, item.ExpiryTtlSeconds);
        Assert.InRange(item.ExpiryAt!.Value, before + DeadLetterExpiryTtlSeconds - 5, before + DeadLetterExpiryTtlSeconds + 30);
        Assert.Equal(3600, item.OriginalExpiryTtlSeconds);
    }

    [Fact]
    public async Task DeadLetter_FromADeadLetterQueue_Returns400DeadLetterNotAllowed()
    {
        var queueId = $"{fixture.QueueId}-expiry-dlq-dlq-{Guid.NewGuid():N}";
        (await EnqueueAsync(queueId, 1, expiryTtlSeconds: null)).EnsureSuccessStatusCode();
        var locked = await DequeueLockedAsync(queueId);
        (await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/deadletter", new ApiDeadLetterRequest(locked.LockId))).EnsureSuccessStatusCode();

        var dlqLocked = await DequeueLockedAsync($"{queueId}-deadletter");
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}-deadletter/deadletter", new ApiDeadLetterRequest(dlqLocked.LockId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("DEADLETTER_NOT_ALLOWED", (await response.Content.ReadFromJsonAsync<ApiDeadLetterResponse>())!.ErrorCode);

        // The lock was left as it was, so it can still be acknowledged.
        var ack = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}-deadletter/acknowledge", new ApiAcknowledgeRequest(dlqLocked.LockId));
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
    }
}
