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
/// With no worker reachable, a gateway's actor call is certainly not delivered, and says so
/// (proposals/readiness-and-retries.md, section 2), instead of a 500 that looks like a bug.
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

    [Fact]
    public Task Rest_Enqueue_NoWorker_Returns503NotDelivered() => WithWorkersDownAsync(fixture, async () =>
    {
        var response = await fixture.GatewayClient.PostAsJsonAsync(
            $"/queue/outage-{Guid.NewGuid():N}/enqueue", new { items = new[] { new { item = new { n = 1 } } } });

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

        var ex = await Assert.ThrowsAsync<RpcException>(() => client.EnqueueAsync(request).ResponseAsync);

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal("not-delivered", ex.Trailers.GetValue("daprmq-delivery"));
    });
}
