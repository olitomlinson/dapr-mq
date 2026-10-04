using System.Net;
using Dapr.Client;
using DaprMQ.ApiServer.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Moq.Protected;

namespace DaprMQ.Tests;

public class DaprReadinessHealthCheckTests
{
    private const string QueueActorMetadata = """
        {"id":"daprmq-api","actorRuntime":{"runtimeStatus":"RUNNING","activeActors":[{"type":"QueueActor","count":0},{"type":"TopicActor"}],"hostReady":true,"placement":"placement: connected"}}
        """;

    private const string GatewayMetadata = """
        {"id":"daprmq-gateway","actorRuntime":{"runtimeStatus":"RUNNING","hostReady":true,"placement":"placement: connected"}}
        """;

    private const string NotHostReadyMetadata = """
        {"id":"daprmq-api","actorRuntime":{"runtimeStatus":"RUNNING","activeActors":[{"type":"QueueActor"}],"hostReady":false,"placement":"placement: disconnected"}}
        """;

    private const string NoQueueActorMetadata = """
        {"id":"daprmq-api","actorRuntime":{"runtimeStatus":"RUNNING","activeActors":[{"type":"TopicActor"}],"hostReady":true,"placement":"placement: connected"}}
        """;

    private static DaprReadinessHealthCheck CreateCheck(
        bool sidecarHealthy,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> metadata,
        string? requiredActorType,
        out Mock<HttpMessageHandler> handler,
        TimeSpan? metadataTimeout = null)
    {
        var daprClient = new Mock<DaprClient>();
        daprClient.Setup(c => c.CheckOutboundHealthAsync(It.IsAny<CancellationToken>())).ReturnsAsync(sidecarHealthy);

        handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(metadata);

        var httpClient = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return new DaprReadinessHealthCheck(daprClient.Object, factory.Object, new DaprReadinessOptions
        {
            DaprHttpEndpoint = "http://localhost:3500",
            RequiredActorType = requiredActorType,
            MetadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(2)
        });
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(string json) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

    private static Task<HealthCheckResult> RunAsync(DaprReadinessHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("dapr", check, HealthStatus.Unhealthy, null)
        });

    [Fact]
    public async Task SidecarUnhealthy_ReturnsUnhealthy_AndSkipsMetadata()
    {
        var check = CreateCheck(false, Respond(QueueActorMetadata), "QueueActor", out var handler);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("sidecar", result.Description);
        handler.Protected().Verify("SendAsync", Times.Never(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task MetadataRequestThrows_ReturnsUnhealthy()
    {
        var check = CreateCheck(true, (_, _) => throw new HttpRequestException("refused"), "QueueActor", out _);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("actors", result.Description);
    }

    [Fact]
    public async Task MetadataNonSuccessStatus_ReturnsUnhealthy()
    {
        var check = CreateCheck(true, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)), "QueueActor", out _);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("actors", result.Description);
    }

    [Fact]
    public async Task MetadataHangsPastTimeout_ReturnsUnhealthy()
    {
        var check = CreateCheck(true, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, "QueueActor", out _, metadataTimeout: TimeSpan.FromMilliseconds(100));

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("actors", result.Description);
    }

    [Fact]
    public async Task NotHostReady_ReturnsUnhealthy()
    {
        var check = CreateCheck(true, Respond(NotHostReadyMetadata), "QueueActor", out _);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("actors", result.Description);
    }

    [Fact]
    public async Task RequiredActorTypeMissing_ReturnsUnhealthy()
    {
        var check = CreateCheck(true, Respond(NoQueueActorMetadata), "QueueActor", out _);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("actors", result.Description);
    }

    [Fact]
    public async Task Gateway_HostReadyWithNoActorTypes_ReturnsHealthy()
    {
        var check = CreateCheck(true, Respond(GatewayMetadata), requiredActorType: null, out _);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task HostReadyWithRequiredActorType_ReturnsHealthy_AndReadsMetadataEndpoint()
    {
        var check = CreateCheck(true, Respond(QueueActorMetadata), "QueueActor", out var handler);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == "http://localhost:3500/v1.0/metadata"),
            ItExpr.IsAny<CancellationToken>());
    }
}
