using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class CatalogShardsTests
{
    [Theory]
    // FNV-1a 32-bit reference values: "" = 0x811c9dc5, "a" = 0xe40c292c, "foobar" = 0xbf9cf968.
    [InlineData("", 0x811c9dc5u % CatalogShards.Count)]
    [InlineData("a", 0xe40c292cu % CatalogShards.Count)]
    [InlineData("foobar", 0xbf9cf968u % CatalogShards.Count)]
    public void ShardFor_IsFnv1aModShardCount(string id, uint expected)
    {
        Assert.Equal((int)expected, CatalogShards.ShardFor(id));
    }

    [Fact]
    public void ShardFor_SpreadsIdsAcrossShards()
    {
        var shards = Enumerable.Range(0, 1000).Select(i => CatalogShards.ShardFor($"queue-{i}")).ToList();

        Assert.All(shards, s => Assert.InRange(s, 0, CatalogShards.Count - 1));
        Assert.Equal(CatalogShards.Count, shards.Distinct().Count());
    }

    [Fact]
    public void ActorIdFor_NamesTheShard()
    {
        Assert.Equal("catalog-7", CatalogShards.ActorIdFor(7));
    }
}
