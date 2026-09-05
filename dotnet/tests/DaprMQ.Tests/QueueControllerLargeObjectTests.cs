using System.Text.Json;
using Dapr.Actors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Moq;
using DaprMQ.ApiServer.Controllers;
using DaprMQ.ApiServer.Models;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Unit tests for QueueController's large-object endpoints (push-object, and blob dereferencing on Pop).
/// </summary>
public class QueueControllerLargeObjectTests
{
    private readonly Mock<ILogger<QueueController>> _mockLogger = new();
    private readonly Mock<IHttpSinkActorInvoker> _mockHttpSinkActorInvoker = new();
    private readonly Mock<Dapr.Actors.Client.IActorProxyFactory> _mockActorProxyFactory = new();
    private static readonly byte[] SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray();
    private readonly IObjectClaimTokenIssuer _tokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
    {
        SigningKey = SigningKey,
        TokenTtl = TimeSpan.FromMinutes(5)
    });

    private QueueController CreateController(
        Mock<IQueueActorInvoker> mockInvoker,
        Mock<IObjectStore> mockObjectStore,
        string body = "hello world",
        Mock<IBlobReaperActorInvoker>? mockBlobReaperActorInvoker = null)
    {
        var controller = new QueueController(
            _mockLogger.Object,
            mockInvoker.Object,
            _mockHttpSinkActorInvoker.Object,
            _mockActorProxyFactory.Object,
            mockObjectStore.Object,
            _tokenIssuer,
            (mockBlobReaperActorInvoker ?? new Mock<IBlobReaperActorInvoker>()).Object,
            new BlobReapConfig { BackstopSeconds = 86400, PostDownloadSeconds = 86400 });
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Fact]
    public async Task PushObject_UploadsBodyAndPushesBlobRefEnvelope()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        PushRequest? capturedRequest = null;
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(), "Push", It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, PushRequest, CancellationToken>((_, _, req, _) => capturedRequest = req)
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 1 });

        var mockObjectStore = new Mock<IObjectStore>();
        mockObjectStore.Setup(o => o.UploadAsync("test-queue", It.IsAny<string>(), It.IsAny<Stream>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-queue/generated-id");

        var controller = CreateController(mockInvoker, mockObjectStore);

        var result = await controller.PushObject("test-queue");

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(capturedRequest);
        Assert.Single(capturedRequest!.Items);
        Assert.True(BlobReferenceEnvelope.TryParse(capturedRequest.Items[0].ItemJson, out var blobReference));
        Assert.Equal("test-queue/generated-id", blobReference);
    }

    [Fact]
    public async Task PushObject_UsesExplicitPrefixWhenProvided()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PushRequest, PushResponse>(
                It.IsAny<ActorId>(), "Push", It.IsAny<PushRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushResponse { Success = true, ItemsPushed = 1 });

        var mockObjectStore = new Mock<IObjectStore>();
        mockObjectStore.Setup(o => o.UploadAsync("store-b", It.IsAny<string>(), It.IsAny<Stream>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("store-b/generated-id");

        var controller = CreateController(mockInvoker, mockObjectStore);

        var result = await controller.PushObject("test-queue", prefix: "store-b");

        Assert.IsType<OkObjectResult>(result);
        mockObjectStore.Verify(o => o.UploadAsync("store-b", It.IsAny<string>(), It.IsAny<Stream>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PushObject_NegativePriority_ReturnsBadRequest()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var mockObjectStore = new Mock<IObjectStore>();
        var controller = CreateController(mockInvoker, mockObjectStore);

        var result = await controller.PushObject("test-queue", priority: -1);

        Assert.IsType<BadRequestObjectResult>(result);
        mockObjectStore.Verify(o => o.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Pop_SingleBlobRefItem_ReturnsJsonWithObjectClaimTokenNoInlineBytes()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PopRequest, PopResponse>(
                It.IsAny<ActorId>(), "Pop", It.IsAny<PopRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PopResponse
            {
                Items = new List<PopItem> { new() { ItemJson = "{}", Priority = 0, ObjectClaimToken = "claim-token-1" } },
                Locked = false,
                IsEmpty = false
            });

        var mockObjectStore = new Mock<IObjectStore>();
        var controller = CreateController(mockInvoker, mockObjectStore);

        var result = await controller.Pop("test-queue", require_ack: false);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiPopResponse>(okResult.Value);
        var itemElement = (JsonElement)response.Items[0].Item;
        Assert.Equal("claim-token-1", itemElement.GetProperty("objectClaimToken").GetString());
        mockObjectStore.Verify(o => o.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Pop_MultipleItemsWithOneBlobRef_ReturnsObjectClaimTokenForBlobItem()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        mockInvoker.Setup(i => i.InvokeMethodAsync<PopRequest, PopResponse>(
                It.IsAny<ActorId>(), "Pop", It.IsAny<PopRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PopResponse
            {
                Items = new List<PopItem>
                {
                    new() { ItemJson = "{\"a\":1}", Priority = 0, ObjectClaimToken = null },
                    new() { ItemJson = "{}", Priority = 0, ObjectClaimToken = "claim-token-2" }
                },
                Locked = false,
                IsEmpty = false
            });

        var mockObjectStore = new Mock<IObjectStore>();
        var controller = CreateController(mockInvoker, mockObjectStore);

        var result = await controller.Pop("test-queue", require_ack: false, count: 2);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiPopResponse>(okResult.Value);
        Assert.Equal(2, response.Items.Count);
        var blobItemElement = (JsonElement)response.Items[1].Item;
        Assert.Equal("claim-token-2", blobItemElement.GetProperty("objectClaimToken").GetString());
        mockObjectStore.Verify(o => o.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetObject_ValidToken_StreamsContentAndPostponesReap()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var mockObjectStore = new Mock<IObjectStore>();
        var contentBytes = System.Text.Encoding.UTF8.GetBytes("fetched via token");
        mockObjectStore.Setup(o => o.DownloadAsync("blob-ref-3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(contentBytes));

        var mockBlobReaperActorInvoker = new Mock<IBlobReaperActorInvoker>();
        PostponeDeletionRequest? capturedRequest = null;
        mockBlobReaperActorInvoker.Setup(i => i.InvokeMethodAsync<PostponeDeletionRequest>(
                It.IsAny<ActorId>(), "PostponeDeletion", It.IsAny<PostponeDeletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ActorId, string, PostponeDeletionRequest, CancellationToken>((_, _, req, _) => capturedRequest = req)
            .Returns(Task.CompletedTask);

        var controller = CreateController(mockInvoker, mockObjectStore, mockBlobReaperActorInvoker: mockBlobReaperActorInvoker);
        var token = _tokenIssuer.Issue("blob-ref-3", "application/pdf");

        var result = await controller.GetObject(token, CancellationToken.None);

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/pdf", fileResult.ContentType);
        Assert.Equal("blob-ref-3.pdf", fileResult.FileDownloadName);
        Assert.NotNull(capturedRequest);
        Assert.Equal("blob-ref-3", capturedRequest!.BlobReference);
        Assert.Equal(86400, capturedRequest.NewDelaySeconds);
    }

    [Fact]
    public async Task GetObject_MalformedToken_Returns400()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var mockObjectStore = new Mock<IObjectStore>();
        var controller = CreateController(mockInvoker, mockObjectStore);

        var result = await controller.GetObject("not-a-valid-token", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetObject_ExpiredToken_Returns410()
    {
        var mockInvoker = new Mock<IQueueActorInvoker>();
        var mockObjectStore = new Mock<IObjectStore>();
        var controller = CreateController(mockInvoker, mockObjectStore);
        var expiredTokenIssuer = new ObjectClaimTokenIssuer(new ObjectClaimTokenConfig
        {
            SigningKey = SigningKey,
            TokenTtl = TimeSpan.FromSeconds(-1)
        });
        var expiredToken = expiredTokenIssuer.Issue("blob-ref-4", "application/pdf");

        var result = await controller.GetObject(expiredToken, CancellationToken.None);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
    }
}
