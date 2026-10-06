using System.Net;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// A gateway's readiness covers only what it controls (its sidecar and placement), never the
/// workers: otherwise a full worker outage would take every gateway out of the Service at once
/// (proposals/readiness-and-retries.md, section 1).
/// </summary>
[Collection("Dapr Split Topology Collection")]
public class GatewayReadinessTests(SplitTopologyFixture fixture)
{
    [Fact]
    public async Task HealthReady_StaysReadyWhileEveryWorkerIsDown()
    {
        Assert.Equal(HttpStatusCode.OK, (await fixture.GatewayClient.GetAsync("/health/ready")).StatusCode);

        await fixture.Environment.StopWorkersAsync();
        try
        {
            // Long enough for placement to drop the worker (keepalive ~5 s) and the
            // health-check publisher to run again.
            await Task.Delay(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.OK, (await fixture.GatewayClient.GetAsync("/health/ready")).StatusCode);
        }
        finally
        {
            await fixture.Environment.StartWorkersAsync();
        }
    }
}
