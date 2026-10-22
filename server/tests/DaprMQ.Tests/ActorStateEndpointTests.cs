using System.Text.Json.Nodes;
using DaprMQ.ApiServer.Endpoints;
using DaprMQ.ApiServer.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace DaprMQ.Tests;

public class ActorStateEndpointTests
{
    private readonly Mock<IQueueActorStateReader> _reader = new();
    private readonly ActorHosting _hosting = new(ActorSet.Control, ["QueueActor"], ["SessionCoordinatorActor"]);

    [Fact]
    public async Task HostedType_ReturnsTheStoredValue()
    {
        var stored = JsonNode.Parse("""{"totalCount":3,"activeCount":1}""");
        _reader.Setup(r => r.ReadStateAsync<JsonNode>("SessionCoordinatorActor", "orders", "dir_summary", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var result = await ActorStateEndpoint.HandleAsync("SessionCoordinatorActor", "orders", "dir_summary", _hosting, _reader.Object, CancellationToken.None);

        var ok = Assert.IsType<Ok<JsonNode>>(result.Result);
        Assert.Equal(3, ok.Value!["totalCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task MissingKey_IsNoContent()
    {
        _reader.Setup(r => r.ReadStateAsync<JsonNode>("SessionCoordinatorActor", "orders", "metadata", It.IsAny<CancellationToken>()))
            .ReturnsAsync((JsonNode?)null);

        var result = await ActorStateEndpoint.HandleAsync("SessionCoordinatorActor", "orders", "metadata", _hosting, _reader.Object, CancellationToken.None);

        Assert.IsType<NoContent>(result.Result);
    }

    [Fact]
    public async Task TypeNotHostedHere_IsNotFoundWithoutReading()
    {
        // Only the app that hosts a type can read its state, and forwarding would let two apps with
        // mismatched config bounce a read between them.
        var result = await ActorStateEndpoint.HandleAsync("QueueActor", "orders", "metadata", _hosting, _reader.Object, CancellationToken.None);

        Assert.IsType<NotFound<string>>(result.Result);
        _reader.VerifyNoOtherCalls();
    }
}
