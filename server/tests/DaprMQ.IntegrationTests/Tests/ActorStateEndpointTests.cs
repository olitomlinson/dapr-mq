using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// The internal read of one actor state key (GET /internal/actor-state/{type}/{id}/{key}) that lets
/// the data and control planes read each other's actor state across app-ids. Reached as the other
/// plane reaches it: Dapr service invocation to the owning app-id.
/// </summary>
[Collection("Dapr Collection")]
public class ActorStateEndpointTests(DaprTestFixture fixture)
{
    private static string QueueActorType => Environment.GetEnvironmentVariable("QUEUE_ACTOR_TYPE_NAME") ?? "QueueActor";

    private static string StateUrl(string actorType, string actorId, string key) =>
        $"/v1.0/invoke/{DaprTestEnvironment.WorkerAppId}/method/internal/actor-state/{actorType}/{actorId}/{key}";

    [Fact]
    public async Task HostedType_ReturnsTheStoredState()
    {
        var queueId = $"actor-state-{Guid.NewGuid():N}";
        var request = new ApiEnqueueRequest([new(JsonSerializer.SerializeToElement(new { n = 1 }), 1)]);
        (await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", request)).EnsureSuccessStatusCode();

        var response = await fixture.DaprSidecarClient.GetAsync(StateUrl(QueueActorType, queueId, "metadata"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode} {body}");
        using var metadata = JsonDocument.Parse(body);
        Assert.Equal(1, metadata.RootElement.GetProperty("queues").GetProperty("1").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task MissingKey_IsNoContent()
    {
        var response = await fixture.DaprSidecarClient.GetAsync(StateUrl(QueueActorType, $"never-used-{Guid.NewGuid():N}", "metadata"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task TypeNotHostedByThatApp_IsNotFound()
    {
        var response = await fixture.DaprSidecarClient.GetAsync(StateUrl("NotAnActorType", "x", "metadata"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
