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
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest one delivered actor call may run when the caller set no tighter deadline - a safety limit
    /// for hung workers, not a deadline (DELIVERY_ATTEMPT_MAX_SECONDS).
    /// </summary>
    public TimeSpan AttemptMax { get; init; } = TimeSpan.FromSeconds(100);

    /// <param name="callDeadline">The caller's explicit deadline, if any; honoured in full.</param>
    /// <param name="retryWindow">How long the caller wants undelivered calls retried, if it said; capped by <see cref="RetryMax"/>.</param>
    public DeliveryBudget Budget(DateTimeOffset? callDeadline, TimeSpan? retryWindow, CancellationToken callerCancelled)
    {
        var retry = retryWindow is { } w && w < RetryMax ? w : RetryMax;
        return new DeliveryBudget(DateTimeOffset.UtcNow + retry, callDeadline, AttemptMax, callerCancelled);
    }
}

/// <summary>Headers a caller uses to shape its request's delivery budget.</summary>
public static class DeliveryBudgetHeaders
{
    /// <summary>The caller's deadline for the whole request, in ms: bounds every attempt and every retry.</summary>
    public const string Timeout = "daprmq-timeout";

    /// <summary>How long, in ms, to keep retrying a call that certainly wasn't delivered. Never cuts a delivered call short.</summary>
    public const string RetryTimeout = "daprmq-retry-timeout";

    /// <returns>False when the header is present but not a positive number of milliseconds.</returns>
    public static bool TryParse(string? raw, out TimeSpan? value)
    {
        value = null;
        if (raw == null)
        {
            return true;
        }

        if (!int.TryParse(raw, out var ms) || ms <= 0)
        {
            return false;
        }

        value = TimeSpan.FromMilliseconds(ms);
        return true;
    }
}

/// <summary>REST: each API action runs inside a delivery budget, from the optional daprmq-timeout / daprmq-retry-timeout headers.</summary>
public sealed class DeliveryBudgetFilter(DeliveryBudgetOptions options) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;
        foreach (var name in new[] { DeliveryBudgetHeaders.Timeout, DeliveryBudgetHeaders.RetryTimeout })
        {
            if (!DeliveryBudgetHeaders.TryParse(headers.TryGetValue(name, out var raw) ? raw.ToString() : null, out _))
            {
                context.Result = new BadRequestObjectResult(new ApiErrorResponse($"{name} must be a positive number of milliseconds"));
                return;
            }
        }

        DeliveryBudgetHeaders.TryParse(headers.TryGetValue(DeliveryBudgetHeaders.Timeout, out var t) ? t.ToString() : null, out var timeout);
        DeliveryBudgetHeaders.TryParse(headers.TryGetValue(DeliveryBudgetHeaders.RetryTimeout, out var r) ? r.ToString() : null, out var retry);

        using (DeliveryBudget.Begin(options.Budget(DateTimeOffset.UtcNow + timeout, retry, context.HttpContext.RequestAborted)))
        {
            await next();
        }
    }
}

/// <summary>gRPC: each unary call runs inside a delivery budget, from its deadline and daprmq-retry-timeout metadata.</summary>
public sealed class DeliveryBudgetInterceptor(DeliveryBudgetOptions options) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        DateTimeOffset? callDeadline = context.Deadline == DateTime.MaxValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(context.Deadline, DateTimeKind.Utc));
        DeliveryBudgetHeaders.TryParse(context.RequestHeaders?.GetValue(DeliveryBudgetHeaders.RetryTimeout), out var retry);

        using (DeliveryBudget.Begin(options.Budget(callDeadline, retry, context.CancellationToken)))
        {
            return await continuation(request, context);
        }
    }
}
