using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// Worker-internal admin reads (GET /internal/admin/*), reached as the operator reaches them: Dapr
/// service invocation to the worker app-id. Browsing must never change what it browses.
/// </summary>
[Collection("Dapr Collection")]
public class AdminReadTests(DaprTestFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string AdminUrl(string path) =>
        $"/v1.0/invoke/{DaprTestEnvironment.WorkerAppId}/method/internal/admin/{path}";

    private async Task<T> GetOkAsync<T>(string path)
    {
        var response = await fixture.DaprSidecarClient.GetAsync(AdminUrl(path));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {response.StatusCode} {body}");
        return JsonSerializer.Deserialize<T>(body, JsonOptions)!;
    }

    private async Task EnqueueAsync(string queueId, object payload, int priority = 1, string? sessionId = null)
    {
        var item = JsonSerializer.SerializeToElement(payload);
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new(item, priority, SessionId: sessionId) });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> LockOneAsync(string queueId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        request.Headers.Add("require-ack", "true");
        request.Headers.Add("ttl-seconds", "60");
        var response = await fixture.ApiClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var locked = await response.Content.ReadFromJsonAsync<ApiDequeueLockedResponse>();
        return locked!.Items![0].LockId;
    }

    [Fact]
    public async Task Queue_DetailMessagesAndLocks_WithoutChangingDepth()
    {
        var queueId = $"admin-q-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, new { n = 0 }, priority: 0);
        await EnqueueAsync(queueId, new { n = 1 });
        await EnqueueAsync(queueId, new { n = 2 });
        await EnqueueAsync(queueId, new { n = 3 });
        var lockId = await LockOneAsync(queueId); // takes n=0, the priority 0 item

        var detail = await GetOkAsync<QueueDetail>($"queues/{queueId}");
        Assert.Equal(3, detail.Ready);
        Assert.Equal(1, detail.Locked);
        Assert.Equal(4, detail.NextSequence);
        Assert.Equal(10, detail.MaxDeliveryCount);

        // Paged two at a time, in queue order.
        var first = await GetOkAsync<AdminMessagePage>($"queues/{queueId}/messages?limit=2");
        var second = await GetOkAsync<AdminMessagePage>($"queues/{queueId}/messages?limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");
        Assert.Equal([1L, 2L], first.Items.Select(m => m.Sequence));
        Assert.Equal([3L], second.Items.Select(m => m.Sequence));
        Assert.Null(second.NextCursor);
        Assert.Contains("\"n\":1", first.Items[0].Text);

        var message = await GetOkAsync<AdminMessage>(
            $"queues/{queueId}/messages/{Uri.EscapeDataString(first.Items[1].Location)}?sequence=2");
        Assert.Contains("\"n\":2", message.Text);

        var wrongSequence = await fixture.DaprSidecarClient.GetAsync(
            AdminUrl($"queues/{queueId}/messages/{Uri.EscapeDataString(first.Items[1].Location)}?sequence=99"));
        Assert.Equal(HttpStatusCode.Gone, wrongSequence.StatusCode);

        var locks = await GetOkAsync<AdminLockPage>($"queues/{queueId}/locks");
        var lockSummary = Assert.Single(locks.Items);
        Assert.Equal(lockId, lockSummary.LockId);
        Assert.Equal(0, lockSummary.Sequence);
        var lockDetail = await GetOkAsync<AdminLock>($"queues/{queueId}/locks/{lockId}");
        Assert.Contains("\"n\":0", lockDetail.Text);

        // Browsing changed nothing.
        var after = await GetOkAsync<QueueDetail>($"queues/{queueId}");
        Assert.Equal(3, after.Ready);
        Assert.Equal(1, after.Locked);
    }

    [Fact]
    public async Task Queue_Unknown_Is404()
    {
        var response = await fixture.DaprSidecarClient.GetAsync(AdminUrl($"queues/admin-missing-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Queue_Sessions_ArePagedWithDepth()
    {
        var queueId = $"admin-s-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, new { n = 1 }, sessionId: "a");
        await EnqueueAsync(queueId, new { n = 2 }, sessionId: "a");
        await EnqueueAsync(queueId, new { n = 3 }, sessionId: "b");

        var first = await GetOkAsync<AdminSessionPage>($"queues/{queueId}/sessions?limit=1");
        var second = await GetOkAsync<AdminSessionPage>($"queues/{queueId}/sessions?limit=1&cursor={first.NextCursor}");

        Assert.Equal("a", Assert.Single(first.Items).SessionId);
        Assert.Equal(2, first.Items[0].Ready);
        Assert.Equal("b", Assert.Single(second.Items).SessionId);
        Assert.Null(second.NextCursor);

        // A session-only queue still has a detail view.
        var detail = await GetOkAsync<QueueDetail>($"queues/{queueId}");
        Assert.Equal(2, detail.SessionCount);
    }

    [Fact]
    public async Task Topic_ListsSubscribersWithLag()
    {
        var topicId = $"admin-t-{Guid.NewGuid():N}";
        foreach (var subscriber in new[] { "audit", "search" })
        {
            var subscribe = await fixture.ApiClient.PostAsync($"/topic/{topicId}/subscribers/{subscriber}", null);
            Assert.Equal(HttpStatusCode.Created, subscribe.StatusCode);
        }

        var detail = await GetOkAsync<TopicDetail>($"topics/{topicId}");

        Assert.Equal(["audit", "search"], detail.Subscribers.Select(s => s.SubscriberId).Order());
        Assert.All(detail.Subscribers, s => Assert.Equal($"{topicId}-sub-{s.SubscriberId}", s.QueueId));
        Assert.All(detail.Subscribers, s => Assert.Equal(0, s.Lag));
    }

    [Fact]
    public async Task BlobMessage_PayloadLoadsOnRequest()
    {
        var queueId = $"admin-blob-{Guid.NewGuid():N}";
        var content = new byte[4096];
        new Random(7).NextBytes(content);
        var enqueue = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/enqueue-object") { Content = new ByteArrayContent(content) };
        (await fixture.ApiClient.SendAsync(enqueue)).EnsureSuccessStatusCode();

        var page = await GetOkAsync<AdminMessagePage>($"queues/{queueId}/messages");
        var message = Assert.Single(page.Items);
        Assert.True(message.IsBlobReference);

        var payload = await GetOkAsync<AdminPayload>(
            $"queues/{queueId}/messages/{Uri.EscapeDataString(message.Location)}/payload?sequence={message.Sequence}");

        Assert.Equal(content, Convert.FromBase64String(payload.ContentBase64));
        Assert.False(payload.Truncated);
    }
}
