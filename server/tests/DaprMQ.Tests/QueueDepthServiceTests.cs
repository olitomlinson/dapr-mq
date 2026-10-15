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
    public async Task Sessions_ReadsOnlyTheCoordinatorsActiveCount()
    {
        _reader.Setup(r => r.ReadDirectorySummaryAsync("orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectorySummary { TotalCount = 8, ActiveCount = 5 });
        var service = new QueueDepthService(_reader.Object);

        var result = await service.GetDepthAsync(new QueueDepthQuery { QueueId = "orders", Mode = QueueDepthMode.Sessions });

        Assert.Equal(5, result.NonEmptySessions);
        Assert.Equal(0, result.Ready);
        Assert.Equal(0, result.Locked);
        _reader.Verify(r => r.ReadQueueDepthAsync(It.IsAny<ActorId>(), It.IsAny<CancellationToken>()), Times.Never);
        _reader.Verify(r => r.ReadSessionDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sessions_SummaryReadFailure_Propagates()
    {
        _reader.Setup(r => r.ReadDirectorySummaryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("HTTP 500"));
        var service = new QueueDepthService(_reader.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetDepthAsync(
            new QueueDepthQuery { QueueId = "orders", Mode = QueueDepthMode.Sessions }));
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
