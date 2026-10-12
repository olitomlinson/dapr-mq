using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.Interfaces;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.Operator.Console;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// The operator's admin console API (/api/console/*, CONSOLE_ENABLED), which reaches the workers'
/// internal admin reads over Dapr service invocation from its own app-id.
/// </summary>
[Collection("Dapr Operator Collection")]
public class ConsoleApiTests(OperatorTestFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<T> GetOkAsync<T>(string path)
    {
        var response = await fixture.OperatorClient.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<T>(body, JsonOptions)!;
    }

    private async Task EnqueueAsync(string queueId, int count)
    {
        var items = Enumerable.Range(0, count).Select(i => new { item = new { n = i } });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new { items });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task QueuesListsAnEnqueuedQueue_AndBrowsingLeavesItsDepthAlone()
    {
        var queueId = $"console-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 3);

        var row = await WaitForQueueRowAsync(queueId, TimeSpan.FromSeconds(15));
        Assert.Equal(3, row.Ready);
        Assert.Equal(0, row.Locked);

        var messages = await GetOkAsync<AdminMessagePage>($"/api/console/queues/{queueId}/messages");
        Assert.Equal([0L, 1L, 2L], messages.Items.Select(m => m.Sequence));

        // The location ("1:0:1") crosses the Dapr hop as an escaped path segment.
        var second = messages.Items[1];
        var message = await GetOkAsync<AdminMessage>(
            $"/api/console/queues/{queueId}/messages/{Uri.EscapeDataString(second.Location)}?sequence={second.Sequence}");
        Assert.Contains("\"n\":1", message.Text);

        var detail = await GetOkAsync<QueueDetail>($"/api/console/queues/{queueId}");
        Assert.Equal(3, detail.Ready);
    }

    [Fact]
    public async Task WorkerStatusesPassThrough()
    {
        var missing = await fixture.OperatorClient.GetAsync($"/api/console/queues/console-missing-{Guid.NewGuid():N}");
        var badLimit = await fixture.OperatorClient.GetAsync("/api/console/catalog?limit=0");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
    }

    [Fact]
    public async Task Metrics_WithoutPrometheus_Are501()
    {
        var response = await fixture.OperatorClient.GetAsync("/api/console/metrics/throughput");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task ServesTheConsoleApp()
    {
        var root = await fixture.OperatorClient.GetAsync("/");
        var deepLink = await fixture.OperatorClient.GetAsync("/queues/orders");
        var unknownApi = await fixture.OperatorClient.GetAsync("/api/console/nope");

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("<div id=\"root\">", await root.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
    }

    private async Task<ConsoleQueueRow> WaitForQueueRowAsync(string queueId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // Catalog ids sort ordinally, so a cursor just below the id makes it the first row.
            var page = await GetOkAsync<ConsoleQueuePage>($"/api/console/queues?limit=1&cursor={Uri.EscapeDataString(queueId[..^1])}");
            var row = page.Items.FirstOrDefault(r => r.Id == queueId);
            if (row != null)
            {
                return row;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{queueId} never appeared in /api/console/queues");
            await Task.Delay(500);
        }
    }
}
