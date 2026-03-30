using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests.Controllers;

public class DaprPubSubControllerTests
{
    private readonly Mock<ILogger<DaprPubSubController>> _mockLogger;
    private readonly Mock<IDaprPubSubSinkActorInvoker> _mockDaprPubSubSinkActorInvoker;

    public DaprPubSubControllerTests()
    {
        _mockLogger = new Mock<ILogger<DaprPubSubController>>();
        _mockDaprPubSubSinkActorInvoker = new Mock<IDaprPubSubSinkActorInvoker>();
    }

    [Fact]
    public async Task RegisterDaprPubSubSink_ValidRequest_Returns200()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);
        var request = new ApiRegisterDaprPubSubSinkRequest(
            "test-pubsub",
            "test-topic",
            false,
            10,
            30
        );

        // Act
        var result = await controller.RegisterDaprPubSubSink("test-queue", request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiRegisterDaprPubSubSinkResponse>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("test-queue-pubsub-sink", response.DaprPubSubSinkActorId);
    }

    [Fact]
    public async Task RegisterDaprPubSubSink_EmptyPubSubName_Returns400()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);
        var request = new ApiRegisterDaprPubSubSinkRequest(
            "",
            "test-topic",
            false,
            10,
            30
        );

        // Act
        var result = await controller.RegisterDaprPubSubSink("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiRegisterDaprPubSubSinkResponse>(badRequestResult.Value);
        Assert.False(response.Success);
    }

    [Fact]
    public async Task RegisterDaprPubSubSink_EmptyTopic_Returns400()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);
        var request = new ApiRegisterDaprPubSubSinkRequest(
            "test-pubsub",
            "",
            false,
            10,
            30
        );

        // Act
        var result = await controller.RegisterDaprPubSubSink("test-queue", request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var response = Assert.IsType<ApiRegisterDaprPubSubSinkResponse>(badRequestResult.Value);
        Assert.False(response.Success);
    }

    [Fact]
    public async Task RegisterDaprPubSubSink_MaxConcurrencyTooLow_Returns400()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);
        var request = new ApiRegisterDaprPubSubSinkRequest(
            "test-pubsub",
            "test-topic",
            false,
            0,
            30
        );

        // Act
        var result = await controller.RegisterDaprPubSubSink("test-queue", request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RegisterDaprPubSubSink_MaxConcurrencyTooHigh_Returns400()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);
        var request = new ApiRegisterDaprPubSubSinkRequest(
            "test-pubsub",
            "test-topic",
            false,
            101,
            30
        );

        // Act
        var result = await controller.RegisterDaprPubSubSink("test-queue", request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RegisterDaprPubSubSink_LockTtlTooLow_Returns400()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);
        var request = new ApiRegisterDaprPubSubSinkRequest(
            "test-pubsub",
            "test-topic",
            false,
            10,
            0
        );

        // Act
        var result = await controller.RegisterDaprPubSubSink("test-queue", request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RegisterDaprPubSubSink_LockTtlTooHigh_Returns400()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);
        var request = new ApiRegisterDaprPubSubSinkRequest(
            "test-pubsub",
            "test-topic",
            false,
            10,
            301
        );

        // Act
        var result = await controller.RegisterDaprPubSubSink("test-queue", request);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }


    [Fact]
    public async Task UnregisterDaprPubSubSink_ValidQueue_Returns200()
    {
        // Arrange
        var controller = new DaprPubSubController(_mockLogger.Object, _mockDaprPubSubSinkActorInvoker.Object);

        // Act
        var result = await controller.UnregisterDaprPubSubSink("test-queue");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiUnregisterDaprPubSubSinkResponse>(okResult.Value);
        Assert.True(response.Success);
    }
}
