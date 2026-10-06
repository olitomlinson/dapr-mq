using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DaprMQ.ApiServer.Services;

public sealed class DeliveryBudgetOptions
{
    /// <summary>Longest an API request may keep retrying undelivered actor calls (DELIVERY_RETRY_MAX_SECONDS).</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(30);

    public DeliveryBudget Budget(DateTimeOffset callerDeadline, CancellationToken callerCancelled)
    {
        var cap = DateTimeOffset.UtcNow + MaxDuration;
        return new DeliveryBudget(callerDeadline < cap ? callerDeadline : cap, callerCancelled);
    }
}

/// <summary>
/// REST: each API action runs inside a delivery budget, from the optional daprmq-timeout header
/// (milliseconds, since HTTP has no standard deadline) capped by the server.
/// </summary>
public sealed class DeliveryBudgetFilter(DeliveryBudgetOptions options) : IAsyncActionFilter
{
    public const string TimeoutHeader = "daprmq-timeout";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var deadline = DateTimeOffset.MaxValue;
        if (context.HttpContext.Request.Headers.TryGetValue(TimeoutHeader, out var raw))
        {
            if (!int.TryParse(raw, out var ms) || ms <= 0)
            {
                context.Result = new BadRequestObjectResult(new ApiErrorResponse($"{TimeoutHeader} must be a positive number of milliseconds"));
                return;
            }

            deadline = DateTimeOffset.UtcNow.AddMilliseconds(ms);
        }

        using (DeliveryBudget.Begin(options.Budget(deadline, context.HttpContext.RequestAborted)))
        {
            await next();
        }
    }
}

/// <summary>gRPC: each unary call runs inside a delivery budget from its deadline, capped by the server.</summary>
public sealed class DeliveryBudgetInterceptor(DeliveryBudgetOptions options) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var deadline = context.Deadline == DateTime.MaxValue
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(DateTime.SpecifyKind(context.Deadline, DateTimeKind.Utc));

        using (DeliveryBudget.Begin(options.Budget(deadline, context.CancellationToken)))
        {
            return await continuation(request, context);
        }
    }
}
