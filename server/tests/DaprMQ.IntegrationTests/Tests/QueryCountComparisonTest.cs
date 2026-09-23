using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// Empirically compares the two Dapr.Actors SDK builds in server/nuget-local/ - upstream PR
/// #1908's evict-and-reload fix ("daprmq-api:reentrancyfix") against the refresh-in-place
/// alternative ("daprmq-api:refreshfix", see docs/REENTRANCY_FIX_ROUND_TRIP_IMPACT.md) - by
/// counting actual Postgres SELECTs against the actor state table via pg_stat_statements while
/// running the same realistic scenario against each: a TopicActor with an actively-ticking 1s
/// relay reminder, published to repeatedly so ordinary Publish() calls interleave with reminder
/// ticks on the same MetadataKey. Builds its own dedicated DaprTestEnvironment per image (not the
/// shared "Dapr Collection" fixture) since it needs to run the same scenario twice against two
/// different app images.
///
/// Requires both images to already be built:
///   docker build --build-arg DaprActorsSdkVersion=1.18.4-reentrancyfix.1 -t daprmq-api:reentrancyfix .
///   docker build --build-arg DaprActorsSdkVersion=1.18.4-refreshfix.1 -t daprmq-api:refreshfix .
/// </summary>
public class QueryCountComparisonTest
{
    private const int RelayCycles = 20;

    [Fact]
    public async Task RefreshInPlaceBuild_CausesFewerActorStateReads_ThanEvictAndReloadBuild()
    {
        var evictCount = await RunScenarioAsync("daprmq-api:reentrancyfix");
        var refreshCount = await RunScenarioAsync("daprmq-api:refreshfix");

        Console.WriteLine($"Actor state SELECTs over {RelayCycles} publish/relay-tick cycles - evict-and-reload (reentrancyfix): {evictCount}, refresh-in-place (refreshfix): {refreshCount}, difference: {evictCount - refreshCount}");

        Assert.True(
            refreshCount < evictCount,
            $"Expected the refresh-in-place build (daprmq-api:refreshfix) to cause fewer actor " +
            $"state SELECTs against Postgres than the evict-and-reload build " +
            $"(daprmq-api:reentrancyfix) over {RelayCycles} publish/relay-tick cycles, but got " +
            $"evict={evictCount}, refresh={refreshCount}.");
    }

    /// <summary>
    /// Where each build's raw, per-statement (bound-parameter-included) Postgres log gets
    /// written, so the two files can be grepped/grouped/diffed directly against each other.
    /// </summary>
    private static string LogFilePathFor(string apiServerImage) =>
        Path.Combine(Path.GetTempPath(), $"dapr-mq-postgres-log-{apiServerImage.Split(':')[^1]}.txt");

    private static async Task<long> RunScenarioAsync(string apiServerImage)
    {
        var env = new DaprTestEnvironment();
        await env.InitializeAsync(null, apiServerImage, enableQueryInstrumentation: true);
        try
        {
            var topicId = $"query-count-{Guid.NewGuid():N}";

            await SubscribeAsync(env, topicId, "sub-a");
            await SubscribeAsync(env, topicId, "sub-b");

            // Prime: one publish starts the relay reminder ticking every 1s while there's
            // pending work. Let it settle before measuring.
            await PublishAsync(env, topicId, new { seq = 0 });
            await Task.Delay(TimeSpan.FromSeconds(2));

            await env.ResetQueryStatsAsync();
            var measurementStart = DateTime.UtcNow;

            // Interleave a publish with each relay tick - an ordinary MetadataKey write
            // landing between two reminder-driven MetadataKey reads, RelayCycles times.
            for (int i = 1; i <= RelayCycles; i++)
            {
                await PublishAsync(env, topicId, new { seq = i });
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            // Let the final tick process before measuring.
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            var logPath = LogFilePathFor(apiServerImage);
            await File.WriteAllTextAsync(logPath, await env.GetPostgresLogsAsync());
            Console.WriteLine($"Wrote per-statement Postgres log for {apiServerImage} to {logPath} (measurement window started {measurementStart:O})");

            return await env.GetActorStateSelectCallCountAsync();
        }
        finally
        {
            await env.DisposeAsync();
        }
    }

    private static async Task SubscribeAsync(DaprTestEnvironment env, string topicId, string subscriberId)
    {
        var response = await env.ApiClient.PostAsync($"/topic/{topicId}/subscribers/{subscriberId}", null);
        Assert.True(response.IsSuccessStatusCode, $"Subscribe failed: {response.StatusCode}");
    }

    private static async Task PublishAsync(DaprTestEnvironment env, string topicId, object payload)
    {
        var itemElement = JsonSerializer.SerializeToElement(payload);
        var request = new ApiPublishRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, Priority: 1) });
        var response = await env.ApiClient.PostAsJsonAsync($"/topic/{topicId}/publish", request);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Publish failed: {response.StatusCode} - {content}");
    }
}
