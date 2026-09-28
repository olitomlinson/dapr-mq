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

        return new QueueActorStateReader(mockFactory.Object, "QueueActor", "http://localhost:3500");
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
