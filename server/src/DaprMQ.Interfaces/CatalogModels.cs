using System.Text;
using System.Text.Json.Serialization;

namespace DaprMQ.Interfaces;

/// <summary>
/// What a catalog entry names. Subscriptions aren't catalogued - they come from each topic's
/// SubscriberIds - and per-session queue actors aren't either - they come from the
/// SessionCoordinatorActor directory.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CatalogEntryKind>))]
public enum CatalogEntryKind
{
    Queue,
    DeadLetter,
    Topic,
    Sink
}

/// <summary>
/// One entity in the catalog, the registry the admin console lists queues, DLQs, topics and sinks
/// from (actor state has no key enumeration). ParentId is the queue a DLQ or sink belongs to.
/// </summary>
public record CatalogEntry
{
    public required string Id { get; init; }
    public CatalogEntryKind Kind { get; init; }
    public string? ParentId { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>
    /// The entry a QueueActor registers for itself: null for a per-session actor (or its DLQ),
    /// DeadLetter with its parent queue for an id ending "-deadletter", otherwise Queue.
    /// </summary>
    public static CatalogEntry? ForQueueActorId(string actorId, DateTimeOffset now)
    {
        if (actorId.Contains(SessionActorIdMarker, StringComparison.Ordinal))
        {
            return null;
        }

        if (actorId.EndsWith(DeadLetterActorIdSuffix, StringComparison.Ordinal) && actorId.Length > DeadLetterActorIdSuffix.Length)
        {
            return new CatalogEntry
            {
                Id = actorId,
                Kind = CatalogEntryKind.DeadLetter,
                ParentId = actorId[..^DeadLetterActorIdSuffix.Length],
                RegisteredAt = now
            };
        }

        return new CatalogEntry { Id = actorId, Kind = CatalogEntryKind.Queue, RegisteredAt = now };
    }

    private const string SessionActorIdMarker = "-session-";
    private const string DeadLetterActorIdSuffix = "-deadletter";
}

/// <summary>Request for CatalogActor.RegisterBatchAsync.</summary>
public record RegisterCatalogBatchRequest
{
    public List<CatalogEntry> Entries { get; init; } = [];
}

/// <summary>Response from CatalogActor.RegisterBatchAsync: how many ids were new to the shard.</summary>
public record RegisterCatalogBatchResponse
{
    public int Added { get; init; }
}

/// <summary>
/// One page of the worker's catalog read (GET /internal/admin/catalog), sorted by Id. NextCursor is
/// null on the last page.
/// </summary>
public record CatalogPage
{
    public List<CatalogEntry> Entries { get; init; } = [];
    public string? NextCursor { get; init; }

    /// <summary>Entries of each kind in the whole catalog, ignoring the page's kind filter and cursor.</summary>
    public Dictionary<CatalogEntryKind, int> Totals { get; init; } = [];
}

/// <summary>
/// The catalog is split across a fixed set of CatalogActor shards, "catalog-{n}", picked by a stable
/// FNV-1a hash of the entity id. string.GetHashCode is randomised per process, so it can't be used.
/// </summary>
public static class CatalogShards
{
    public const int Count = 64;

    public static int ShardFor(string id)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(id))
        {
            hash = unchecked((hash ^ b) * prime);
        }

        return (int)(hash % Count);
    }

    public static string ActorIdFor(int shard) => $"catalog-{shard}";
}
