using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Fixtures;

/// <summary>
/// Dedicated split stack (a gateway in front of one worker) for gateway readiness, whatever
/// DAPRMQ_TEST_TOPOLOGY says. Its tests stop the worker, so it can't share the main collection's stack.
/// </summary>
public class GatewayTestFixture : IAsyncLifetime
{
    public DaprTestEnvironment Environment { get; private set; } = null!;
    public HttpClient GatewayClient => Environment.ApiClient;

    public async Task InitializeAsync()
    {
        Environment = new DaprTestEnvironment();
        await Environment.InitializeAsync(null, null, false, DaprTopology.Split);
    }

    public async Task DisposeAsync()
    {
        if (Environment != null)
        {
            await Environment.DisposeAsync();
        }
    }
}

[CollectionDefinition("Dapr Gateway Collection")]
public class GatewayCollection : ICollectionFixture<GatewayTestFixture>
{
}
