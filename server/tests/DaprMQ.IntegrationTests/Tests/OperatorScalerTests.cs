using System.Net.Http.Json;
using DaprMQ.IntegrationTests.Fixtures;
using Externalscaler;
using Grpc.Net.Client;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// Calls DaprMQ.Operator's KEDA external scaler the way KEDA does, against real queues. The operator
/// runs under its own app-id, so these only pass if depth is read through the worker.
/// </summary>
[Collection("Dapr Operator Collection")]
public class OperatorScalerTests(OperatorTestFixture fixture)
{
    private ExternalScaler.ExternalScalerClient CreateClient()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return new ExternalScaler.ExternalScalerClient(GrpcChannel.ForAddress(fixture.OperatorGrpcUrl));
    }

    private static ScaledObjectRef Ref(string queueId, params (string Key, string Value)[] metadata)
    {
        var scaledObject = new ScaledObjectRef { Name = "consumer", Namespace = "default" };
        scaledObject.ScalerMetadata["queueId"] = queueId;
        foreach (var (key, value) in metadata)
        {
            scaledObject.ScalerMetadata[key] = value;
        }

        return scaledObject;
    }

    private async Task<double> GetMetricAsync(ScaledObjectRef scaledObject) =>
        (await CreateClient().GetMetricsAsync(new GetMetricsRequest { ScaledObjectRef = scaledObject })).MetricValues.Single().MetricValueFloat;

    private async Task EnqueueAsync(string queueId, int count, string? sessionId = null)
    {
        var items = Enumerable.Range(0, count).Select(i => new { item = new { n = i }, sessionId });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", new { items });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetMetrics_ReportsQueueDepth_IncludingLockedItems()
    {
        var queueId = $"keda-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 5);

        Assert.Equal(5, await GetMetricAsync(Ref(queueId)));

        // Lock every item: the queue's own Count drops to 0, but the work is still outstanding.
        using var dequeue = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        dequeue.Headers.Add("require-ack", "true");
        dequeue.Headers.Add("count", "5");
        (await fixture.ApiClient.SendAsync(dequeue)).EnsureSuccessStatusCode();

        Assert.Equal(5, await GetMetricAsync(Ref(queueId)));
        Assert.True((await CreateClient().IsActiveAsync(Ref(queueId))).Result);
    }

    [Fact]
    public async Task IsActive_UnusedQueue_IsFalse()
    {
        var response = await CreateClient().IsActiveAsync(Ref($"keda-unused-{Guid.NewGuid():N}"));

        Assert.False(response.Result);
    }

    [Fact]
    public async Task IsActive_QueueWithItems_IsTrue()
    {
        var queueId = $"keda-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 1);

        var response = await CreateClient().IsActiveAsync(Ref(queueId));

        Assert.True(response.Result);
    }

    [Fact]
    public async Task GetMetrics_SessionsMode_CountsNonEmptySessions()
    {
        var queueId = $"keda-{Guid.NewGuid():N}";
        await EnqueueAsync(queueId, 2, "a");
        await EnqueueAsync(queueId, 3, "b");

        // Each session registers with its coordinator on its first enqueue; allow for the operator's depth cache.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        double sessions;
        while ((sessions = await GetMetricAsync(Ref(queueId, ("mode", "sessions")))) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        Assert.Equal(2, sessions);
    }

    [Fact]
    public async Task GetMetrics_SessionsMode_DropsAsSoonAsADrainedSessionIsReleased()
    {
        var queueId = $"keda-{Guid.NewGuid():N}";
        var sessions = Ref(queueId, ("mode", "sessions"));
        await EnqueueAsync(queueId, 1, "a");
        await EnqueueAsync(queueId, 1, "b");
        Assert.Equal(2, await WaitForMetricAsync(sessions, 2));

        // Drain "a" under a lease, then release it: the release sees nothing left.
        var accept = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/accept", new { sessionId = "a", leaseSeconds = 30 });
        accept.EnsureSuccessStatusCode();
        var leaseId = (await accept.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("leaseId").GetString()!;
        using var dequeue = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}-session-a/dequeue");
        dequeue.Headers.Add("lease-id", leaseId);
        dequeue.Headers.Add("require-ack", "false");
        (await fixture.ApiClient.SendAsync(dequeue)).EnsureSuccessStatusCode();
        (await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/a/release", new { leaseId })).EnsureSuccessStatusCode();

        // Only the operator's 1s depth cache stands between the release and the metric.
        Assert.Equal(1, await WaitForMetricAsync(sessions, 1));
    }

    private async Task<double> WaitForMetricAsync(ScaledObjectRef scaledObject, double expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        double value;
        while ((value = await GetMetricAsync(scaledObject)) != expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }
        return value;
    }
}
