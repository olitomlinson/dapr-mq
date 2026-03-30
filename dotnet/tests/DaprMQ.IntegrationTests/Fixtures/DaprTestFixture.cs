using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection fixture for sharing Dapr test environment across test classes
/// This ensures containers are started once and reused across all integration tests
/// </summary>
public class DaprTestFixture : IAsyncLifetime
{
    public DaprTestEnvironment Environment { get; private set; } = null!;
    public HttpClient ApiClient => Environment.ApiClient;
    public HttpClient DaprSidecarClient => Environment.DaprSidecarClient;
    public QueueActorHttpClient QueueActorHttpClient { get; private set; } = null!;

    /// <summary>
    /// gRPC endpoint URL (separate port for HTTP/2)
    /// </summary>
    public string GrpcUrl => Environment.ApiServerGrpcUrl;

    /// <summary>
    /// WireMock server URL for HTTP sink testing (test host access)
    /// </summary>
    public string WireMockUrl => Environment.WireMockUrl;

    /// <summary>
    /// WireMock server URL for HTTP sink testing (Docker network internal access)
    /// </summary>
    public string WireMockInternalUrl => Environment.WireMockInternalUrl;

    /// <summary>
    /// Queue ID for tests - read from environment variable or generated as random GUID
    /// </summary>
    public string QueueId { get; private set; }

    public DaprTestFixture()
    {
        // Read queue ID from environment variable, fallback to random GUID
        var envQueueId = System.Environment.GetEnvironmentVariable("DAPRMQ_TEST_QUEUE_ID");
        QueueId = !string.IsNullOrEmpty(envQueueId)
            ? envQueueId
            : $"test-queue-{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        Environment = new DaprTestEnvironment();
        await Environment.InitializeAsync();

        var queueActorTypeName = System.Environment.GetEnvironmentVariable("QUEUE_ACTOR_TYPE_NAME") ?? "QueueActor";
        QueueActorHttpClient = new QueueActorHttpClient(Environment.DaprSidecarClient, queueActorTypeName);
    }

    public async Task DisposeAsync()
    {
        if (Environment != null)
        {
            await Environment.DisposeAsync();
        }
    }
}

/// <summary>
/// Collection definition for xUnit
/// All test classes decorated with [Collection("Dapr Collection")] will share this fixture
/// </summary>
[CollectionDefinition("Dapr Collection")]
public class DaprCollection : ICollectionFixture<DaprTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}
