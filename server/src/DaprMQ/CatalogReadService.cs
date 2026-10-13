using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// Lists the catalog behind the worker's GET /internal/admin/catalog.
/// </summary>
public interface ICatalogReadService
{
    /// <summary>
    /// One page of entries sorted by Id (ordinal), after <paramref name="cursor"/> (the last Id of the
    /// previous page), optionally filtered to one kind.
    /// </summary>
    Task<CatalogPage> ListAsync(CatalogEntryKind? kind, string? cursor, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads every CatalogActor shard's entries key in parallel through the actor-state API - never
/// calling or activating the shards - then merges, filters and keyset-pages them. Each read is a
/// snapshot of committed state.
/// </summary>
public class CatalogReadService : ICatalogReadService
{
    private readonly IQueueActorStateReader _reader;
    private readonly string _catalogActorType;

    public CatalogReadService(IQueueActorStateReader reader, string catalogActorType)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _catalogActorType = catalogActorType ?? throw new ArgumentNullException(nameof(catalogActorType));
    }

    /// <inheritdoc />
    public async Task<CatalogPage> ListAsync(CatalogEntryKind? kind, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        var shards = await Task.WhenAll(Enumerable.Range(0, CatalogShards.Count).Select(shard =>
            _reader.ReadStateAsync<Dictionary<string, CatalogEntry>>(
                _catalogActorType, CatalogShards.ActorIdFor(shard), CatalogActor.EntriesKey, cancellationToken)));

        var all = shards.Where(entries => entries != null).SelectMany(entries => entries!.Values).ToList();
        var totals = Enum.GetValues<CatalogEntryKind>().ToDictionary(k => k, k => all.Count(e => e.Kind == k));

        var matching = all
            .Where(e => kind == null || e.Kind == kind)
            .Where(e => cursor == null || string.CompareOrdinal(e.Id, cursor) > 0)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();

        var hasMore = matching.Count > limit;
        if (hasMore)
        {
            matching.RemoveAt(limit);
        }

        return new CatalogPage { Entries = matching, NextCursor = hasMore ? matching[^1].Id : null, Totals = totals };
    }
}
