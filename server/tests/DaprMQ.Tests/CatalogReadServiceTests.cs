using DaprMQ.Interfaces;
using Moq;

namespace DaprMQ.Tests;

public class CatalogReadServiceTests
{
    private readonly Mock<IQueueActorStateReader> _reader = new();
    private readonly Dictionary<int, Dictionary<string, CatalogEntry>> _shards = new();

    public CatalogReadServiceTests()
    {
        _reader.Setup(r => r.ReadStateAsync<Dictionary<string, CatalogEntry>>("CatalogActor", It.IsAny<string>(), CatalogActor.EntriesKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string actorId, string _, CancellationToken _) =>
                _shards.TryGetValue(int.Parse(actorId["catalog-".Length..]), out var entries) ? entries : null);
    }

    private void Add(params CatalogEntry[] entries)
    {
        foreach (var entry in entries)
        {
            var shard = CatalogShards.ShardFor(entry.Id);
            if (!_shards.TryGetValue(shard, out var map))
            {
                _shards[shard] = map = new Dictionary<string, CatalogEntry>();
            }
            map[entry.Id] = entry;
        }
    }

    private static CatalogEntry Entry(string id, CatalogEntryKind kind = CatalogEntryKind.Queue) => new() { Id = id, Kind = kind };

    private CatalogReadService CreateService() => new(_reader.Object, "CatalogActor");

    [Fact]
    public async Task ReadsEveryShardAndMergesSortedById()
    {
        Add(Entry("c"), Entry("a"), Entry("b"), Entry("orders"), Entry("emails"));

        var page = await CreateService().ListAsync(null, null, 100);

        Assert.Equal(["a", "b", "c", "emails", "orders"], page.Entries.Select(e => e.Id));
        Assert.Null(page.NextCursor);
        _reader.Verify(r => r.ReadStateAsync<Dictionary<string, CatalogEntry>>("CatalogActor", It.IsAny<string>(), CatalogActor.EntriesKey, It.IsAny<CancellationToken>()),
            Times.Exactly(CatalogShards.Count));
    }

    [Fact]
    public async Task FiltersByKind()
    {
        Add(Entry("q"), Entry("q-deadletter", CatalogEntryKind.DeadLetter), Entry("t", CatalogEntryKind.Topic));

        var page = await CreateService().ListAsync(CatalogEntryKind.DeadLetter, null, 100);

        Assert.Equal(["q-deadletter"], page.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task PagesWithTheLastIdAsCursor()
    {
        Add(Entry("a"), Entry("b"), Entry("c"), Entry("d"), Entry("e"));

        var first = await CreateService().ListAsync(null, null, 2);
        var second = await CreateService().ListAsync(null, first.NextCursor, 2);
        var last = await CreateService().ListAsync(null, second.NextCursor, 2);

        Assert.Equal(["a", "b"], first.Entries.Select(e => e.Id));
        Assert.Equal("b", first.NextCursor);
        Assert.Equal(["c", "d"], second.Entries.Select(e => e.Id));
        Assert.Equal(["e"], last.Entries.Select(e => e.Id));
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task ExactlyFullLastPage_HasNoCursor()
    {
        Add(Entry("a"), Entry("b"));

        var page = await CreateService().ListAsync(null, null, 2);

        Assert.Equal(2, page.Entries.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task TotalsCountEveryKind_RegardlessOfFilterAndCursor()
    {
        Add(Entry("q1"), Entry("q2"), Entry("q1-deadletter", CatalogEntryKind.DeadLetter), Entry("t", CatalogEntryKind.Topic));

        var page = await CreateService().ListAsync(CatalogEntryKind.Topic, "a", 1);

        Assert.Equal(2, page.Totals[CatalogEntryKind.Queue]);
        Assert.Equal(1, page.Totals[CatalogEntryKind.DeadLetter]);
        Assert.Equal(1, page.Totals[CatalogEntryKind.Topic]);
        Assert.Equal(0, page.Totals[CatalogEntryKind.Sink]);
    }
}
