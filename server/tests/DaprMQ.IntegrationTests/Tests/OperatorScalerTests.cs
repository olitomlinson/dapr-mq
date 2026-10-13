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

        // Session actors register with their coordinator as part of activation; allow for that.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        double sessions;
        while ((sessions = await GetMetricAsync(Ref(queueId, ("mode", "sessions")))) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        Assert.Equal(2, sessions);
    }
}
