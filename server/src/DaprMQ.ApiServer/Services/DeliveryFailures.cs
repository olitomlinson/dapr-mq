using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// How a delivery failure (<see cref="ActorCallException"/>) reaches callers: UNAVAILABLE when the
/// operation certainly didn't run, DELIVERY_UNKNOWN when it may have, each with a daprmq-delivery
/// marker (REST header / gRPC trailer). Public names and messages never mention actors.
/// See proposals/readiness-and-retries.md, section 2.
/// </summary>
public static class DeliveryFailures
{
    public const string MarkerHeader = "daprmq-delivery";
    public const string UnavailableCode = "UNAVAILABLE";
    public const string DeliveryUnknownCode = "DELIVERY_UNKNOWN";

    public static string Marker(DeliveryOutcome outcome) =>
        outcome == DeliveryOutcome.NotDelivered ? "not-delivered" : "unknown";

    public static string ErrorCode(DeliveryOutcome outcome) =>
        outcome == DeliveryOutcome.NotDelivered ? UnavailableCode : DeliveryUnknownCode;

    public static string Message(DeliveryOutcome outcome) => outcome == DeliveryOutcome.NotDelivered
        ? "DaprMQ is temporarily unavailable; the operation was not performed and can be retried."
        : "The operation's outcome is unknown: it may or may not have been performed.";

    public static RpcException ToRpcException(ActorCallException ex) => new(
        new Status(ex.Outcome == DeliveryOutcome.NotDelivered ? StatusCode.Unavailable : StatusCode.Unknown, Message(ex.Outcome)),
        new Metadata { { MarkerHeader, Marker(ex.Outcome) } });
}

/// <summary>
/// REST form of a delivery failure: 503 + Retry-After when not delivered, 504 when unknown, with the
/// marker header. Headers are written while formatting, so actions can return it without an HttpContext.
/// </summary>
public sealed class DeliveryFailureResult : ObjectResult
{
    public DeliveryOutcome Outcome { get; }

    public DeliveryFailureResult(ActorCallException ex)
        : base(new ApiErrorResponse(DeliveryFailures.Message(ex.Outcome), ErrorCode: DeliveryFailures.ErrorCode(ex.Outcome)))
    {
        Outcome = ex.Outcome;
        StatusCode = ex.Outcome == DeliveryOutcome.NotDelivered ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status504GatewayTimeout;
    }

    public override void OnFormatting(ActionContext context)
    {
        base.OnFormatting(context);
        var headers = context.HttpContext.Response.Headers;
        headers[DeliveryFailures.MarkerHeader] = DeliveryFailures.Marker(Outcome);
        if (Outcome == DeliveryOutcome.NotDelivered)
        {
            headers.RetryAfter = "1";
        }
    }
}
