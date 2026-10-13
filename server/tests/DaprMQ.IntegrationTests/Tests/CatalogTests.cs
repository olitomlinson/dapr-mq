using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// Worker-internal catalog read (GET /internal/admin/catalog), reached the way the operator reaches
/// it: Dapr service invocation to the worker app-id, so it works in the combined and split topologies.
/// </summary>
[Collection("Dapr Collection")]
public class CatalogTests(DaprTestFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Catalog_ListsQueuesDeadLettersAndTopics_ButNotSessions()
    {
        var queueId = $"catalog-q-{Guid.NewGuid():N}";
        var topicId = $"catalog-t-{Guid.NewGuid():N}";

        await EnqueueAsync(queueId, sessionId: null);
        await EnqueueAsync(queueId, sessionId: "s1");
        await DeadLetterOneAsync(queueId);
        var subscribe = await fixture.ApiClient.PostAsync($"/topic/{topicId}/subscribers/sub-a", null);
        Assert.Equal(HttpStatusCode.Created, subscribe.StatusCode);

        var entries = await WaitForEntriesAsync(
            e => e.Id == queueId || e.Id == $"{queueId}-deadletter" || e.Id == topicId,
            expected: 3,
            TimeSpan.FromSeconds(15));

        Assert.Equal(CatalogEntryKind.Queue, entries.Single(e => e.Id == queueId).Kind);
        var dlq = entries.Single(e => e.Id == $"{queueId}-deadletter");
        Assert.Equal(CatalogEntryKind.DeadLetter, dlq.Kind);
        Assert.Equal(queueId, dlq.ParentId);
        Assert.Equal(CatalogEntryKind.Topic, entries.Single(e => e.Id == topicId).Kind);

        var all = await ReadAllAsync(kind: null);
        Assert.DoesNotContain(all, e => e.Id.StartsWith($"{queueId}-session-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catalog_FiltersByKind()
    {
        var topicId = $"catalog-kind-{Guid.NewGuid():N}";
        var subscribe = await fixture.ApiClient.PostAsync($"/topic/{topicId}/subscribers/sub-a", null);
        Assert.Equal(HttpStatusCode.Created, subscribe.StatusCode);

        await WaitForEntriesAsync(e => e.Id == topicId, expected: 1, TimeSpan.FromSeconds(15));

        var topics = await ReadAllAsync(CatalogEntryKind.Topic);
        Assert.Contains(topics, e => e.Id == topicId);
        Assert.All(topics, e => Assert.Equal(CatalogEntryKind.Topic, e.Kind));
    }

    [Fact]
    public async Task Catalog_RejectsAnUnknownKind()
    {
        var response = await fixture.DaprSidecarClient.GetAsync(CatalogUrl("kind=nope"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string CatalogUrl(string query) =>
        $"/v1.0/invoke/{DaprTestEnvironment.WorkerAppId}/method/internal/admin/catalog?{query}";

    private async Task EnqueueAsync(string queueId, string? sessionId)
    {
        var item = JsonSerializer.SerializeToElement(new { value = "catalog" });
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new(item, 1, SessionId: sessionId) });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", request);
        response.EnsureSuccessStatusCode();
    }

    private async Task DeadLetterOneAsync(string queueId)
    {
        var dequeue = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeue.Headers.Add("require-ack", "true");
        dequeue.Headers.Add("ttl-seconds", "30");
        var dequeueResponse = await fixture.ApiClient.SendAsync(dequeue);
        dequeueResponse.EnsureSuccessStatusCode();
        var locked = await dequeueResponse.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();

        var response = await fixture.ApiClient.PostAsJsonAsync(
            $"/queue/{queueId}/deadletter", new ApiDeadLetterRequest(locked!.Items![0].LockId));
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<CatalogEntry>> WaitForEntriesAsync(Func<CatalogEntry, bool> match, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        List<CatalogEntry> found;
        do
        {
            found = (await ReadAllAsync(kind: null)).Where(match).ToList();
            if (found.Count >= expected)
            {
                return found;
            }
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);

        Assert.Fail($"Expected {expected} catalog entries, found {found.Count}: {string.Join(", ", found.Select(e => e.Id))}");
        return found;
    }

    private async Task<List<CatalogEntry>> ReadAllAsync(CatalogEntryKind? kind)
    {
        var all = new List<CatalogEntry>();
        string? cursor = null;
        do
        {
            var query = $"limit=1000{(kind is null ? "" : $"&kind={kind}")}{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var response = await fixture.DaprSidecarClient.GetAsync(CatalogUrl(query));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await response.Content.ReadFromJsonAsync<CatalogPage>(JsonOptions);
            all.AddRange(page!.Entries);
            cursor = page.NextCursor;
        } while (cursor is not null);

        return all;
    }
}
