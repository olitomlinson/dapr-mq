using System.Net;
using System.Net.Http.Json;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// The Prometheus scrape endpoint. Read from a worker (through Dapr service invocation, so it works
/// split or combined), since that is where queue actors count their work.
/// </summary>
[Collection("Dapr Collection")]
public class MetricsTests(DaprTestFixture fixture)
{
    [Fact]
    public async Task WorkerMetrics_CountEnqueuedItemsAndReportDepthPerQueue()
    {
        var queueId = $"metrics-{Guid.NewGuid():N}";
        var items = Enumerable.Range(0, 3).Select(i => new { item = new { n = i } });
        (await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new { items })).EnsureSuccessStatusCode();

        var response = await fixture.DaprSidecarClient.GetAsync($"/v1.0/invoke/{DaprTestEnvironment.WorkerAppId}/method/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches($"daprmq_queue_enqueued_total\\{{[^}}]*queue=\"{queueId}\"[^}}]*\\}} 3", body);
        Assert.Matches($"daprmq_queue_depth\\{{[^}}]*queue=\"{queueId}\"[^}}]*state=\"ready\"[^}}]*\\}} 3", body);
    }
}
