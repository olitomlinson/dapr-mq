using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Moq.Protected;

namespace DaprMQ.Tests;

/// <summary>
/// Where an API request's delivery budget comes from: the REST daprmq-timeout header or the gRPC
/// deadline, capped by DELIVERY_RETRY_MAX_SECONDS (proposals/readiness-and-retries.md, section 3).
/// </summary>
public class DeliveryBudgetSourceTests
{
    private static readonly DeliveryBudgetOptions Options = new() { MaxDuration = TimeSpan.FromSeconds(30) };

    /// <summary>Runs the filter around an action that records the budget it saw.</summary>
    private static async Task<(DeliveryBudget? Seen, IActionResult? Result)> RunFilterAsync(string? timeoutHeader)
    {
        var httpContext = new DefaultHttpContext();
        if (timeoutHeader != null)
        {
            httpContext.Request.Headers["daprmq-timeout"] = timeoutHeader;
        }

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
        DeliveryBudget? seen = null;

        await new DeliveryBudgetFilter(Options).OnActionExecutionAsync(executing, () =>
        {
            seen = DeliveryBudget.Current;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        });

        return (seen, executing.Result);
    }

    [Fact]
    public async Task Rest_TimeoutHeader_SetsTheDeadline()
    {
        var (seen, _) = await RunFilterAsync("5000");

        Assert.NotNull(seen);
        Assert.InRange(seen.Remaining, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("600000")]
    public async Task Rest_NoOrLongerTimeout_IsCappedByTheServer(string? header)
    {
        var (seen, _) = await RunFilterAsync(header);

        Assert.InRange(seen!.Remaining, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task Rest_InvalidTimeout_Returns400_WithoutRunningTheAction(string header)
    {
        var (seen, result) = await RunFilterAsync(header);

        Assert.Null(seen);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("daprmq-timeout", Assert.IsType<ApiErrorResponse>(badRequest.Value).Message);
    }

    [Fact]
    public async Task Rest_BudgetEndsWithTheRequest()
    {
        await RunFilterAsync("5000");

        Assert.Null(DeliveryBudget.Current);
    }

    private static ServerCallContext GrpcContext(DateTime deadline, CancellationToken cancelled = default)
    {
        var context = new Mock<ServerCallContext>();
        context.Protected().Setup<DateTime>("DeadlineCore").Returns(deadline);
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(cancelled);
        return context.Object;
    }

    [Fact]
    public async Task Grpc_Deadline_SetsTheBudget()
    {
        DeliveryBudget? seen = null;

        await new DeliveryBudgetInterceptor(Options).UnaryServerHandler<string, string>(
            "req", GrpcContext(DateTime.UtcNow.AddSeconds(5)), (_, _) => { seen = DeliveryBudget.Current; return Task.FromResult("ok"); });

        Assert.InRange(seen!.Remaining, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
        Assert.Null(DeliveryBudget.Current);
    }

    [Fact]
    public async Task Grpc_NoDeadline_IsCappedByTheServer()
    {
        DeliveryBudget? seen = null;

        await new DeliveryBudgetInterceptor(Options).UnaryServerHandler<string, string>(
            "req", GrpcContext(DateTime.MaxValue), (_, _) => { seen = DeliveryBudget.Current; return Task.FromResult("ok"); });

        Assert.InRange(seen!.Remaining, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
    }
}
