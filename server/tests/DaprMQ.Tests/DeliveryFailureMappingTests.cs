using System.Text.Json;
using Dapr;
using Dapr.Actors;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using ActorModels = DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Delivery failures (proposals/readiness-and-retries.md, section 2) reach callers as
/// UNAVAILABLE (503 / gRPC UNAVAILABLE, never ran) or DELIVERY_UNKNOWN (504 / gRPC UNKNOWN, may have
/// run), each with a daprmq-delivery marker, instead of a 500 that looks like a bug.
/// </summary>
public class DeliveryFailureMappingTests
{
    private static ActorCallException NotDelivered() =>
        new(DeliveryOutcome.NotDelivered, new DaprApiException("failed to lookup actor: did not find address for actor 'QueueActor/q'"));

    private static ActorCallException Unknown() =>
        new(DeliveryOutcome.Unknown, new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));

    private static QueueController CreateController(IQueueActorInvoker invoker) => new(
        new Mock<ILogger<QueueController>>().Object,
        invoker,
        new Mock<IHttpSinkActorInvoker>().Object,
        new Mock<Dapr.Actors.Client.IActorProxyFactory>().Object,
        new Mock<IObjectStore>().Object,
        new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig { SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(), TokenTtl = TimeSpan.FromMinutes(5) }),
        new Mock<IBlobReaperActorInvoker>().Object,
        new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 },
        new Mock<ISessionCoordinatorActorInvoker>().Object);

    private static Mock<IQueueActorInvoker> EnqueueThrows(Exception ex)
    {
        var invoker = new Mock<IQueueActorInvoker>();
        invoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);
        return invoker;
    }

    private static ApiEnqueueRequest OneItem() =>
        new([new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { n = 1 }), Priority: 1)]);

    /// <summary>Formats the result as MVC would, and returns the response headers it set.</summary>
    private static IHeaderDictionary Headers(IActionResult result)
    {
        var httpContext = new DefaultHttpContext();
        ((ObjectResult)result).OnFormatting(new ActionContext(httpContext, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        return httpContext.Response.Headers;
    }

    [Fact]
    public async Task Rest_NotDelivered_Returns503Unavailable_WithRetryAfterAndMarker()
    {
        var result = await CreateController(EnqueueThrows(NotDelivered()).Object).Enqueue("q", OneItem());

        var objectResult = Assert.IsType<DeliveryFailureResult>(result);
        Assert.Equal(503, objectResult.StatusCode);
        Assert.Equal("UNAVAILABLE", Assert.IsType<ApiErrorResponse>(objectResult.Value).ErrorCode);
        var headers = Headers(result);
        Assert.Equal("not-delivered", headers["daprmq-delivery"]);
        Assert.Equal("1", headers.RetryAfter);
    }

    [Fact]
    public async Task Rest_Unknown_Returns504DeliveryUnknown_WithMarkerAndNoRetryAfter()
    {
        var invoker = new Mock<IQueueActorInvoker>();
        invoker.Setup(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Unknown());

        var result = await CreateController(invoker.Object).Dequeue("q", require_ack: true, ttl_seconds: 30);

        var objectResult = Assert.IsType<DeliveryFailureResult>(result);
        Assert.Equal(504, objectResult.StatusCode);
        Assert.Equal("DELIVERY_UNKNOWN", Assert.IsType<ApiErrorResponse>(objectResult.Value).ErrorCode);
        var headers = Headers(result);
        Assert.Equal("unknown", headers["daprmq-delivery"]);
        Assert.False(headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Rest_Nack_NotDelivered_Returns503Unavailable()
    {
        var invoker = new Mock<IQueueActorInvoker>();
        invoker.Setup(i => i.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.NackRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(NotDelivered());

        var result = await CreateController(invoker.Object).Nack("q", new ApiNackRequest("L1"));

        var objectResult = Assert.IsType<DeliveryFailureResult>(result);
        Assert.Equal(503, objectResult.StatusCode);
        Assert.Equal("UNAVAILABLE", Assert.IsType<ApiErrorResponse>(objectResult.Value).ErrorCode);
    }

    [Fact]
    public async Task Rest_NonDeliveryFailure_StillReturns500()
    {
        var result = await CreateController(EnqueueThrows(new InvalidOperationException("bug")).Object).Enqueue("q", OneItem());

        Assert.Equal(500, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.IsNotType<DeliveryFailureResult>(result);
    }

    [Fact]
    public void Rest_Messages_DontMentionActors()
    {
        foreach (var ex in new[] { NotDelivered(), Unknown() })
        {
            var body = Assert.IsType<ApiErrorResponse>(new DeliveryFailureResult(ex).Value);
            Assert.DoesNotContain("actor", body.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Grpc_Nack_Unknown_ReturnsUnknownWithMarker()
    {
        var invoker = new Mock<IQueueActorInvoker>();
        invoker.Setup(i => i.InvokeMethodAsync<ActorModels.NackRequest, ActorModels.NackResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.NackRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Unknown());
        var service = new DaprMQGrpcService(new Mock<ILogger<DaprMQGrpcService>>().Object, invoker.Object, new Mock<ISessionCoordinatorActorInvoker>().Object);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.Nack(new ApiServer.Grpc.NackRequest { QueueId = "q", LockId = "L1" }, new Mock<ServerCallContext>().Object));

        Assert.Equal(StatusCode.Unknown, ex.StatusCode);
        Assert.Equal("unknown", ex.Trailers.GetValue("daprmq-delivery"));
    }

    [Theory]
    [InlineData(true, StatusCode.Unavailable, "not-delivered")]
    [InlineData(false, StatusCode.Unknown, "unknown")]
    public async Task Grpc_MapsOutcome_ToStatusAndTrailer(bool notDelivered, StatusCode expected, string marker)
    {
        var invoker = EnqueueThrows(notDelivered ? NotDelivered() : Unknown());
        var service = new DaprMQGrpcService(new Mock<ILogger<DaprMQGrpcService>>().Object, invoker.Object, new Mock<ISessionCoordinatorActorInvoker>().Object);
        var request = new ApiServer.Grpc.EnqueueRequest { QueueId = "q" };
        request.Items.Add(new ApiServer.Grpc.EnqueueItem { ItemJson = "{\"n\":1}", Priority = 1 });

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.Enqueue(request, new Mock<ServerCallContext>().Object));

        Assert.Equal(expected, ex.StatusCode);
        Assert.Equal(marker, ex.Trailers.GetValue("daprmq-delivery"));
        Assert.DoesNotContain("actor", ex.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
