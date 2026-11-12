using System.Text.Json;
using Dapr.Actors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// QueueController's side of message expiry: validating and stamping a time to live on enqueue, and
/// retrying a dequeue that only removed expired items.
/// </summary>
public class QueueControllerExpiryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly Mock<IQueueActorInvoker> _invoker = new();
    private readonly Mock<IObjectStore> _objectStore = new();
    private EnqueueRequest? _captured;

    public QueueControllerExpiryTests()
    {
        _invoker.Setup(i => i.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(
                It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<EnqueueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, EnqueueRequest, CancellationToken>((_, _, req, _) => _captured = req)
            .ReturnsAsync(new EnqueueResponse { Success = true, ItemsEnqueued = 1 });
    }

    private QueueController CreateController(ExpiryConfig? expiryConfig = null) =>
        new(
            Mock.Of<ILogger<QueueController>>(),
            _invoker.Object,
            Mock.Of<IHttpSinkActorInvoker>(),
            Mock.Of<Dapr.Actors.Client.IActorProxyFactory>(),
            _objectStore.Object,
            new ObjectStoreConfig { StoreName = "daprmq-blobstore", MaxObjectBytes = 1024 },
            new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
            {
                SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray(),
                TokenTtl = TimeSpan.FromMinutes(5)
            }),
            Mock.Of<IBlobReaperActorInvoker>(),
            new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 },
            Mock.Of<ISessionCoordinatorActorInvoker>(),
            new MessageExpiryStamper(expiryConfig ?? new ExpiryConfig(), new FakeTimeProvider(Now)));

    private static ApiEnqueueRequest Request(int? expiryTtlSeconds) =>
        new([new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { id = 1 }), ExpiryTtlSeconds: expiryTtlSeconds)]);

    [Theory]
    [InlineData(599)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ExpiryConfig.MaxExpiryTtlSeconds + 1)]
    public async Task Enqueue_ExpiryTtlSecondsOutOfRange_Returns400(int expiryTtlSeconds)
    {
        var result = await CreateController().Enqueue("q", Request(expiryTtlSeconds));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(_captured);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(ExpiryConfig.MaxExpiryTtlSeconds)]
    public async Task Enqueue_ExpiryTtlSecondsAtTheBounds_IsAccepted(int expiryTtlSeconds)
    {
        var result = await CreateController().Enqueue("q", Request(expiryTtlSeconds));

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Enqueue_BelowAConfiguredMinimum_Returns400()
    {
        var result = await CreateController(new ExpiryConfig { MinExpiryTtlSeconds = 3600 }).Enqueue("q", Request(3599));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Enqueue_StampsExpiryAtFromTheControllersClock()
    {
        await CreateController().Enqueue("q", Request(3600));

        var item = Assert.Single(_captured!.Items);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
        Assert.Equal(Now.ToUnixTimeSeconds() + 3600, item.ExpiryAt);
    }

    [Fact]
    public async Task Enqueue_WithoutATimeToLive_UsesTheConfiguredDefault()
    {
        await CreateController(new ExpiryConfig { DefaultExpiryTtlSeconds = 7200 }).Enqueue("q", Request(null));

        var item = Assert.Single(_captured!.Items);
        Assert.Equal(7200, item.ExpiryTtlSeconds);
        Assert.Equal(Now.ToUnixTimeSeconds() + 7200, item.ExpiryAt);
    }

    private QueueController ObjectController(ExpiryConfig? expiryConfig = null)
    {
        _objectStore.Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("q/obj");
        var controller = CreateController(expiryConfig);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream("body"u8.ToArray());
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Fact]
    public async Task EnqueueObject_StampsTheExpiryTtlSecondsHeader()
    {
        await ObjectController().EnqueueObject("q", expiryTtlSeconds: 3600);

        var item = Assert.Single(_captured!.Items);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
        Assert.Equal(Now.ToUnixTimeSeconds() + 3600, item.ExpiryAt);
    }

    [Fact]
    public async Task EnqueueObject_WithoutATimeToLive_UsesTheConfiguredDefault()
    {
        await ObjectController(new ExpiryConfig { DefaultExpiryTtlSeconds = 7200 }).EnqueueObject("q");

        Assert.Equal(7200, Assert.Single(_captured!.Items).ExpiryTtlSeconds);
    }

    [Fact]
    public async Task EnqueueObject_ExpiryTtlSecondsOutOfRange_Returns400BeforeUploading()
    {
        var result = await ObjectController().EnqueueObject("q", expiryTtlSeconds: 1);

        Assert.IsType<BadRequestObjectResult>(result);
        _objectStore.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dequeue_ExpiredMore_IsRetriedUntilItemsComeBack()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Dequeue, It.IsAny<DequeueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueResponse
            {
                Items = [new DequeueItem { ItemJson = "{\"id\":1}", Priority = 1, ExpiryAt = 1_800_003_600, ExpiryTtlSeconds = 3600 }]
            });

        var result = await CreateController().Dequeue("q");

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.Single(Assert.IsType<ApiDequeueResponse>(ok.Value).Items);
        Assert.Equal(1_800_003_600, item.ExpiryAt);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
        _invoker.Verify(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
            It.IsAny<ActorId>(), ActorMethodNames.Dequeue, It.IsAny<DequeueRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Dequeue_ExpiredMore_IsRetriedUntilEmpty()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<DequeueRequest, DequeueResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.Dequeue, It.IsAny<DequeueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueResponse { IsEmpty = true });

        var result = await CreateController().Dequeue("q");

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DequeueLocked_ExpiredMore_IsRetriedUntilItemsComeBack()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.DequeueLocked, It.IsAny<DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueLockedResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueLockedResponse
            {
                Items =
                [
                    new DequeueLockedItem
                    {
                        ItemJson = "{\"id\":1}", Priority = 1, LockId = "lock", LockExpiresAt = 1_800_000_030,
                        ExpiryAt = 1_800_003_600, ExpiryTtlSeconds = 3600, OriginalExpiryTtlSeconds = 60
                    }
                ]
            });

        var result = await CreateController().Dequeue("q", require_ack: true);

        var ok = Assert.IsType<OkObjectResult>(result);
        var item = Assert.Single(Assert.IsType<ApiDequeueLockedResponse>(ok.Value).Items);
        Assert.Equal(1_800_003_600, item.ExpiryAt);
        Assert.Equal(3600, item.ExpiryTtlSeconds);
        Assert.Equal(60, item.OriginalExpiryTtlSeconds);
    }

    [Fact]
    public async Task DequeueLocked_ExpiredMore_IsRetriedUntilLocked()
    {
        _invoker.SetupSequence(i => i.InvokeMethodAsync<DequeueLockedRequest, DequeueLockedResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.DequeueLocked, It.IsAny<DequeueLockedRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DequeueLockedResponse { ExpiredMore = true })
            .ReturnsAsync(new DequeueLockedResponse { Locked = true });

        var result = await CreateController().Dequeue("q", require_ack: true);

        Assert.Equal(423, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
