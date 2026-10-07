using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Fixtures;

/// <summary>
/// Dedicated split stack (a gateway in front of one worker), whatever DAPRMQ_TEST_TOPOLOGY says.
/// Its tests stop and restart the worker, so it can't share the main collection's stack.
/// </summary>
public class SplitTopologyFixture : IAsyncLifetime
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

[CollectionDefinition("Dapr Split Topology Collection")]
public class SplitTopologyCollection : ICollectionFixture<SplitTopologyFixture>
{
}
