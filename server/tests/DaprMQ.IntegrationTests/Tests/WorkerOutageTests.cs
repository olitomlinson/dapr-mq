using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Grpc;
using DaprMQ.IntegrationTests.Fixtures;
using Grpc.Core;
using Grpc.Net.Client;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// With no worker reachable, a gateway's actor call is certainly not delivered: the gateway retries
/// it within the caller's deadline (section 3), and if no worker returns in time, says so (section 2)
/// instead of a 500 that looks like a bug. See proposals/readiness-and-retries.md.
/// </summary>
[Collection("Dapr Split Topology Collection")]
public class WorkerOutageTests(SplitTopologyFixture fixture)
{
    private static async Task WithWorkersDownAsync(SplitTopologyFixture fixture, Func<Task> test)
    {
        await fixture.Environment.StopWorkersAsync();
        try
        {
            await test();
        }
        finally
        {
            await fixture.Environment.StartWorkersAsync();
            await fixture.Environment.WaitForReadyAsync(TimeSpan.FromMinutes(2));
        }
    }

    private static HttpRequestMessage Enqueue(string queueId, int timeoutMs) => new(HttpMethod.Post, $"/queue/{queueId}/enqueue")
    {
        Content = JsonContent.Create(new { items = new[] { new { item = new { n = 1 } } } }),
        Headers = { { "daprmq-timeout", timeoutMs.ToString() } },
    };

    private async Task<int> CountAndDrainAsync(string queueId)
    {
        var total = 0;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue") { Headers = { { "count", "1000" } } };
            using var response = await fixture.GatewayClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return total;
            }

            response.EnsureSuccessStatusCode();
            total += (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength();
        }
    }

    [Fact]
    public async Task Rest_Enqueue_WorkerReturnsWithinTheDeadline_Succeeds_AndStoresTheItemOnce()
    {
        var queueId = $"recovery-{Guid.NewGuid():N}";
        await fixture.Environment.StopWorkersAsync();
        try
        {
            var enqueue = fixture.GatewayClient.SendAsync(Enqueue(queueId, 45_000));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await fixture.Environment.StartWorkersAsync();

            var response = await enqueue;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await fixture.Environment.StartWorkersAsync();
            await fixture.Environment.WaitForReadyAsync(TimeSpan.FromMinutes(2));
        }

        Assert.Equal(1, await CountAndDrainAsync(queueId));
    }

    [Fact]
    public Task Rest_Enqueue_OutageOutlastsTheDeadline_Returns503Promptly() => WithWorkersDownAsync(fixture, async () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var response = await fixture.GatewayClient.SendAsync(Enqueue($"outage-{Guid.NewGuid():N}", 15_000));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Retried (daprd takes ~5 s per "no host" answer), stopped before the deadline cut an attempt off.
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(15));
    });

    [Fact]
    public Task Rest_Enqueue_NoWorker_Returns503NotDelivered() => WithWorkersDownAsync(fixture, async () =>
    {
        var response = await fixture.GatewayClient.SendAsync(Enqueue($"outage-{Guid.NewGuid():N}", 7_000));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("not-delivered", Assert.Single(response.Headers.GetValues("daprmq-delivery")));
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNAVAILABLE", body.GetProperty("errorCode").GetString());
    });

    [Fact]
    public Task Grpc_Enqueue_NoWorker_ReturnsUnavailableNotDelivered() => WithWorkersDownAsync(fixture, async () =>
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        using var channel = GrpcChannel.ForAddress(fixture.Environment.ApiServerGrpcUrl);
        var client = new GrpcService.DaprMQClient(channel);
        var request = new EnqueueRequest { QueueId = $"outage-{Guid.NewGuid():N}" };
        request.Items.Add(new EnqueueItem { ItemJson = "{\"n\":1}", Priority = 1 });

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.EnqueueAsync(request, deadline: DateTime.UtcNow.AddSeconds(8)).ResponseAsync);

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal("not-delivered", ex.Trailers.GetValue("daprmq-delivery"));
    });
}
