using System.Net;
using Grpc.Health.V1;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class HealthTests(DaprTestFixture fixture)
{
    [Theory]
    [InlineData("")]
    [InlineData("daprmq.DaprMQ")]
    [InlineData("daprmq.DaprMQ.operations")]
    public async Task GrpcHealthCheck_RunningStack_ReturnsServing(string service)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        using var channel = GrpcChannel.ForAddress(fixture.GrpcUrl);
        var client = new Health.HealthClient(channel);

        var response = await client.CheckAsync(new HealthCheckRequest { Service = service });

        Assert.Equal(HealthCheckResponse.Types.ServingStatus.Serving, response.Status);
    }

    [Fact]
    public async Task HealthOperations_RunningStack_Returns200()
    {
        var response = await fixture.ApiClient.GetAsync("/health/operations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_RunningStack_Returns200()
    {
        var response = await fixture.ApiClient.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
