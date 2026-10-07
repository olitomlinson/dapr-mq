using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests.Infrastructure;

/// <summary>
/// A split stack (a gateway in front of one worker) whose tests stop and restart the worker, so it
/// can't share the main collection's stack. Re-declared here for the same reason as
/// <see cref="DaprMQClientCollection"/>.
/// </summary>
[CollectionDefinition(Name)]
public class SplitStackCollection : ICollectionFixture<SplitTopologyFixture>
{
    public const string Name = "DaprMQ Client Split Stack";
}
