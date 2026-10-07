using System.Net;
using DaprMQ.ApiServer.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Moq.Protected;

namespace DaprMQ.Tests;

/// <summary>
/// A gateway's "daprmq.DaprMQ.operations" signal: can any worker serve queue operations? Asked
/// through the gateway's own sidecar (Dapr service invocation reaches one worker per call), so any
/// success proves it. Never part of the gateway's readiness (proposals/readiness-and-retries.md, section 5).
/// </summary>
public class OperationsHealthCheckTests
{
    private static (OperationsHealthCheck Check, Mock<HttpMessageHandler> Handler) Create(params Func<CancellationToken, Task<HttpResponseMessage>>[] attempts)
    {
        var queue = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>(attempts);
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((_, ct) => queue.Dequeue()(ct));
        var h = handler.Object;
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(h));

        return (new OperationsHealthCheck(factory.Object, new OperationsHealthCheckOptions
        {
            DaprHttpEndpoint = "http://localhost:3500/",
            WorkerAppId = "rel-daprmq-worker",
            AttemptTimeout = TimeSpan.FromMilliseconds(200),
        }), handler);
    }

    private static Func<CancellationToken, Task<HttpResponseMessage>> Status(HttpStatusCode code) =>
        _ => Task.FromResult(new HttpResponseMessage(code));

    private static Task<HealthCheckResult> RunAsync(OperationsHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("operations", check, HealthStatus.Unhealthy, null) });

    [Fact]
    public async Task AWorkerAnswers_Healthy_ViaServiceInvocationOfTheWorkerOnlyRoute()
    {
        var (check, handler) = Create(Status(HttpStatusCode.OK));

        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get
                && r.RequestUri!.ToString() == "http://localhost:3500/v1.0/invoke/rel-daprmq-worker/method/internal/operations-ready"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task OneUnreadyWorker_ThenAReadyOne_Healthy()
    {
        var (check, _) = Create(Status(HttpStatusCode.ServiceUnavailable), _ => throw new HttpRequestException("refused"), Status(HttpStatusCode.OK));

        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);
    }

    [Fact]
    public async Task NoWorkerAnswersInThreeAttempts_Unhealthy()
    {
        var (check, handler) = Create(
            Status(HttpStatusCode.InternalServerError),
            async ct => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); },
            Status(HttpStatusCode.NotFound));

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("no worker available", result.Description);
        handler.Protected().Verify("SendAsync", Times.Exactly(3), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }
}
