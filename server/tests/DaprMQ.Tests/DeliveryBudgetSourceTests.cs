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
    private static readonly DeliveryBudgetOptions Options = new() { RetryMax = TimeSpan.FromSeconds(30), AttemptMax = TimeSpan.FromSeconds(100) };

    /// <summary>Runs the filter around an action that records the budget it saw.</summary>
    private static async Task<(DeliveryBudget? Seen, IActionResult? Result)> RunFilterAsync(params (string Name, string Value)[] headers)
    {
        var httpContext = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            httpContext.Request.Headers[name] = value;
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

    private static TimeSpan Until(DateTimeOffset at) => at - DateTimeOffset.UtcNow;

    [Fact]
    public async Task Rest_NoHeaders_RetriesForTheServerWindow_AndSetsNoCallDeadline()
    {
        var (seen, _) = await RunFilterAsync();

        Assert.InRange(Until(seen!.RetryDeadline), TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
        Assert.Null(seen.CallDeadline);
        Assert.Equal(TimeSpan.FromSeconds(100), seen.MaxAttempt);
    }

    [Fact]
    public async Task Rest_RetryTimeout_ShortensTheRetryWindowOnly_CappedByTheServer()
    {
        var (shorter, _) = await RunFilterAsync(("daprmq-retry-timeout", "5000"));
        var (longer, _) = await RunFilterAsync(("daprmq-retry-timeout", "600000"));

        Assert.InRange(Until(shorter!.RetryDeadline), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
        Assert.Null(shorter.CallDeadline);
        Assert.InRange(Until(longer!.RetryDeadline), TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Rest_Timeout_IsTheCallersDeadline_AndIsHonouredInFull()
    {
        var (seen, _) = await RunFilterAsync(("daprmq-timeout", "600000"));

        Assert.InRange(Until(seen!.CallDeadline!.Value), TimeSpan.FromSeconds(599), TimeSpan.FromSeconds(600));
    }

    [Theory]
    [InlineData("daprmq-timeout", "abc")]
    [InlineData("daprmq-timeout", "0")]
    [InlineData("daprmq-retry-timeout", "-5")]
    public async Task Rest_InvalidTimeout_Returns400_WithoutRunningTheAction(string header, string value)
    {
        var (seen, result) = await RunFilterAsync((header, value));

        Assert.Null(seen);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(header, Assert.IsType<ApiErrorResponse>(badRequest.Value).Message);
    }

    [Fact]
    public async Task Rest_BudgetEndsWithTheRequest()
    {
        await RunFilterAsync(("daprmq-timeout", "5000"));

        Assert.Null(DeliveryBudget.Current);
    }

    private static ServerCallContext GrpcContext(DateTime deadline, Metadata? headers = null)
    {
        var context = new Mock<ServerCallContext>();
        context.Protected().Setup<DateTime>("DeadlineCore").Returns(deadline);
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        context.Protected().Setup<Metadata>("RequestHeadersCore").Returns(headers ?? new Metadata());
        return context.Object;
    }

    private static async Task<DeliveryBudget> RunInterceptorAsync(ServerCallContext context)
    {
        DeliveryBudget? seen = null;
        await new DeliveryBudgetInterceptor(Options).UnaryServerHandler<string, string>(
            "req", context, (_, _) => { seen = DeliveryBudget.Current; return Task.FromResult("ok"); });
        Assert.Null(DeliveryBudget.Current);
        return seen!;
    }

    [Fact]
    public async Task Grpc_Deadline_IsTheCallersDeadline()
    {
        var seen = await RunInterceptorAsync(GrpcContext(DateTime.UtcNow.AddSeconds(5)));

        Assert.InRange(Until(seen.CallDeadline!.Value), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Grpc_NoDeadline_SetsNoCallDeadline_AndRetryTimeoutMetadataShortensRetries()
    {
        var seen = await RunInterceptorAsync(GrpcContext(DateTime.MaxValue, new Metadata { { "daprmq-retry-timeout", "5000" } }));

        Assert.Null(seen.CallDeadline);
        Assert.InRange(Until(seen.RetryDeadline), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
    }
}
