using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class CatalogRegistrarTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    private readonly Mock<ICatalogActorInvoker> _invoker = new();
    private readonly List<(string Shard, List<string> Ids)> _calls = [];
    private readonly CatalogRegistrar _registrar;

    public CatalogRegistrarTests()
    {
        _invoker.Setup(i => i.InvokeMethodAsync<RegisterCatalogBatchRequest, RegisterCatalogBatchResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.RegisterCatalogBatch, It.IsAny<RegisterCatalogBatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ActorId id, string _, RegisterCatalogBatchRequest request, CancellationToken _) =>
            {
                lock (_calls)
                {
                    _calls.Add((id.GetId(), request.Entries.Select(e => e.Id).ToList()));
                }
            })
            .ReturnsAsync(new RegisterCatalogBatchResponse());
        _registrar = new CatalogRegistrar(_invoker.Object, _time, NullLogger<CatalogRegistrar>.Instance);
    }

    private static CatalogEntry Entry(string id) => new() { Id = id, Kind = CatalogEntryKind.Queue };

    // Two ids that hash to the same shard, and one that doesn't.
    private static (string A, string B, string Other) IdsSharingAShard()
    {
        var ids = Enumerable.Range(0, 1000).Select(i => $"q-{i}").ToList();
        var a = ids[0];
        var b = ids.Skip(1).First(id => CatalogShards.ShardFor(id) == CatalogShards.ShardFor(a));
        var other = ids.First(id => CatalogShards.ShardFor(id) != CatalogShards.ShardFor(a));
        return (a, b, other);
    }

    [Fact]
    public async Task Flush_GroupsEntriesByShard_OneCallPerShard()
    {
        var (a, b, other) = IdsSharingAShard();
        _registrar.Register(Entry(a));
        _registrar.Register(Entry(b));
        _registrar.Register(Entry(other));

        await _registrar.FlushAsync(CancellationToken.None);

        Assert.Equal(2, _calls.Count);
        var shared = _calls.Single(c => c.Shard == CatalogShards.ActorIdFor(CatalogShards.ShardFor(a)));
        Assert.Equal([a, b], shared.Ids);
        Assert.Contains(_calls, c => c.Shard == CatalogShards.ActorIdFor(CatalogShards.ShardFor(other)) && c.Ids.SequenceEqual([other]));
    }

    [Fact]
    public async Task Register_RepeatedId_SentOnce()
    {
        _registrar.Register(Entry("orders"));
        await _registrar.FlushAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));

        _registrar.Register(Entry("orders"));
        await _registrar.FlushAsync(CancellationToken.None);

        Assert.Single(_calls);
    }

    [Fact]
    public async Task Flush_AtMostOneCallPerShardPerSecond()
    {
        var (a, b, _) = IdsSharingAShard();
        _registrar.Register(Entry(a));
        await _registrar.FlushAsync(CancellationToken.None);

        _registrar.Register(Entry(b));
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await _registrar.FlushAsync(CancellationToken.None);
        Assert.Single(_calls);

        _time.Advance(TimeSpan.FromMilliseconds(500));
        await _registrar.FlushAsync(CancellationToken.None);
        Assert.Equal(2, _calls.Count);
        Assert.Equal([b], _calls[1].Ids);
    }

    [Fact]
    public async Task Flush_FailedSend_LetsTheIdRegisterAgain()
    {
        _invoker.Setup(i => i.InvokeMethodAsync<RegisterCatalogBatchRequest, RegisterCatalogBatchResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.RegisterCatalogBatch, It.IsAny<RegisterCatalogBatchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sidecar down"));
        _registrar.Register(Entry("orders"));
        await _registrar.FlushAsync(CancellationToken.None);

        var calls = 0;
        _invoker.Setup(i => i.InvokeMethodAsync<RegisterCatalogBatchRequest, RegisterCatalogBatchResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.RegisterCatalogBatch, It.IsAny<RegisterCatalogBatchRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls++)
            .ReturnsAsync(new RegisterCatalogBatchResponse());
        _time.Advance(TimeSpan.FromSeconds(1));
        _registrar.Register(Entry("orders"));
        await _registrar.FlushAsync(CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Flush_NothingRegistered_NoCalls()
    {
        await _registrar.FlushAsync(CancellationToken.None);

        Assert.Empty(_calls);
    }
}
