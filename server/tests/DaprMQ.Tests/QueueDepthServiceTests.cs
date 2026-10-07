using Dapr.Actors;
using DaprMQ.Interfaces;
using Moq;

namespace DaprMQ.Tests;

public class QueueDepthServiceTests
{
    private readonly Mock<IQueueActorStateReader> _reader = new();

    private void SetupDepth(string actorId, long ready, long locked) =>
        _reader.Setup(r => r.ReadQueueDepthAsync(It.Is<ActorId>(a => a.GetId() == actorId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDepth(ready, locked));

    [Fact]
    public async Task Messages_ReportsQueueReadyAndLocked()
    {
        SetupDepth("orders", 7, 2);
        var service = new QueueDepthService(_reader.Object);

        var result = await service.GetDepthAsync(new QueueDepthQuery { QueueId = "orders" });

        Assert.Equal("orders", result.QueueId);
        Assert.Equal(7, result.Ready);
        Assert.Equal(2, result.Locked);
        Assert.Null(result.Error);
        _reader.Verify(r => r.ReadQueueDepthAsync(It.Is<ActorId>(a => a.GetId() == "orders-deadletter"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Messages_IncludeDeadLetter_AddsDeadLetterQueue()
    {
        SetupDepth("orders", 7, 2);
        SetupDepth("orders-deadletter", 4, 0);
        var service = new QueueDepthService(_reader.Object);

        var result = await service.GetDepthAsync(new QueueDepthQuery { QueueId = "orders", IncludeDeadLetter = true });

        Assert.Equal(11, result.Ready);
        Assert.Equal(2, result.Locked);
    }

    [Fact]
    public async Task Sessions_CountsNonEmptySessionsAndSumsDepth()
    {
        _reader.Setup(r => r.ReadSessionDirectoryAsync("orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "a", "b", "c" });
        SetupDepth("orders-session-a", 3, 0);
        SetupDepth("orders-session-b", 0, 0);
        SetupDepth("orders-session-c", 0, 1); // only locked items - still owns work
        var service = new QueueDepthService(_reader.Object);

        var result = await service.GetDepthAsync(new QueueDepthQuery { QueueId = "orders", Mode = QueueDepthMode.Sessions });

        Assert.Equal(2, result.NonEmptySessions);
        Assert.Equal(3, result.Ready);
        Assert.Equal(1, result.Locked);
    }

    [Fact]
    public async Task Sessions_IncludeDeadLetter_IsRejected()
    {
        var service = new QueueDepthService(_reader.Object);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetDepthAsync(
            new QueueDepthQuery { QueueId = "orders", Mode = QueueDepthMode.Sessions, IncludeDeadLetter = true }));
    }

    [Fact]
    public async Task ReadFailure_Propagates()
    {
        _reader.Setup(r => r.ReadQueueDepthAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("HTTP 500"));
        var service = new QueueDepthService(_reader.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetDepthAsync(new QueueDepthQuery { QueueId = "orders" }));
    }
}
