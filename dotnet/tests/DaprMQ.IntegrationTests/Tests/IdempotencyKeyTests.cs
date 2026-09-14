using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// End-to-end tests for idempotency-key deduplication, run against a real Dapr sidecar and
/// Postgres state store - the only way to confirm the Postgres v2 component actually honors
/// Dapr's per-entry state TTL, since unit tests only exercise the mocked IActorStateManager.
/// </summary>
[Collection("Dapr Collection")]
public class IdempotencyKeyTests(DaprTestFixture fixture)
{
    private async Task<ApiPushResponse> PushAsync(string queueId, object payload, string? idempotencyKey = null, int priority = 1)
    {
        var itemElement = JsonSerializer.SerializeToElement(payload);
        var request = new ApiPushRequest(new List<ApiPushItem> { new ApiPushItem(itemElement, priority, idempotencyKey) });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/push", request);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Push failed: {response.StatusCode} - {content}");
        var result = await JsonSerializer.DeserializeAsync<ApiPushResponse>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        return result!;
    }

    private async Task<ApiPopResponse?> PopAsync(string queueId, int count = 10)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        request.Headers.Add("count", count.ToString());
        var response = await fixture.ApiClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ApiPopResponse>();
    }

    [Fact]
    public async Task Push_SameIdempotencyKeyTwiceViaSeparateHttpCalls_OnlyOneItemDelivered()
    {
        var queueId = $"{fixture.QueueId}-idem-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();

        var first = await PushAsync(queueId, new { id = 1 }, idempotencyKey: key);
        Assert.Equal(1, first.ItemsPushed);
        Assert.Equal(0, first.ItemsDeduplicated);

        // Different payload, same key - the key is what's deduped, not the content.
        var second = await PushAsync(queueId, new { id = 2 }, idempotencyKey: key);
        Assert.Equal(0, second.ItemsPushed);
        Assert.Equal(1, second.ItemsDeduplicated);

        var popResult = await PopAsync(queueId);
        Assert.NotNull(popResult);
        Assert.Single(popResult!.Items);
        Assert.Equal(1, ((JsonElement)popResult.Items[0].Item).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Push_DifferentIdempotencyKeys_BothItemsDelivered()
    {
        var queueId = $"{fixture.QueueId}-idem-diff-{Guid.NewGuid():N}";

        await PushAsync(queueId, new { id = 1 }, idempotencyKey: Guid.NewGuid().ToString());
        await PushAsync(queueId, new { id = 2 }, idempotencyKey: Guid.NewGuid().ToString());

        var popResult = await PopAsync(queueId);
        Assert.NotNull(popResult);
        Assert.Equal(2, popResult!.Items.Count);
    }

    private async Task SubscribeAsync(string topicId, string subscriberId, bool? dedupEnabled = null)
    {
        var response = await fixture.ApiClient.PostAsJsonAsync(
            $"/topic/{topicId}/subscribers/{subscriberId}",
            new ApiSubscribeRequest(DedupEnabled: dedupEnabled));
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Subscribe failed: {response.StatusCode} - {content}");
    }

    private async Task<ApiPublishResponse> PublishAsync(string topicId, object payload, string? idempotencyKey = null)
    {
        var itemElement = JsonSerializer.SerializeToElement(payload);
        var request = new ApiPublishRequest(new List<ApiPushItem> { new ApiPushItem(itemElement, 1, idempotencyKey) });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/topic/{topicId}/publish", request);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"Publish failed: {response.StatusCode} - {content}");
        var result = await JsonSerializer.DeserializeAsync<ApiPublishResponse>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        return result!;
    }

    private async Task<bool> WaitForPublishCompleteAsync(string topicId, string publishId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await fixture.ApiClient.GetAsync($"/topic/{topicId}/publish/{publishId}");
            if (response.IsSuccessStatusCode)
            {
                var status = await response.Content.ReadFromJsonAsync<ApiPublishStatusResponse>();
                if (status != null && status.Complete)
                {
                    return true;
                }
            }

            await Task.Delay(500);
        }

        return false;
    }

    [Fact]
    public async Task Publish_SameIdempotencyKeyTwice_DefaultSubscriberDedupesButOptedOutSubscriberDoesNot()
    {
        var topicId = $"idem-topic-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();

        await SubscribeAsync(topicId, "default-sub"); // dedup default (enabled)
        await SubscribeAsync(topicId, "no-dedup-sub", dedupEnabled: false);

        var first = await PublishAsync(topicId, new { id = 1 }, idempotencyKey: key);
        Assert.True(await WaitForPublishCompleteAsync(topicId, first.PublishId, TimeSpan.FromSeconds(20)));

        var second = await PublishAsync(topicId, new { id = 2 }, idempotencyKey: key);
        Assert.True(await WaitForPublishCompleteAsync(topicId, second.PublishId, TimeSpan.FromSeconds(20)));

        var defaultItems = await PopAsync($"{topicId}-sub-default-sub");
        var noDedupItems = await PopAsync($"{topicId}-sub-no-dedup-sub");

        Assert.NotNull(defaultItems);
        Assert.Single(defaultItems!.Items); // deduped - only the first publish's item landed

        Assert.NotNull(noDedupItems);
        Assert.Equal(2, noDedupItems!.Items.Count); // dedup opted out - both publishes landed
    }
}
