using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DaprMQ.ApiServer.Endpoints;

/// <summary>
/// Internal, worker-only queue-depth read (POST /internal/queue-depth). Called by DaprMQ.Operator's
/// KEDA external scaler via Dapr service invocation - the operator can't read actor state itself,
/// because Dapr keys actor state by the caller's app-id and only workers share QueueActor's.
/// Mapped whenever actors are registered, independent of ENABLE_API (workers run with the public
/// API off).
/// </summary>
public static class QueueDepthEndpoint
{
    public const string Route = "/internal/queue-depth";
    public const int MaxQueries = 100;

    public static IEndpointRouteBuilder MapQueueDepthEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, (QueueDepthRequest request, IQueueDepthService service, CancellationToken ct) =>
            HandleAsync(request, service, ct));
        return endpoints;
    }

    public static async Task<Results<Ok<QueueDepthResponse>, BadRequest<string>>> HandleAsync(
        QueueDepthRequest request, IQueueDepthService service, CancellationToken cancellationToken)
    {
        if (request.Queries is not { Count: > 0 })
        {
            return TypedResults.BadRequest("Queries must not be empty");
        }

        if (request.Queries.Count > MaxQueries)
        {
            return TypedResults.BadRequest($"At most {MaxQueries} queries per request");
        }

        if (request.Queries.Any(q => string.IsNullOrWhiteSpace(q.QueueId)))
        {
            return TypedResults.BadRequest("QueueId is required");
        }

        // A failed read is reported per query rather than failing the batch, so one bad queue
        // doesn't blind the scaler to every other queue in the request.
        var results = await Task.WhenAll(request.Queries.Select(async query =>
        {
            try
            {
                return await service.GetDepthAsync(query, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new QueueDepthResult { QueueId = query.QueueId, Error = ex.Message };
            }
        }));

        return TypedResults.Ok(new QueueDepthResponse { Results = results.ToList() });
    }
}
