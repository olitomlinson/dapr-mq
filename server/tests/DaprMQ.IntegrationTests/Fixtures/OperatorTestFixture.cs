using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Fixtures;

/// <summary>
/// Dedicated stack for DaprMQ.Operator: the shared API server/worker plus the operator on its own
/// Dapr app-id, so its depth reads have to cross app-ids exactly as they do under Helm.
/// </summary>
public class OperatorTestFixture : IAsyncLifetime
{
    public DaprTestEnvironment Environment { get; private set; } = null!;
    public HttpClient ApiClient => Environment.ApiClient;
    public string OperatorGrpcUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        Environment = new DaprTestEnvironment();
        await Environment.InitializeAsync();
        OperatorGrpcUrl = await Environment.StartOperatorAsync();
    }

    public async Task DisposeAsync()
    {
        if (Environment != null)
        {
            await Environment.DisposeAsync();
        }
    }
}

[CollectionDefinition("Dapr Operator Collection")]
public class OperatorCollection : ICollectionFixture<OperatorTestFixture>
{
}
