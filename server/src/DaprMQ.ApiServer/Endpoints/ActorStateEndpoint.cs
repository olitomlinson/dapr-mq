using System.Text.Json.Nodes;
using DaprMQ.ApiServer.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DaprMQ.ApiServer.Endpoints;

/// <summary>
/// Internal read of one actor state key (GET /internal/actor-state/{type}/{id}/{key}), for a host of
/// another app-id that can't read this app's actor state itself - Dapr keys it by app-id. Called by
/// QueueActorStateReader via Dapr service invocation when the data and control planes are split
/// (see ActorHosting). Serves only types hosted here, so a read is never forwarded on.
/// </summary>
public static class ActorStateEndpoint
{
    public const string Route = "/internal/actor-state/{actorType}/{actorId}/{key}";

    public static IEndpointRouteBuilder MapActorStateEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, (string actorType, string actorId, string key, ActorHosting hosting, IQueueActorStateReader reader,
            CancellationToken ct) => HandleAsync(actorType, actorId, key, hosting, reader, ct));
        return endpoints;
    }

    public static async Task<Results<Ok<JsonNode>, NoContent, NotFound<string>>> HandleAsync(
        string actorType, string actorId, string key, ActorHosting hosting, IQueueActorStateReader reader, CancellationToken cancellationToken)
    {
        if (!hosting.Hosts(actorType))
        {
            return TypedResults.NotFound($"Actor type '{actorType}' is not hosted by this app");
        }

        var value = await reader.ReadStateAsync<JsonNode>(actorType, actorId, key, cancellationToken);
        return value == null ? TypedResults.NoContent() : TypedResults.Ok(value);
    }
}
