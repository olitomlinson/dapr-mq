using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DaprMQ.ApiServer.Endpoints;

/// <summary>
/// Internal, worker-only, read-only admin reads (GET /internal/admin/*) behind the admin console.
/// Called by DaprMQ.Operator via Dapr service invocation, for the same reason as
/// <see cref="QueueDepthEndpoint"/>: only workers can read actor state. Reads go through the
/// actor-state API and never call or activate an actor.
/// </summary>
public static class AdminReadEndpoints
{
    public const string CatalogRoute = "/internal/admin/catalog";
    public const int DefaultCatalogLimit = 100;
    public const int MaxCatalogLimit = 1000;

    public static IEndpointRouteBuilder MapAdminReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(CatalogRoute, (string? kind, string? cursor, int? limit, ICatalogReadService service, CancellationToken ct) =>
            HandleCatalogAsync(kind, cursor, limit, service, ct));
        return endpoints;
    }

    public static async Task<Results<Ok<CatalogPage>, BadRequest<string>>> HandleCatalogAsync(
        string? kind, string? cursor, int? limit, ICatalogReadService service, CancellationToken cancellationToken)
    {
        CatalogEntryKind? parsedKind = null;
        if (!string.IsNullOrEmpty(kind))
        {
            // Names only - Enum.TryParse would also accept "1".
            var name = Enum.GetNames<CatalogEntryKind>().FirstOrDefault(n => n.Equals(kind, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return TypedResults.BadRequest($"kind must be one of: {string.Join(", ", Enum.GetNames<CatalogEntryKind>())}");
            }
            parsedKind = Enum.Parse<CatalogEntryKind>(name);
        }

        var pageSize = limit ?? DefaultCatalogLimit;
        if (pageSize is < 1 or > MaxCatalogLimit)
        {
            return TypedResults.BadRequest($"limit must be between 1 and {MaxCatalogLimit}");
        }

        return TypedResults.Ok(await service.ListAsync(parsedKind, cursor, pageSize, cancellationToken));
    }
}
