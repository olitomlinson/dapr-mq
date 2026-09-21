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
    public async Task IsSessionEmptyAsync_AllQueuesZeroCount_ReturnsTrue()
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

        var result = await reader.IsSessionEmptyAsync(new ActorId("orders-session-42"));

        Assert.True(result);
    }

    [Fact]
    public async Task IsSessionEmptyAsync_NonZeroCount_ReturnsFalse()
    {
        var metadata = new ActorMetadata
        {
            Queues = new Dictionary<int, QueueMetadata>
            {
                [0] = new QueueMetadata { Count = 3 }
            }
        };
        var reader = CreateReader(JsonResponse(metadata), out _);

        var result = await reader.IsSessionEmptyAsync(new ActorId("orders-session-42"));

        Assert.False(result);
    }

    [Fact]
    public async Task IsSessionEmptyAsync_NoQueuesAtAll_ReturnsTrue()
    {
        var metadata = new ActorMetadata { Queues = new Dictionary<int, QueueMetadata>() };
        var reader = CreateReader(JsonResponse(metadata), out _);

        var result = await reader.IsSessionEmptyAsync(new ActorId("orders-session-42"));

        Assert.True(result);
    }

    [Fact]
    public async Task IsSessionEmptyAsync_NonSuccessStatusCode_Throws()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var reader = CreateReader(response, out _);

        await Assert.ThrowsAnyAsync<Exception>(() => reader.IsSessionEmptyAsync(new ActorId("orders-session-42")));
    }

    [Fact]
    public async Task IsSessionEmptyAsync_EmptyBody_Throws()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty)
        };
        var reader = CreateReader(response, out _);

        await Assert.ThrowsAnyAsync<Exception>(() => reader.IsSessionEmptyAsync(new ActorId("orders-session-42")));
    }

    [Fact]
    public async Task IsSessionEmptyAsync_RequestsExpectedUrl()
    {
        var metadata = new ActorMetadata { Queues = new Dictionary<int, QueueMetadata>() };
        var reader = CreateReader(JsonResponse(metadata), out var mockHandler);

        await reader.IsSessionEmptyAsync(new ActorId("orders-session-42"));

        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r =>
                r.RequestUri!.ToString() == "http://localhost:3500/v1.0/actors/QueueActor/orders-session-42/state/metadata"),
            ItExpr.IsAny<CancellationToken>());
    }
}
