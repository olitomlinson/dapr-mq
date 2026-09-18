using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.IntegrationTests.Fixtures;
using Grpc.Net.Client;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// Proves the acceptance criterion from the sessions plan's §8/§6.10.3: a SessionQueueConsumer
/// run with MaxConcurrentSessions >= 2 across two sessions preserves per-session FIFO order and
/// gives genuine cross-session throughput isolation (a slow session's handler doesn't stall a
/// fast session's delivery/handling), against a real Dapr sidecar. Mirrors
/// dotnet/tests/DaprMQ.IntegrationTests/Tests/SessionTests.cs's fixture usage.
/// </summary>
// Not [Collection("Dapr Collection")] - xUnit collection fixtures are scoped per test assembly,
// so the [CollectionDefinition] declared in DaprMQ.IntegrationTests's own assembly can't be
// resolved from here. IClassFixture works across the assembly boundary (via the ProjectReference
// on DaprMQ.IntegrationTests.csproj) and this project has only the one test class needing it, so
// there's no shared-fixture-across-classes benefit being given up.
public class SessionQueueConsumerTests(DaprTestFixture fixture) : IClassFixture<DaprTestFixture>
{
    private string NewQueueId() => $"{fixture.QueueId}-sq-{Guid.NewGuid():N}";

    private async Task EnqueueAsync(string queueId, string sessionId, int seq)
    {
        var itemElement = JsonSerializer.SerializeToElement(new { sessionId, seq });
        var request = new ApiEnqueueRequest([new ApiEnqueueItem(itemElement, 1, SessionId: sessionId)]);
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", request);
        Assert.True(response.IsSuccessStatusCode);
    }

    private DaprMQClient CreateClient()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        var grpcChannel = GrpcChannel.ForAddress(fixture.GrpcUrl, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });
        return new DaprMQClient(fixture.ApiClient, grpcChannel);
    }

    [Fact]
    public async Task MultiSessionConsumer_PreservesPerSessionOrder_AndIsolatesThroughput()
    {
        var queueId = NewQueueId();
        const int itemsPerSession = 5;

        for (var seq = 1; seq <= itemsPerSession; seq++)
        {
            await EnqueueAsync(queueId, "fast", seq);
            await EnqueueAsync(queueId, "slow", seq);
        }

        var observedSeqs = new ConcurrentDictionary<string, List<int>>();
        var fastDone = new TaskCompletionSource();
        var slowDone = new TaskCompletionSource();
        var stopwatch = Stopwatch.StartNew();
        double fastCompletedAtMs = -1;

        var client = CreateClient();
        var options = new SessionQueueConsumerOptions
        {
            MaxConcurrentSessions = 2,
            LeaseSeconds = 30,
            PrefetchCount = 10,
            MinBackoffSeconds = 1,
            MaxBackoffSeconds = 2
        };

        await using var consumer = new SessionQueueConsumer(client, queueId, options, async (ctx, ct) =>
        {
            if (ctx.SessionId == "slow")
            {
                await Task.Delay(300, ct);
            }

            var seq = ctx.Item.GetProperty("seq").GetInt32();
            var list = observedSeqs.GetOrAdd(ctx.SessionId, _ => []);
            lock (list)
            {
                list.Add(seq);
                if (list.Count == itemsPerSession)
                {
                    if (ctx.SessionId == "fast")
                    {
                        fastCompletedAtMs = stopwatch.Elapsed.TotalMilliseconds;
                        fastDone.TrySetResult();
                    }
                    else if (ctx.SessionId == "slow")
                    {
                        slowDone.TrySetResult();
                    }
                }
            }
        });

        await consumer.StartAsync();
        try
        {
            await Task.WhenAll(fastDone.Task, slowDone.Task).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.Equal(Enumerable.Range(1, itemsPerSession), observedSeqs["fast"]);
        Assert.Equal(Enumerable.Range(1, itemsPerSession), observedSeqs["slow"]);

        // Cross-session isolation: "slow" deliberately takes >= itemsPerSession * 300ms to fully
        // process (each item sleeps 300ms, processed sequentially within its own session stream).
        // "fast" must complete well within that window, proving it wasn't queued up behind "slow"
        // on a shared resource - each session runs on its own independent stream/slot.
        var slowMinimumDurationMs = itemsPerSession * 300;
        Assert.True(fastCompletedAtMs >= 0, "fast session never completed");
        Assert.True(fastCompletedAtMs < slowMinimumDurationMs,
            $"fast session took {fastCompletedAtMs}ms, expected well under the slow session's own {slowMinimumDurationMs}ms floor - cross-session isolation appears broken");
    }
}
