using System.Net;
using DaprMQ.ApiServer.Services;
using Moq;
using Moq.Protected;

namespace DaprMQ.Tests;

public class PlacementStateClientTests
{
    private const string Tables = """
        {"tables":{"default":{"version":7,"hosts":[
          {"name":"10.0.0.1:50002","id":"rel-daprmq-worker","namespace":"default","entities":["rel-daprmq-QueueActor","rel-daprmq-TopicActor"],"api_level":20},
          {"name":"10.0.0.2:50002","id":"rel-daprmq-worker","namespace":"default","entities":["rel-daprmq-QueueActor"],"api_level":20},
          {"name":"10.0.0.3:50002","id":"rel-daprmq-gateway","namespace":"default","entities":[],"api_level":20},
          {"name":"10.0.0.4:50002","id":"rel-daprmq-worker","namespace":"default","entities":["rel-daprmq-TopicActor"],"api_level":20}
        ]},"other":{"version":2,"hosts":[
          {"name":"10.0.9.1:50002","id":"rel-daprmq-worker","namespace":"other","entities":["rel-daprmq-QueueActor"],"api_level":20}
        ]}}}
        """;

    private static PlacementStateClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        out Mock<HttpMessageHandler> handler,
        string address = "placement:8080",
        IPAddress[]? resolved = null)
    {
        handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(send);

        var h = handler.Object;
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(h));

        return new PlacementStateClient(factory.Object, address, TimeSpan.FromMilliseconds(200),
            (_, _) => Task.FromResult(resolved ?? [IPAddress.Parse("10.1.0.1")]));
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(string json) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

    [Fact]
    public async Task FiltersByAppIdActorTypeAndNamespace()
    {
        var client = CreateClient(Respond(Tables), out var handler);

        var hosts = await client.GetHostsAsync("rel-daprmq-worker", "rel-daprmq-QueueActor", "default", CancellationToken.None);

        Assert.Equal([IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2")], hosts);
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString() == "http://10.1.0.1:8080/placement/state"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task NoNamespace_MatchesAcrossTables()
    {
        var client = CreateClient(Respond(Tables), out _);

        var hosts = await client.GetHostsAsync("rel-daprmq-worker", "rel-daprmq-QueueActor", null, CancellationToken.None);

        Assert.Equal(3, hosts!.Count);
    }

    [Fact]
    public async Task NoMatchingHosts_ReturnsEmpty()
    {
        var client = CreateClient(Respond(Tables), out _);

        var hosts = await client.GetHostsAsync("missing", "rel-daprmq-QueueActor", null, CancellationToken.None);

        Assert.NotNull(hosts);
        Assert.Empty(hosts);
    }

    [Fact]
    public async Task FollowerRejects_TriesNextAddress()
    {
        var client = CreateClient((r, _) => Task.FromResult(r.RequestUri!.Host == "10.1.0.1"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Tables) }),
            out _, resolved: [IPAddress.Parse("10.1.0.1"), IPAddress.Parse("10.1.0.2")]);

        var hosts = await client.GetHostsAsync("rel-daprmq-worker", "rel-daprmq-QueueActor", "default", CancellationToken.None);

        Assert.Equal(2, hosts!.Count);
    }

    [Fact]
    public async Task AllAddressesFailOrHang_ReturnsNull()
    {
        var client = CreateClient(async (r, ct) =>
            {
                if (r.RequestUri!.Host == "10.1.0.1")
                {
                    throw new HttpRequestException("refused");
                }

                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            out _, resolved: [IPAddress.Parse("10.1.0.1"), IPAddress.Parse("10.1.0.2")]);

        var hosts = await client.GetHostsAsync("rel-daprmq-worker", "rel-daprmq-QueueActor", null, CancellationToken.None);

        Assert.Null(hosts);
    }

    [Fact]
    public async Task MalformedJson_ReturnsNull()
    {
        var client = CreateClient(Respond("not json"), out _);

        var hosts = await client.GetHostsAsync("rel-daprmq-worker", "rel-daprmq-QueueActor", null, CancellationToken.None);

        Assert.Null(hosts);
    }

    [Fact]
    public async Task Ipv6HostNames_AreParsed()
    {
        var client = CreateClient(Respond("""
            {"tables":{"":{"hosts":[{"name":"[fd00::5]:50002","id":"w","entities":["Q"]}]}}}
            """), out _);

        var hosts = await client.GetHostsAsync("w", "Q", null, CancellationToken.None);

        Assert.Equal([IPAddress.Parse("fd00::5")], hosts);
    }

    [Fact]
    public async Task IpLiteralAddress_SkipsResolution()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(Respond(Tables));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler.Object));
        var client = new PlacementStateClient(factory.Object, "127.0.0.1:9090", TimeSpan.FromSeconds(1),
            (_, _) => throw new InvalidOperationException("should not resolve"));

        var hosts = await client.GetHostsAsync("rel-daprmq-worker", "rel-daprmq-QueueActor", "default", CancellationToken.None);

        Assert.Equal(2, hosts!.Count);
    }
}
