using Dapr.Actors.Runtime;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// One shard of the catalog: the registry of queues, DLQs, topics and sinks the admin console lists
/// (actor state has no key enumeration). Shard "catalog-{n}" holds the ids that
/// <see cref="CatalogShards.ShardFor"/> maps to n, in a single "entries" key. It isn't segmented:
/// nothing consumes from a head, the shard count already bounds the blob (~1.6k entries at 100k
/// entities), and it's only rewritten when a shard sees an id for the first time.
/// Written only by worker-local CatalogRegistrars; reads go through the actor-state API.
/// </summary>
public class CatalogActor : Actor, ICatalogActor
{
    public const string EntriesKey = "entries";

    private Dictionary<string, CatalogEntry> _entries = new();

    public CatalogActor(ActorHost host) : base(host)
    {
    }

    protected override async Task OnActivateAsync()
    {
        var entries = await StateManager.TryGetStateAsync<Dictionary<string, CatalogEntry>>(EntriesKey);
        _entries = entries.HasValue ? entries.Value : new Dictionary<string, CatalogEntry>();
    }

    /// <inheritdoc />
    public async Task<RegisterCatalogBatchResponse> RegisterBatchAsync(RegisterCatalogBatchRequest request)
    {
        if (request.Entries.All(entry => _entries.ContainsKey(entry.Id)))
        {
            return new RegisterCatalogBatchResponse();
        }

        // Add to a copy and swap it in only after the save, so a failed save leaves the in-memory set
        // matching what's stored and the ids are written on the next attempt.
        var next = new Dictionary<string, CatalogEntry>(_entries);
        var added = request.Entries.Count(entry => next.TryAdd(entry.Id, entry));
        await StateManager.SetStateAsync(EntriesKey, next);
        await StateManager.SaveStateAsync();
        _entries = next;

        return new RegisterCatalogBatchResponse { Added = added };
    }
}
