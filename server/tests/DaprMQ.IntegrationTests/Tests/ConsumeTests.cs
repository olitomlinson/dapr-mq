using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// gRPC contract tests for Consume - the managed consume stream for plain queues - against a real
/// Dapr sidecar: window refill on settle, delivery counts, server-side lock renewal, and
/// nack-on-disconnect.
/// </summary>
[Collection("Dapr Collection")]
public class ConsumeTests(DaprTestFixture fixture)
{
    private string NewQueueId() => $"{fixture.QueueId}-consume-{Guid.NewGuid():N}";

    private GrpcService.DaprMQClient CreateGrpcClient()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        var channel = GrpcChannel.ForAddress(fixture.GrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler()
        });
        return new GrpcService.DaprMQClient(channel);
    }

    private async Task EnqueueAsync(string queueId, params object[] payloads)
    {
        var items = payloads.Select(p => new ApiEnqueueItem(JsonSerializer.SerializeToElement(p), 1)).ToList();
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new ApiEnqueueRequest(items));
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Enqueue failed: {response.StatusCode} - {content}");
    }

    private static ConsumeRequest Start(string queueId, int prefetchCount = 5, int lockTtlSeconds = 30) =>
        new() { Start = new ConsumeStart { QueueId = queueId, PrefetchCount = prefetchCount, LockTtlSeconds = lockTtlSeconds, AllowCompetingConsumers = true } };

    private static async Task<ConsumeResponse> NextAsync(AsyncDuplexStreamingCall<ConsumeRequest, ConsumeResponse> call, int timeoutSeconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        Assert.True(await call.ResponseStream.MoveNext(cts.Token), "Consume stream ended unexpectedly");
        return call.ResponseStream.Current;
    }

    [Fact]
    public async Task Consume_DeliversEveryItemInOrderAndRefillsAsTheyAreAcked()
    {
        var queueId = NewQueueId();
        await EnqueueAsync(queueId, new { seq = 1 }, new { seq = 2 }, new { seq = 3 });

        using var call = CreateGrpcClient().Consume();
        await call.RequestStream.WriteAsync(Start(queueId, prefetchCount: 2));

        var delivered = new List<ConsumeDelivered>();
        while (delivered.Count < 3)
        {
            var response = await NextAsync(call);
            Assert.Equal(ConsumeResponse.PayloadOneofCase.Delivered, response.PayloadCase);
            delivered.Add(response.Delivered);
            await call.RequestStream.WriteAsync(new ConsumeRequest { Ack = new ConsumeAck { LockId = response.Delivered.LockId } });
        }
        await call.RequestStream.CompleteAsync();

        Assert.Equal(new[] { 1, 2, 3 }, delivered.Select(d => JsonDocument.Parse(d.ItemJson).RootElement.GetProperty("seq").GetInt32()));
        Assert.All(delivered, d => Assert.Equal(1, d.DeliveryCount));

        var dequeue = await fixture.ApiClient.PostAsync($"/queue/{queueId}/dequeue", null);
        Assert.Equal(HttpStatusCode.NoContent, dequeue.StatusCode);
    }

    [Fact]
    public async Task Consume_NackedItemIsRedeliveredWithItsDeliveryCount()
    {
        var queueId = NewQueueId();
        await EnqueueAsync(queueId, new { seq = 1 });

        using var call = CreateGrpcClient().Consume();
        await call.RequestStream.WriteAsync(Start(queueId, prefetchCount: 1));

        var first = (await NextAsync(call)).Delivered;
        await call.RequestStream.WriteAsync(new ConsumeRequest { Nack = new ConsumeNack { LockId = first.LockId } });
        var second = (await NextAsync(call)).Delivered;
        await call.RequestStream.WriteAsync(new ConsumeRequest { Ack = new ConsumeAck { LockId = second.LockId } });
        await call.RequestStream.CompleteAsync();

        Assert.Equal(first.ItemJson, second.ItemJson);
        Assert.Equal(1, first.DeliveryCount);
        Assert.Equal(2, second.DeliveryCount);
    }

    [Fact]
    public async Task Consume_KeepsAnUnsettledLockAlivePastItsTtl()
    {
        var queueId = NewQueueId();
        await EnqueueAsync(queueId, new { seq = 1 });

        using var call = CreateGrpcClient().Consume();
        await call.RequestStream.WriteAsync(Start(queueId, prefetchCount: 1, lockTtlSeconds: 2));

        var delivered = (await NextAsync(call)).Delivered;
        await Task.Delay(TimeSpan.FromSeconds(6));

        // Still locked by this stream: no other consumer can take it.
        var competing = await fixture.ApiClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue")
        {
            Headers = { { "require-ack", "true" }, { "allow-competing-consumers", "true" } }
        });
        Assert.Equal(HttpStatusCode.NoContent, competing.StatusCode);

        await call.RequestStream.WriteAsync(new ConsumeRequest { Ack = new ConsumeAck { LockId = delivered.LockId } });
        await call.RequestStream.CompleteAsync();

        // The ack was accepted: the stream ends with no SettleFailed frame.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await call.ResponseStream.MoveNext(cts.Token))
        {
            Assert.NotEqual(ConsumeResponse.PayloadOneofCase.SettleFailed, call.ResponseStream.Current.PayloadCase);
        }
    }

    [Fact]
    public async Task Consume_DisconnectReturnsOutstandingItemsStraightAway()
    {
        var queueId = NewQueueId();
        await EnqueueAsync(queueId, new { seq = 1 });

        using (var call = CreateGrpcClient().Consume())
        {
            await call.RequestStream.WriteAsync(Start(queueId, prefetchCount: 1, lockTtlSeconds: 300));
            await NextAsync(call);
            await call.RequestStream.CompleteAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await call.ResponseStream.MoveNext(cts.Token))
            {
            }
        }

        // Well inside the 300s lock: the item is back because the stream nacked it on the way out.
        var dequeue = await fixture.ApiClient.PostAsync($"/queue/{queueId}/dequeue", null);
        Assert.Equal(HttpStatusCode.OK, dequeue.StatusCode);
    }

    [Fact]
    public async Task Consume_FirstMessageNotStart_EndsWithError()
    {
        using var call = CreateGrpcClient().Consume();
        await call.RequestStream.WriteAsync(new ConsumeRequest { Ack = new ConsumeAck { LockId = "nope" } });

        var response = await NextAsync(call);

        Assert.Equal(ConsumeResponse.PayloadOneofCase.Error, response.PayloadCase);
        Assert.Equal("INVALID_ARGUMENT", response.Error.ErrorCode);
    }
}
