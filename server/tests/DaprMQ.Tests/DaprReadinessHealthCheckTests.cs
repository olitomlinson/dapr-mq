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
        TimeSpan? metadataTimeout = null,
        WorkerProbeOptions? workerProbe = null,
        IPlacementStateClient? placement = null)
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
            MetadataTimeout = metadataTimeout ?? TimeSpan.FromSeconds(2),
            WorkerProbe = workerProbe
        }, placement);
    }

    private static readonly WorkerProbeOptions WorkerProbe = new()
    {
        AppId = "rel-daprmq-worker",
        ActorType = "rel-daprmq-QueueActor",
        AppPort = 8080,
        Namespace = "default",
        ProbeTimeout = TimeSpan.FromMilliseconds(200)
    };

    private static Mock<IPlacementStateClient> Placement(params string[]? hosts)
    {
        var placement = new Mock<IPlacementStateClient>();
        placement.Setup(p => p.GetHostsAsync(WorkerProbe.AppId, WorkerProbe.ActorType, WorkerProbe.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hosts?.Select(IPAddress.Parse).ToList());
        return placement;
    }

    // Gateway metadata for the local sidecar; worker probes answered per host.
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Gateway(
        Func<string, CancellationToken, Task<HttpResponseMessage>> probe) =>
        (r, ct) => r.RequestUri!.Host == "localhost"
            ? Respond(GatewayMetadata)(r, ct)
            : probe(r.RequestUri.Host, ct);

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

    private static int ProbeCount(Mock<HttpMessageHandler> handler) =>
        handler.Invocations.Count(i => ((HttpRequestMessage)i.Arguments[0]).RequestUri!.AbsolutePath == "/health/ready");

    private static string[] ProbedHosts(Mock<HttpMessageHandler> handler) =>
        handler.Invocations.Select(i => ((HttpRequestMessage)i.Arguments[0]).RequestUri!)
            .Where(u => u.AbsolutePath == "/health/ready").Select(u => u.Host).ToArray();

    [Fact]
    public async Task Gateway_AllWorkersReady_StopsAtFirstSuccess()
    {
        var check = CreateCheck(true, Gateway((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))),
            requiredActorType: null, out var handler, workerProbe: WorkerProbe, placement: Placement("10.0.0.1", "10.0.0.2", "10.0.0.3").Object);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(3, result.Data["registered"]);
        Assert.Equal(1, result.Data["probed"]);
        Assert.Equal(1, ProbeCount(handler));
        Assert.Matches(@"^http://10\.0\.0\.[123]:8080/health/ready$", handler.Invocations
            .Select(i => ((HttpRequestMessage)i.Arguments[0]).RequestUri!.ToString()).Last());
    }

    [Fact]
    public async Task Gateway_OneOfTwoWorkersReady_ReturnsHealthy()
    {
        var check = CreateCheck(true, Gateway((host, _) => Task.FromResult(new HttpResponseMessage(
                host == "10.0.0.2" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable))),
            requiredActorType: null, out var handler, workerProbe: WorkerProbe, placement: Placement("10.0.0.1", "10.0.0.2").Object);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("10.0.0.2", ProbedHosts(handler).Last());
    }

    [Fact]
    public async Task Gateway_ProbesLastReadyWorkerFirst()
    {
        var check = CreateCheck(true, Gateway((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))),
            requiredActorType: null, out var handler, workerProbe: WorkerProbe, placement: Placement("10.0.0.1", "10.0.0.2", "10.0.0.3").Object);

        await RunAsync(check);
        var first = ProbedHosts(handler).Single();
        for (var i = 0; i < 5; i++)
        {
            await RunAsync(check);
        }

        Assert.All(ProbedHosts(handler), h => Assert.Equal(first, h));
    }

    [Fact]
    public async Task Gateway_LastReadyWorkerGone_FallsBackToOthers()
    {
        var down = new HashSet<string>();
        var check = CreateCheck(true, Gateway((host, _) => Task.FromResult(new HttpResponseMessage(
                down.Contains(host) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK))),
            requiredActorType: null, out var handler, workerProbe: WorkerProbe, placement: Placement("10.0.0.1", "10.0.0.2").Object);

        await RunAsync(check);
        down.Add(ProbedHosts(handler).Single());

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(2, result.Data["probed"]);
    }

    [Fact]
    public async Task Gateway_NoWorkerReady_ReturnsUnhealthy()
    {
        var check = CreateCheck(true, Gateway(async (host, ct) =>
            {
                if (host == "10.0.0.1")
                {
                    throw new HttpRequestException("refused");
                }

                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            requiredActorType: null, out _, workerProbe: WorkerProbe, placement: Placement("10.0.0.1", "10.0.0.2").Object);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("workers", result.Description);
        Assert.Equal(2, result.Data["registered"]);
        Assert.Equal(2, result.Data["probed"]);
    }

    [Fact]
    public async Task Gateway_NoWorkerRegistered_ReturnsUnhealthy_WithoutProbing()
    {
        var check = CreateCheck(true, Respond(GatewayMetadata), requiredActorType: null, out var handler,
            workerProbe: WorkerProbe, placement: Placement().Object);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("workers", result.Description);
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task Gateway_PlacementUnreachable_ReturnsUnhealthy()
    {
        var check = CreateCheck(true, Respond(GatewayMetadata), requiredActorType: null, out _,
            workerProbe: WorkerProbe, placement: Placement(null).Object);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("placement", result.Description);
    }

    [Fact]
    public async Task Gateway_NotHostReady_SkipsPlacement()
    {
        var placement = Placement("10.0.0.1");
        var check = CreateCheck(true, Respond(NotHostReadyMetadata), requiredActorType: null, out _,
            workerProbe: WorkerProbe, placement: placement.Object);

        var result = await RunAsync(check);

        Assert.Equal("actors", result.Description);
        placement.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HostReadyWithRequiredActorType_ReturnsHealthy_AndReadsMetadataEndpoint()
    {
        var placement = new Mock<IPlacementStateClient>();
        var check = CreateCheck(true, Respond(QueueActorMetadata), "QueueActor", out var handler, placement: placement.Object);

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        placement.VerifyNoOtherCalls();
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == "http://localhost:3500/v1.0/metadata"),
            ItExpr.IsAny<CancellationToken>());
    }
}
