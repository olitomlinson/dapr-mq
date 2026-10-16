using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DaprMQ.ApiServer.Endpoints;

/// <summary>
/// Internal, worker-only endpoints for the scaling registry, called by DaprMQ.Operator via Dapr
/// service invocation. The scaler reports the ScaledObjects KEDA asks about with POST
/// /internal/scaling/registrations, which writes through ScalingRegistryActor; the console reads
/// them with GET /internal/admin/scaling from committed state, which never activates the actor.
/// </summary>
public static class ScalingRegistryEndpoints
{
    public const string RegisterRoute = "/internal/scaling/registrations";
    public const string ListRoute = "/internal/admin/scaling";
    public const int MaxRegistrations = 1000;

    public static IEndpointRouteBuilder MapScalingRegistryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(RegisterRoute, (RegisterScaledObjectsRequest request, IScalingRegistryActorInvoker invoker, CancellationToken ct) =>
            HandleRegisterAsync(request, invoker, ct));
        endpoints.MapGet(ListRoute, (IScalingRegistryReadService service, CancellationToken ct) => HandleListAsync(service, ct));
        return endpoints;
    }

    public static async Task<Results<NoContent, BadRequest<string>, ProblemHttpResult>> HandleRegisterAsync(
        RegisterScaledObjectsRequest request, IScalingRegistryActorInvoker invoker, CancellationToken cancellationToken)
    {
        if (request.Registrations is not { Count: > 0 } || request.Registrations.Count > MaxRegistrations)
        {
            return TypedResults.BadRequest($"Registrations must hold 1 to {MaxRegistrations} entries");
        }

        if (request.Registrations.Any(r => string.IsNullOrWhiteSpace(r.Namespace) || string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.QueueId)))
        {
            return TypedResults.BadRequest("Namespace, Name and QueueId are required");
        }

        try
        {
            await invoker.InvokeMethodAsync(new ActorId(ScalingRegistry.ActorId), ActorMethodNames.RegisterScaledObjects, request, cancellationToken);
            return TypedResults.NoContent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The scaler keeps them due and retries on its next flush.
            return TypedResults.Problem(title: "Scaling registry unavailable", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    public static async Task<Ok<ScaledObjectRegistrations>> HandleListAsync(IScalingRegistryReadService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.ListAsync(cancellationToken));
}
