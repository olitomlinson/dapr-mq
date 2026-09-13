using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class TopicTests(DaprTestFixture fixture)
{
    private static string NewTopicId() => $"test-topic-{Guid.NewGuid():N}";

    private async Task<ApiPublishResponse> PublishAsync(string topicId, object payload, int priority = 1)
    {
        var itemElement = JsonSerializer.SerializeToElement(payload);
        var request = new ApiPublishRequest(new List<ApiPushItem> { new ApiPushItem(itemElement, priority) });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/topic/{topicId}/publish", request);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"Publish failed: {response.StatusCode} - {content}");
        var result = await JsonSerializer.DeserializeAsync<ApiPublishResponse>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        return result!;
    }

    private async Task SubscribeAsync(string topicId, string subscriberId)
    {
        var response = await fixture.ApiClient.PostAsync($"/topic/{topicId}/subscribers/{subscriberId}", null);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Subscribe failed: {response.StatusCode} - {content}");
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
    public async Task Publish_TwoSubscribers_BothReceiveItemInTheirOwnQueue()
    {
        var topicId = NewTopicId();
        await SubscribeAsync(topicId, "sub-a");
        await SubscribeAsync(topicId, "sub-b");

        var publishResponse = await PublishAsync(topicId, new { id = 1 });

        var completed = await WaitForPublishCompleteAsync(topicId, publishResponse.PublishId, TimeSpan.FromSeconds(20));
        Assert.True(completed, "Publish did not complete relay within timeout");

        var itemsA = await PopAsync($"{topicId}-sub-sub-a");
        var itemsB = await PopAsync($"{topicId}-sub-sub-b");

        Assert.NotNull(itemsA);
        Assert.NotNull(itemsB);
        Assert.Single(itemsA!.Items);
        Assert.Single(itemsB!.Items);
        Assert.Equal(1, ((JsonElement)itemsA.Items[0].Item).GetProperty("id").GetInt32());
        Assert.Equal(1, ((JsonElement)itemsB.Items[0].Item).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Publish_TwoMessages_DeliveredToBothSubscribersInPublishOrder()
    {
        var topicId = NewTopicId();
        await SubscribeAsync(topicId, "sub-a");
        await SubscribeAsync(topicId, "sub-b");

        var first = await PublishAsync(topicId, new { id = 1 });
        var second = await PublishAsync(topicId, new { id = 2 });

        Assert.True(await WaitForPublishCompleteAsync(topicId, first.PublishId, TimeSpan.FromSeconds(20)));
        Assert.True(await WaitForPublishCompleteAsync(topicId, second.PublishId, TimeSpan.FromSeconds(20)));

        var itemsA = await PopAsync($"{topicId}-sub-sub-a");
        var itemsB = await PopAsync($"{topicId}-sub-sub-b");

        Assert.NotNull(itemsA);
        Assert.NotNull(itemsB);
        Assert.Equal(2, itemsA!.Items.Count);
        Assert.Equal(2, itemsB!.Items.Count);

        var idsA = itemsA.Items.Select(i => ((JsonElement)i.Item).GetProperty("id").GetInt32()).ToList();
        var idsB = itemsB.Items.Select(i => ((JsonElement)i.Item).GetProperty("id").GetInt32()).ToList();

        Assert.Equal(new List<int> { 1, 2 }, idsA);
        Assert.Equal(new List<int> { 1, 2 }, idsB);
    }

    [Fact]
    public async Task Unsubscribe_RemovesSubscriberFromFutureTargeting()
    {
        var topicId = NewTopicId();
        await SubscribeAsync(topicId, "sub-a");
        await SubscribeAsync(topicId, "sub-b");

        var deleteResponse = await fixture.ApiClient.DeleteAsync($"/topic/{topicId}/subscribers/sub-b");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var publishResponse = await PublishAsync(topicId, new { id = 99 });
        Assert.True(await WaitForPublishCompleteAsync(topicId, publishResponse.PublishId, TimeSpan.FromSeconds(20)));

        var statusResponse = await fixture.ApiClient.GetAsync($"/topic/{topicId}/publish/{publishResponse.PublishId}");
        statusResponse.EnsureSuccessStatusCode();
        var status = await statusResponse.Content.ReadFromJsonAsync<ApiPublishStatusResponse>();

        Assert.NotNull(status);
        Assert.DoesNotContain("sub-b", status!.TargetSubscriberIds);
        Assert.Contains("sub-a", status.TargetSubscriberIds);

        var listResponse = await fixture.ApiClient.GetAsync($"/topic/{topicId}/subscribers");
        listResponse.EnsureSuccessStatusCode();
        var list = await listResponse.Content.ReadFromJsonAsync<ApiListSubscribersResponse>();
        Assert.NotNull(list);
        Assert.DoesNotContain("sub-b", list!.SubscriberIds);
    }
}
