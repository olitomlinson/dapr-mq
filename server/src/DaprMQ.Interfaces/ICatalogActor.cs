using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// One shard of the catalog (see <see cref="CatalogShards"/>). Written only by worker-local
/// registrars; the admin console reads shards through the actor-state API and never calls the actor.
/// </summary>
public interface ICatalogActor : IActor
{
    /// <summary>
    /// Adds the entries whose ids the shard doesn't already hold, in one save. Known ids cost no write.
    /// </summary>
    Task<RegisterCatalogBatchResponse> RegisterBatchAsync(RegisterCatalogBatchRequest request);
}
