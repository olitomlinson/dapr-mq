using System.Net;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// A gateway hosts no actors, so it is ready only while a worker that registered QueueActor in
/// placement answers its own /health/ready.
/// </summary>
[Collection("Dapr Gateway Collection")]
public class GatewayReadinessTests(GatewayTestFixture fixture)
{
    private async Task<HttpStatusCode> WaitForStatusAsync(HttpStatusCode expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpStatusCode status;
        do
        {
            status = (await fixture.GatewayClient.GetAsync("/health/ready")).StatusCode;
            if (status == expected)
            {
                break;
            }

            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);

        return status;
    }

    [Fact]
    public async Task HealthReady_TracksWorkerAvailability()
    {
        Assert.Equal(HttpStatusCode.OK, await WaitForStatusAsync(HttpStatusCode.OK, TimeSpan.FromSeconds(30)));

        await fixture.Environment.StopWorkersAsync();
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                await WaitForStatusAsync(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(20)));
        }
        finally
        {
            await fixture.Environment.StartWorkersAsync();
        }

        Assert.Equal(HttpStatusCode.OK, await WaitForStatusAsync(HttpStatusCode.OK, TimeSpan.FromSeconds(60)));
    }
}
