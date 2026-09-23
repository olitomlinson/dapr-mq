using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Fixtures;

/// <summary>
/// Dedicated fixture (separate container stack from the shared "Dapr Collection") for tests that
/// need a session actor to actually go cold within test time, rather than the production 60s
/// ActorIdleTimeout - see SessionReentrancyTests.
/// </summary>
public class ReentrancyTestFixture : IAsyncLifetime
{
    public DaprTestEnvironment Environment { get; private set; } = null!;
    public HttpClient ApiClient => Environment.ApiClient;

    public string QueueId { get; } = $"test-queue-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        Environment = new DaprTestEnvironment();
        await Environment.InitializeAsync(new Dictionary<string, string>
        {
            ["ACTOR_IDLE_TIMEOUT_SECONDS"] = "3",
            ["ACTOR_SCAN_INTERVAL_SECONDS"] = "1"
        });
    }

    public async Task DisposeAsync()
    {
        if (Environment != null)
        {
            await Environment.DisposeAsync();
        }
    }
}

[CollectionDefinition("Dapr Reentrancy Collection")]
public class ReentrancyCollection : ICollectionFixture<ReentrancyTestFixture>
{
}
