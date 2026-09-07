using System.Net;
using System.Text.Json;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Moq;
using Moq.Protected;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Verifies HttpSinkActor delivers a reference (not dereferenced content) for offloaded items,
/// so a poll's HTTP POST body stays small regardless of the underlying object's size.
/// </summary>
public class HttpSinkActorBlobOffloadTests
{
    private Mock<IActorStateManager> CreateMockStateManager(Dictionary<string, object> stateData)
    {
        var mock = new Mock<IActorStateManager>();

        mock.Setup(m => m.GetStateAsync<HttpSinkActorState>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken ct) => (HttpSinkActorState)stateData[key]);

        mock.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string key, object value, CancellationToken ct) => { stateData[key] = value; return Task.CompletedTask; });

        mock.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        return mock;
    }

    private static string BlobRefJson(string reference, string? contentType) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["__daprmq_blob_ref__"] = reference,
            ["contentType"] = contentType
        });

    [Fact]
    public async Task PollAndDeliver_BlobRefItem_SendsReferenceNotContent()
    {
        var initialState = new HttpSinkActorState
        {
            Url = "http://test.com/webhook",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            CurrentIntervalSeconds = 1
        };
        var stateData = new Dictionary<string, object> { ["sink-state"] = initialState };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(), "PopWithAck", It.IsAny<PopWithAckRequest>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                Items = new List<PopWithAckItem>
                {
                    new()
                    {
                        ItemJson = BlobRefJson("test-queue/obj-123", "application/pdf"),
                        LockId = "lock-1",
                        Priority = 1,
                        LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
                        ObjectClaimToken = "claim-token-123",
                        BlobContentType = "application/pdf"
                    }
                },
                IsEmpty = false,
                MaxConcurrencyReached = false
            });

        string? capturedBody = null;
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedBody = req.Content!.ReadAsStringAsync().Result)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var mockHttpClient = new HttpClient(mockHttpMessageHandler.Object);
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(mockHttpClient);

        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(), "Acknowledge", It.IsAny<AcknowledgeRequest>()))
            .ReturnsAsync(new AcknowledgeResponse { Success = true });

        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<HttpSinkActor>(testOptions);
        var actor = new HttpSinkActor(actorHost, mockQueueInvoker.Object, mockHttpClientFactory.Object);
        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.NotNull(capturedBody);
        using var document = JsonDocument.Parse(capturedBody!);
        var deliveredItem = document.RootElement[0].GetProperty("item");

        // Must NOT contain the raw envelope sentinel, raw blob reference, or full object content -
        // only an opaque claim token the webhook can redeem separately via GET /object/{token}.
        Assert.False(deliveredItem.TryGetProperty("__daprmq_blob_ref__", out _));
        Assert.False(deliveredItem.TryGetProperty("blobReference", out _));
        Assert.Equal("claim-token-123", deliveredItem.GetProperty("objectClaimToken").GetString());
        Assert.Equal("application/pdf", deliveredItem.GetProperty("contentType").GetString());

        // 200 OK acknowledges every item in the batch, including blob-reference items - the
        // backstop reap TTL (scheduled on Acknowledge) gives the endpoint time to fetch the
        // object via GET /object/{token} before the reaper deletes it.
        mockQueueInvoker.Verify(m => m.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
            It.IsAny<ActorId>(), "Acknowledge", It.Is<AcknowledgeRequest>(r => r.LockId == "lock-1")), Times.Once);
    }

    [Fact]
    public async Task PollAndDeliver_MixedBatchOn200_AcknowledgesAllItemsIncludingBlobItems()
    {
        var initialState = new HttpSinkActorState
        {
            Url = "http://test.com/webhook",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            CurrentIntervalSeconds = 1
        };
        var stateData = new Dictionary<string, object> { ["sink-state"] = initialState };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(), "PopWithAck", It.IsAny<PopWithAckRequest>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                Items = new List<PopWithAckItem>
                {
                    new()
                    {
                        ItemJson = "{\"task\":\"normal\"}",
                        LockId = "lock-normal",
                        Priority = 1,
                        LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
                    },
                    new()
                    {
                        ItemJson = BlobRefJson("test-queue/obj-456", "image/png"),
                        LockId = "lock-blob",
                        Priority = 1,
                        LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds(),
                        ObjectClaimToken = "claim-token-456",
                        BlobContentType = "image/png"
                    }
                },
                IsEmpty = false,
                MaxConcurrencyReached = false
            });

        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var mockHttpClient = new HttpClient(mockHttpMessageHandler.Object);
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(mockHttpClient);

        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(), "Acknowledge", It.IsAny<AcknowledgeRequest>()))
            .ReturnsAsync(new AcknowledgeResponse { Success = true });

        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<HttpSinkActor>(testOptions);
        var actor = new HttpSinkActor(actorHost, mockQueueInvoker.Object, mockHttpClientFactory.Object);
        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        mockQueueInvoker.Verify(m => m.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
            It.IsAny<ActorId>(), "Acknowledge",
            It.Is<AcknowledgeRequest>(r => r.LockId == "lock-normal")), Times.Once);
        mockQueueInvoker.Verify(m => m.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
            It.IsAny<ActorId>(), "Acknowledge",
            It.Is<AcknowledgeRequest>(r => r.LockId == "lock-blob")), Times.Once);
    }

    [Fact]
    public async Task PollAndDeliver_NormalItem_SendsInlineContentUnchanged()
    {
        var initialState = new HttpSinkActorState
        {
            Url = "http://test.com/webhook",
            QueueActorId = "test-queue",
            MaxConcurrency = 10,
            LockTtlSeconds = 30,
            CurrentIntervalSeconds = 1
        };
        var stateData = new Dictionary<string, object> { ["sink-state"] = initialState };
        var mockStateManager = CreateMockStateManager(stateData);
        var mockQueueInvoker = new Mock<IQueueActorInvoker>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockTimerManager = new Mock<ActorTimerManager>();

        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                It.IsAny<ActorId>(), "PopWithAck", It.IsAny<PopWithAckRequest>()))
            .ReturnsAsync(new PopWithAckResponse
            {
                Items = new List<PopWithAckItem>
                {
                    new()
                    {
                        ItemJson = "{\"task\":\"send_email\"}",
                        LockId = "lock-2",
                        Priority = 1,
                        LockExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
                    }
                },
                IsEmpty = false,
                MaxConcurrencyReached = false
            });

        string? capturedBody = null;
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedBody = req.Content!.ReadAsStringAsync().Result)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var mockHttpClient = new HttpClient(mockHttpMessageHandler.Object);
        mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(mockHttpClient);

        mockQueueInvoker.Setup(m => m.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                It.IsAny<ActorId>(), "Acknowledge", It.IsAny<AcknowledgeRequest>()))
            .ReturnsAsync(new AcknowledgeResponse { Success = true });

        mockTimerManager.Setup(m => m.RegisterReminderAsync(It.IsAny<ActorReminder>())).Returns(Task.CompletedTask);

        var testOptions = new ActorTestOptions { TimerManager = mockTimerManager.Object };
        var actorHost = ActorHost.CreateForTest<HttpSinkActor>(testOptions);
        var actor = new HttpSinkActor(actorHost, mockQueueInvoker.Object, mockHttpClientFactory.Object);
        typeof(Actor).GetProperty("StateManager")?.SetValue(actor, mockStateManager.Object);

        await actor.ReceiveReminderAsync("sink-poll", null!, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        Assert.NotNull(capturedBody);
        using var document = JsonDocument.Parse(capturedBody!);
        var deliveredItem = document.RootElement[0].GetProperty("item");
        Assert.Equal("send_email", deliveredItem.GetProperty("task").GetString());
    }
}
