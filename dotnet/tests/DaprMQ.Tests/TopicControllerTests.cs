using System.Text.Json;
using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class TopicControllerTests
{
    private readonly Mock<ILogger<TopicController>> _mockLogger = new();

    private TopicController CreateController(ITopicActorInvoker invoker) => new(_mockLogger.Object, invoker);

    private static ApiPublishRequest SinglePublishRequest() => new(new List<ApiPushItem>
    {
        new ApiPushItem(JsonSerializer.SerializeToElement(new { hello = "world" }))
    });

    [Fact]
    public async Task Publish_ValidItems_Returns202WithPublishId()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PublishRequest, PublishResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<PublishRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResponse { Accepted = true, PublishId = "p1", Sequence = 0 });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.Publish("topic-a", SinglePublishRequest());

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
        var response = Assert.IsType<ApiPublishResponse>(objectResult.Value);
        Assert.Equal("p1", response.PublishId);
    }

    [Fact]
    public async Task Publish_EmptyItems_Returns400()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        var controller = CreateController(mockInvoker.Object);

        var result = await controller.Publish("topic-a", new ApiPublishRequest(new List<ApiPushItem>()));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Subscribe_NewSubscriber_Returns201WithQueueActorId()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<SubscribeRequest, SubscribeResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscribeResponse { Success = true, QueueActorId = "topic-a-sub-s1" });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.Subscribe("topic-a", "s1", null);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, objectResult.StatusCode);
        var response = Assert.IsType<ApiSubscribeResponse>(objectResult.Value);
        Assert.Equal("topic-a-sub-s1", response.QueueActorId);
    }

    [Fact]
    public async Task Subscribe_WithValidHttpSink_PassesConfigToActor()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        SubscribeRequest? captured = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<SubscribeRequest, SubscribeResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, SubscribeRequest, CancellationToken>((_, _, req, _) => captured = req)
            .ReturnsAsync(new SubscribeResponse { Success = true, QueueActorId = "topic-a-sub-s1" });

        var controller = CreateController(mockInvoker.Object);
        var request = new ApiSubscribeRequest(new ApiRegisterHttpSinkRequest("https://example.com/webhook", 10, 60));
        var result = await controller.Subscribe("topic-a", "s1", request);

        Assert.IsType<ObjectResult>(result);
        Assert.NotNull(captured?.HttpSink);
        Assert.Equal("https://example.com/webhook", captured!.HttpSink!.Url);
        Assert.Equal(10, captured.HttpSink.MaxConcurrency);
        Assert.Equal(60, captured.HttpSink.LockTtlSeconds);
    }

    [Fact]
    public async Task Subscribe_HttpSinkInvalidUrl_Returns400()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        var controller = CreateController(mockInvoker.Object);

        var request = new ApiSubscribeRequest(new ApiRegisterHttpSinkRequest("not-a-url", 5, 30));
        var result = await controller.Subscribe("topic-a", "s1", request);

        Assert.IsType<BadRequestObjectResult>(result);
        mockInvoker.Verify(i => i.InvokeMethodAsync<SubscribeRequest, SubscribeResponse>(
            It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Subscribe_HttpSinkMaxConcurrencyOutOfRange_Returns400()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        var controller = CreateController(mockInvoker.Object);

        var request = new ApiSubscribeRequest(new ApiRegisterHttpSinkRequest("https://example.com/webhook", 0, 30));
        var result = await controller.Subscribe("topic-a", "s1", request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Subscribe_HttpSinkLockTtlOutOfRange_Returns400()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        var controller = CreateController(mockInvoker.Object);

        var request = new ApiSubscribeRequest(new ApiRegisterHttpSinkRequest("https://example.com/webhook", 5, 301));
        var result = await controller.Subscribe("topic-a", "s1", request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Subscribe_DuplicateSubscriber_Returns409()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<SubscribeRequest, SubscribeResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<SubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscribeResponse { Success = false, QueueActorId = string.Empty, ErrorCode = "SUBSCRIBER_EXISTS" });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.Subscribe("topic-a", "s1", null);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Unsubscribe_Existing_Returns200()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<UnsubscribeRequest, UnsubscribeResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<UnsubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnsubscribeResponse { Success = true });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.Unsubscribe("topic-a", "s1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Unsubscribe_NotFound_Returns404()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<UnsubscribeRequest, UnsubscribeResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<UnsubscribeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnsubscribeResponse { Success = false, ErrorCode = "SUBSCRIBER_NOT_FOUND" });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.Unsubscribe("topic-a", "ghost");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetPublishStatus_NotFound_Returns404()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<GetPublishStatusRequest, PublishStatusResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<GetPublishStatusRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishStatusResponse { Found = false });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.GetPublishStatus("topic-a", "does-not-exist");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetPublishStatus_Found_Returns200()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<GetPublishStatusRequest, PublishStatusResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<GetPublishStatusRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishStatusResponse { Found = true, Complete = true, TargetSubscriberIds = new() { "a" }, DeliveredSubscriberIds = new() { "a" } });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.GetPublishStatus("topic-a", "p1");

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiPublishStatusResponse>(okResult.Value);
        Assert.True(response.Complete);
    }

    [Fact]
    public async Task ResetCircuitBreaker_Success_Returns200()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ResetCircuitBreakerRequest, ResetCircuitBreakerResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ResetCircuitBreakerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetCircuitBreakerResponse { Success = true });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.ResetCircuitBreaker("topic-a", "s1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task ResetCircuitBreaker_SubscriberNotFound_Returns404()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ResetCircuitBreakerRequest, ResetCircuitBreakerResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ResetCircuitBreakerRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResetCircuitBreakerResponse { Success = false, ErrorCode = "SUBSCRIBER_NOT_FOUND" });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.ResetCircuitBreaker("topic-a", "ghost");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetCircuitBreakerStatus_Found_Returns200WithFields()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<GetCircuitBreakerStatusRequest, CircuitBreakerStatusResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<GetCircuitBreakerStatusRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CircuitBreakerStatusResponse { Found = true, ConsecutiveFailures = 3, Blacklisted = false });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.GetCircuitBreakerStatus("topic-a", "s1");

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiCircuitBreakerStatusResponse>(okResult.Value);
        Assert.Equal(3, response.ConsecutiveFailures);
    }

    [Fact]
    public async Task GetCircuitBreakerStatus_NotFound_Returns404()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<GetCircuitBreakerStatusRequest, CircuitBreakerStatusResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<GetCircuitBreakerStatusRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CircuitBreakerStatusResponse { Found = false });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.GetCircuitBreakerStatus("topic-a", "s1");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task ListSubscribers_ReturnsList()
    {
        var mockInvoker = new Mock<ITopicActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<ListSubscribersResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListSubscribersResponse { SubscriberIds = new() { "a", "b" } });

        var controller = CreateController(mockInvoker.Object);
        var result = await controller.ListSubscribers("topic-a");

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiListSubscribersResponse>(okResult.Value);
        Assert.Equal(new List<string> { "a", "b" }, response.SubscriberIds);
    }
}
