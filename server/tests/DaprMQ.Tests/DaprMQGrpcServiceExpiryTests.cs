using Dapr.Actors;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using ActorModels = DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// DaprMQGrpcService's side of message expiry: validating and stamping a time to live on enqueue, retrying
/// a dequeue that only removed expired items, and carrying each item's expiry in the response.
/// </summary>
public class DaprMQGrpcServiceExpiryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly Mock<IQueueActorInvoker> _invoker = new();
    private readonly Mock<ServerCallContext> _context = new();
    private ActorModels.EnqueueRequest? _captured;

    public DaprMQGrpcServiceExpiryTests()
    {
        _invoker.Setup(i => i.InvokeMethodAsync<ActorModels.EnqueueRequest, ActorModels.EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<ActorModels.EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, ActorModels.EnqueueRequest, CancellationToken>((_, _, req, _) => _captured = req)
            .ReturnsAsync(new ActorModels.EnqueueResponse { Success = true, ItemsEnqueued = 1 });
    }

    private DaprMQGrpcService CreateService(ExpiryConfig? expiryConfig = null) =>
        new(Mock.Of<ILogger<DaprMQGrpcService>>(), _invoker.Object, Mock.Of<ISessionCoordinatorActorInvoker>(),
            expiryStamper: new MessageExpiryStamper(expiryConfig ?? new ExpiryConfig(), new FakeTimeProvider(Now)));

    private static ApiServer.Grpc.EnqueueRequest Request(int? expiryTtlSeconds)
    {
        var item = new ApiServer.Grpc.EnqueueItem { ItemJson = "{\"id\":1}", Priority = 1 };
        if (expiryTtlSeconds is int ttl)
        {
            item.ExpiryTtlSeconds = ttl;
        }
        return new ApiServer.Grpc.EnqueueRequest { QueueId = "q", Items = { item } };
    }

    [Theory]
    [InlineData(599)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ExpiryConfig.MaxExpiryTtlSeconds + 1)]
    public async Task Enqueue_ExpiryTtlSecondsOutOfRange_IsInvalidArgument(int expiryTtlSeconds)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => CreateService().Enqueue(Request(expiryTtlSeconds), _context.Object));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Null(_captured);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(ExpiryConfig.MaxExpiryTtlSeconds)]
    public async Task Enqueue_ExpiryTtlSecondsAtTheBounds_IsAccepted(int expiryTtlSeconds)
    {
        var response = await CreateService().Enqueue(Request(expiryTtlSeconds), _context.Object);

        Assert.True(response.Success);
    }

    [Fact]
    public async Task Enqueue_StampsExpiryAtFromTheServicesClock()
    {
        await CreateService().Enqueue(Request(3600), _context.Object);

        var item = Assert.Single(_captured!.Items);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
        Assert.Equal(Now.ToUnixTimeSeconds() + 3600, item.ExpiryAt);
    }

    [Fact]
    public async Task Enqueue_WithoutATimeToLive_UsesTheConfiguredDefault()
    {
        await CreateService(new ExpiryConfig { DefaultExpiryTtlSeconds = 7200 }).Enqueue(Request(null), _context.Object);

        var item = Assert.Single(_captured!.Items);
        Assert.Equal(7200, item.ExpiryTtlSeconds);
        Assert.Equal(Now.ToUnixTimeSeconds() + 7200, item.ExpiryAt);
    }

    [Fact]
    public async Task Dequeue_ExpiredMore_IsRetried_AndItemsCarryTheirExpiry()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                It.IsAny<ActorId>(), "Dequeue", It.IsAny<ActorModels.DequeueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueResponse { ExpiredMore = true })
            .ReturnsAsync(new ActorModels.DequeueResponse
            {
                Items =
                [
                    new ActorModels.DequeueItem { ItemJson = "{\"id\":1}", Priority = 1, ExpiryAt = 1_800_003_600, ExpiryTtlSeconds = 3600, OriginalExpiryTtlSeconds = 60 },
                    new ActorModels.DequeueItem { ItemJson = "{\"id\":2}", Priority = 1 }
                ]
            });

        var response = await CreateService().Dequeue(new ApiServer.Grpc.DequeueRequest { QueueId = "q", Count = 2 }, _context.Object);

        Assert.Equal([1_800_003_600, 0], response.Success.ExpiryAt);
        Assert.Equal([3600, 0], response.Success.ExpiryTtlSeconds);
        Assert.Equal([60, 0], response.Success.OriginalExpiryTtlSeconds);
    }

    [Fact]
    public async Task Dequeue_ExpiredMore_IsRetriedUntilEmpty()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
                It.IsAny<ActorId>(), "Dequeue", It.IsAny<ActorModels.DequeueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueResponse { ExpiredMore = true })
            .ReturnsAsync(new ActorModels.DequeueResponse { IsEmpty = true });

        var response = await CreateService().Dequeue(new ApiServer.Grpc.DequeueRequest { QueueId = "q" }, _context.Object);

        Assert.Equal(ApiServer.Grpc.DequeueResponse.ResultOneofCase.Empty, response.ResultCase);
        _invoker.Verify(i => i.InvokeMethodAsync<ActorModels.DequeueRequest, ActorModels.DequeueResponse>(
            It.IsAny<ActorId>(), "Dequeue", It.IsAny<ActorModels.DequeueRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DequeueLocked_ExpiredMore_IsRetried_AndItemsCarryTheirExpiry()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse { ExpiredMore = true })
            .ReturnsAsync(new ActorModels.DequeueLockedResponse
            {
                Items =
                [
                    new ActorModels.DequeueLockedItem
                    {
                        ItemJson = "{\"id\":1}", Priority = 1, LockId = "lock", LockExpiresAt = 1_800_000_030,
                        ExpiryAt = 1_800_003_600, ExpiryTtlSeconds = 3600
                    }
                ]
            });

        var response = await CreateService().DequeueLocked(new ApiServer.Grpc.DequeueLockedRequest { QueueId = "q" }, _context.Object);

        Assert.Equal([1_800_003_600], response.Success.ExpiryAt);
        Assert.Equal([3600], response.Success.ExpiryTtlSeconds);
        Assert.Equal([0], response.Success.OriginalExpiryTtlSeconds);
    }

    [Fact]
    public async Task DequeueLocked_ExpiredMore_IsRetriedUntilLocked()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<ActorModels.DequeueLockedRequest, ActorModels.DequeueLockedResponse>(
                It.IsAny<ActorId>(), "DequeueLocked", It.IsAny<ActorModels.DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActorModels.DequeueLockedResponse { ExpiredMore = true })
            .ReturnsAsync(new ActorModels.DequeueLockedResponse { Locked = true });

        var response = await CreateService().DequeueLocked(new ApiServer.Grpc.DequeueLockedRequest { QueueId = "q" }, _context.Object);

        Assert.Equal(ApiServer.Grpc.DequeueLockedResponse.ResultOneofCase.Locked, response.ResultCase);
    }
}
