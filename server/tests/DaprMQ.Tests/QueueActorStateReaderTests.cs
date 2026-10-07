using System.Net;
using Dapr.Actors;
using Moq;
using Moq.Protected;

namespace DaprMQ.Tests;

public class QueueActorStateReaderTests
{
    private static QueueActorStateReader CreateReader(HttpResponseMessage response, out Mock<HttpMessageHandler> mockHandler)
    {
        mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var httpClient = new HttpClient(mockHandler.Object);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        return new QueueActorStateReader(mockFactory.Object, "QueueActor", "http://localhost:3500", "SessionCoordinatorActor");
    }

    private static HttpResponseMessage JsonResponse(ActorMetadata metadata)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(metadata);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task ReadSessionStateAsync_AllQueuesZeroCount_ReportsEmpty()
    {
        var metadata = new ActorMetadata
        {
            Queues = new Dictionary<int, QueueMetadata>
            {
                [0] = new QueueMetadata { Count = 0 },
                [1] = new QueueMetadata { Count = 0 }
            }
        };
        var reader = CreateReader(JsonResponse(metadata), out _);

        var result = await reader.ReadSessionStateAsync(new ActorId("orders-session-42"));

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task ReadSessionStateAsync_NonZeroCount_ReportsNotEmpty()
    {
        var metadata = new ActorMetadata
        {
            Queues = new Dictionary<int, QueueMetadata>
            {
                [0] = new QueueMetadata { Count = 3 }
            }
        };
        var reader = CreateReader(JsonResponse(metadata), out _);

        var result = await reader.ReadSessionStateAsync(new ActorId("orders-session-42"));

        Assert.False(result.IsEmpty);
    }

    [Fact]
    public async Task ReadSessionStateAsync_NoQueuesAtAll_ReportsEmpty()
    {
        var metadata = new ActorMetadata { Queues = new Dictionary<int, QueueMetadata>() };
        var reader = CreateReader(JsonResponse(metadata), out _);

        var result = await reader.ReadSessionStateAsync(new ActorId("orders-session-42"));

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task ReadSessionStateAsync_OutstandingLocksOnly_ReportsNotEmpty()
    {
        // A session whose consumer died holds its items inside locks, not in the queues. Reporting it
        // empty would let the coordinator evict its directory entry and make those items
        // undiscoverable - nothing could ever claim the session again to get them back.
        var metadata = new ActorMetadata
        {
            Queues = new Dictionary<int, QueueMetadata>(),
            LockCount = 2,
            ActiveSessionLeaseId = "lease-1",
            ActiveSessionLeaseExpiresAt = 1000
        };
        var reader = CreateReader(JsonResponse(metadata), out _);

        var result = await reader.ReadSessionStateAsync(new ActorId("orders-session-42"));

        Assert.False(result.IsEmpty);
        Assert.Equal(0, result.ItemCount);
        Assert.Equal(2, result.LockCount);
        Assert.Equal(1000, result.LeaseExpiresAt);
    }

    [Fact]
    public async Task ReadSessionStateAsync_NonSuccessStatusCode_Throws()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var reader = CreateReader(response, out _);

        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadSessionStateAsync(new ActorId("orders-session-42")));
    }

    [Fact]
    public async Task ReadSessionStateAsync_EmptyBody_Throws()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var reader = CreateReader(response, out _);

        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadSessionStateAsync(new ActorId("orders-session-42")));
    }

    [Fact]
    public async Task ReadQueueDepthAsync_SumsReadyAcrossPrioritiesAndReportsLocked()
    {
        var metadata = new ActorMetadata
        {
            Queues = new Dictionary<int, QueueMetadata>
            {
                [0] = new QueueMetadata { Count = 2 },
                [1] = new QueueMetadata { Count = 5 }
            },
            LockCount = 3
        };
        var reader = CreateReader(JsonResponse(metadata), out _);

        var depth = await reader.ReadQueueDepthAsync(new ActorId("orders"));

        Assert.Equal(new QueueDepth(7, 3), depth);
    }

    [Fact]
    public async Task ReadQueueDepthAsync_NoContent_ReportsZero()
    {
        // Dapr answers 204 for a key that was never written - a queue nothing has enqueued to yet
        // has no depth, unlike the session sweep where a missing key is "unknown".
        var reader = CreateReader(new HttpResponseMessage(HttpStatusCode.NoContent), out _);

        var depth = await reader.ReadQueueDepthAsync(new ActorId("never-used"));

        Assert.Equal(new QueueDepth(0, 0), depth);
    }

    [Fact]
    public async Task ReadQueueDepthAsync_NonSuccessStatusCode_Throws()
    {
        var reader = CreateReader(new HttpResponseMessage(HttpStatusCode.InternalServerError), out _);

        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadQueueDepthAsync(new ActorId("orders")));
    }

    [Fact]
    public async Task ReadSessionDirectoryAsync_ReturnsDirectorySessionIds()
    {
        var coordinator = new SessionCoordinatorMetadata
        {
            SessionDirectory = new Dictionary<string, SweepCandidate>
            {
                ["a"] = new SweepCandidate(),
                ["b"] = new SweepCandidate()
            }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(coordinator);
        var reader = CreateReader(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        }, out var mockHandler);

        var sessions = await reader.ReadSessionDirectoryAsync("orders");

        Assert.Equal(new[] { "a", "b" }, sessions.OrderBy(s => s));
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri!.ToString() == "http://localhost:3500/v1.0/actors/SessionCoordinatorActor/orders/state/metadata"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ReadSessionDirectoryAsync_NoContent_ReturnsEmpty()
    {
        var reader = CreateReader(new HttpResponseMessage(HttpStatusCode.NoContent), out _);

        var sessions = await reader.ReadSessionDirectoryAsync("orders");

        Assert.Empty(sessions);
    }

    [Fact]
    public async Task ReadSessionStateAsync_RequestsExpectedUrl()
    {
        var metadata = new ActorMetadata { Queues = new Dictionary<int, QueueMetadata>() };
        var reader = CreateReader(JsonResponse(metadata), out var mockHandler);

        await reader.ReadSessionStateAsync(new ActorId("orders-session-42"));

        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri!.ToString() == "http://localhost:3500/v1.0/actors/QueueActor/orders-session-42/state/metadata"),
            ItExpr.IsAny<CancellationToken>());
    }
}
