using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests.Infrastructure;

/// <summary>
/// One shared Testcontainers stack (Postgres + placement + scheduler + API server + daprd) for
/// every integration test class in this assembly.
///
/// xUnit resolves a [CollectionDefinition] only within the assembly that declares it, so the
/// "Dapr Collection" definition living in DaprMQ.IntegrationTests's own assembly is invisible from
/// here - but the fixture *type* it wraps crosses the assembly boundary fine (via the
/// ProjectReference). Re-declaring the definition locally is what lets all of this assembly's test
/// classes share a single DaprTestEnvironment instead of paying container startup per class, which
/// IClassFixture would have forced.
/// </summary>
[CollectionDefinition(Name)]
public class DaprMQClientCollection : ICollectionFixture<DaprTestFixture>
{
    public const string Name = "DaprMQ Client Integration";
}
