using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class CatalogEntryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void ForQueueActorId_PlainQueue()
    {
        var entry = CatalogEntry.ForQueueActorId("orders", Now);

        Assert.Equal(new CatalogEntry { Id = "orders", Kind = CatalogEntryKind.Queue, RegisteredAt = Now }, entry);
    }

    [Fact]
    public void ForQueueActorId_DeadLetter_HasParent()
    {
        var entry = CatalogEntry.ForQueueActorId("orders-deadletter", Now);

        Assert.Equal(CatalogEntryKind.DeadLetter, entry!.Kind);
        Assert.Equal("orders", entry.ParentId);
    }

    [Theory]
    [InlineData("orders-session-42")]
    [InlineData("orders-session-42-deadletter")]
    public void ForQueueActorId_SessionActors_AreSkipped(string id)
    {
        Assert.Null(CatalogEntry.ForQueueActorId(id, Now));
    }
}
