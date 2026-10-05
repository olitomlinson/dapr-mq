using DaprMQ.Interfaces;
using DaprMQ.Operator.Scaling;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class OperatorCachingWorkerDepthClientTests
{
    private readonly Mock<IWorkerDepthClient> _inner = new();
    private readonly FakeTimeProvider _time = new();
    private static readonly QueueDepthQuery Orders = new() { QueueId = "orders" };

    private CachingWorkerDepthClient CreateClient() => new(_inner.Object, TimeSpan.FromSeconds(1), _time);

    [Fact]
    public async Task RepeatedCallsWithinTtl_HitWorkerOnce()
    {
        _inner.Setup(c => c.GetDepthAsync(Orders, It.IsAny<CancellationToken>())).ReturnsAsync(new QueueDepthResult { QueueId = "orders", Ready = 3 });
        var client = CreateClient();

        await client.GetDepthAsync(Orders, CancellationToken.None);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        var second = await client.GetDepthAsync(Orders, CancellationToken.None);

        Assert.Equal(3, second.Ready);
        _inner.Verify(c => c.GetDepthAsync(Orders, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CallAfterTtl_RefreshesFromWorker()
    {
        _inner.Setup(c => c.GetDepthAsync(Orders, It.IsAny<CancellationToken>())).ReturnsAsync(new QueueDepthResult { QueueId = "orders" });
        var client = CreateClient();

        await client.GetDepthAsync(Orders, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1.1));
        await client.GetDepthAsync(Orders, CancellationToken.None);

        _inner.Verify(c => c.GetDepthAsync(Orders, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DifferentQueries_CachedSeparately()
    {
        var dlq = Orders with { IncludeDeadLetter = true };
        _inner.Setup(c => c.GetDepthAsync(It.IsAny<QueueDepthQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(new QueueDepthResult());
        var client = CreateClient();

        await client.GetDepthAsync(Orders, CancellationToken.None);
        await client.GetDepthAsync(dlq, CancellationToken.None);

        _inner.Verify(c => c.GetDepthAsync(Orders, It.IsAny<CancellationToken>()), Times.Once);
        _inner.Verify(c => c.GetDepthAsync(dlq, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Failure_IsNotCached()
    {
        _inner.SetupSequence(c => c.GetDepthAsync(Orders, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("worker down"))
            .ReturnsAsync(new QueueDepthResult { QueueId = "orders", Ready = 1 });
        var client = CreateClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDepthAsync(Orders, CancellationToken.None));
        var retry = await client.GetDepthAsync(Orders, CancellationToken.None);

        Assert.Equal(1, retry.Ready);
    }

    [Fact]
    public async Task ErrorResult_IsNotCached()
    {
        _inner.SetupSequence(c => c.GetDepthAsync(Orders, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDepthResult { QueueId = "orders", Error = "HTTP 500" })
            .ReturnsAsync(new QueueDepthResult { QueueId = "orders", Ready = 1 });
        var client = CreateClient();

        await client.GetDepthAsync(Orders, CancellationToken.None);
        var retry = await client.GetDepthAsync(Orders, CancellationToken.None);

        Assert.Null(retry.Error);
    }
}
