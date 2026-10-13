using System.Text.Json;
using DaprMQ.Interfaces;
using DaprMQ.Operator.Console;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class OperatorConsoleEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FakeTimeProvider _time = new();
    private readonly Mock<IWorkerAdminClient> _client = new();
    private readonly ConsoleResponseCache _cache;

    public OperatorConsoleEndpointsTests()
    {
        _cache = new ConsoleResponseCache(TimeSpan.FromSeconds(10), _time);
    }

    private static IReadOnlyCollection<KeyValuePair<string, string>> NoQuery => [];

    private void Worker(string path, int status, string body = "{}") =>
        _client.Setup(c => c.GetAsync(path, It.IsAny<IReadOnlyCollection<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkerReply(status, body));

    private static int? StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    // --- proxy ---

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Proxy_PassesWorkerStatusAndBodyThrough(int status)
    {
        Worker("internal/admin/queues/q", status, "{\"queueId\":\"q\"}");

        var result = await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, false, _client.Object, _cache, CancellationToken.None);

        var content = Assert.IsType<ContentHttpResult>(result);
        Assert.Equal(status, content.StatusCode);
        Assert.Equal("{\"queueId\":\"q\"}", content.ResponseContent);
    }

    [Fact]
    public async Task Proxy_WorkerFailure_Is503()
    {
        Worker("internal/admin/queues/q", 500);

        var result = await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, false, _client.Object, _cache, CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
    }

    [Fact]
    public async Task Proxy_TransportFailure_Is503()
    {
        _client.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("sidecar down"));

        var result = await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, true, _client.Object, _cache, CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
    }

    [Fact]
    public async Task Proxy_Cacheable_ServedFromCacheWithinTtl_ThenReread()
    {
        Worker("internal/admin/queues/q", 200);

        await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, true, _client.Object, _cache, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(9));
        await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, true, _client.Object, _cache, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));
        await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, true, _client.Object, _cache, CancellationToken.None);

        _client.Verify(c => c.GetAsync("internal/admin/queues/q", It.IsAny<IReadOnlyCollection<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Proxy_CacheKeyIncludesQuery()
    {
        Worker("internal/admin/catalog", 200);

        await ConsoleEndpoints.ProxyAsync("internal/admin/catalog", [new("kind", "Topic")], true, _client.Object, _cache, CancellationToken.None);
        await ConsoleEndpoints.ProxyAsync("internal/admin/catalog", [new("kind", "Sink")], true, _client.Object, _cache, CancellationToken.None);

        _client.Verify(c => c.GetAsync("internal/admin/catalog", It.IsAny<IReadOnlyCollection<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Proxy_BrowsePages_AreNeverCached()
    {
        Worker("internal/admin/queues/q/messages", 200);

        await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q/messages", NoQuery, false, _client.Object, _cache, CancellationToken.None);
        await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q/messages", NoQuery, false, _client.Object, _cache, CancellationToken.None);

        _client.Verify(c => c.GetAsync("internal/admin/queues/q/messages", It.IsAny<IReadOnlyCollection<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Proxy_Non200_IsNotCached()
    {
        Worker("internal/admin/queues/q", 404);

        await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, true, _client.Object, _cache, CancellationToken.None);
        await ConsoleEndpoints.ProxyAsync("internal/admin/queues/q", NoQuery, true, _client.Object, _cache, CancellationToken.None);

        _client.Verify(c => c.GetAsync("internal/admin/queues/q", It.IsAny<IReadOnlyCollection<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // --- queue list ---

    [Fact]
    public async Task Queues_JoinsCatalogWithDepthAndDeadLetterDepth()
    {
        var catalog = new CatalogPage
        {
            Entries = [new CatalogEntry { Id = "orders", Kind = CatalogEntryKind.Queue }],
            NextCursor = "orders",
            Totals = new() { [CatalogEntryKind.Queue] = 7 }
        };
        Worker("internal/admin/catalog", 200, JsonSerializer.Serialize(catalog, JsonOptions));
        QueueDepthRequest? sent = null;
        _client.Setup(c => c.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>()))
            .Callback((QueueDepthRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync(new QueueDepthResponse
            {
                Results = [new() { QueueId = "orders", Ready = 5, Locked = 2 }, new() { QueueId = "orders-deadletter", Ready = 3, Locked = 1 }]
            });

        var result = await ConsoleEndpoints.HandleQueuesAsync(null, null, null, _client.Object, _cache, CancellationToken.None);

        var page = JsonSerializer.Deserialize<ConsoleQueuePage>(Assert.IsType<ContentHttpResult>(result).ResponseContent!, JsonOptions)!;
        Assert.Equal(new ConsoleQueueRow { Id = "orders", Kind = CatalogEntryKind.Queue, Ready = 5, Locked = 2, DeadLetterCount = 4 }, Assert.Single(page.Items));
        Assert.Equal("orders", page.NextCursor);
        Assert.Equal(7, page.Totals[CatalogEntryKind.Queue]);
        Assert.Equal(["orders", "orders-deadletter"], sent!.Queries.Select(q => q.QueueId));
        _client.Verify(c => c.GetAsync("internal/admin/catalog",
            It.Is<IReadOnlyCollection<KeyValuePair<string, string>>>(q => q.Contains(new("kind", "Queue")) && q.Contains(new("limit", "50"))),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Queues_DeadLetterRows_HaveNoDeadLetterOfTheirOwn()
    {
        var catalog = new CatalogPage { Entries = [new CatalogEntry { Id = "orders-deadletter", Kind = CatalogEntryKind.DeadLetter, ParentId = "orders" }] };
        Worker("internal/admin/catalog", 200, JsonSerializer.Serialize(catalog, JsonOptions));
        _client.Setup(c => c.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDepthResponse { Results = [new() { QueueId = "orders-deadletter", Ready = 3 }] });

        var result = await ConsoleEndpoints.HandleQueuesAsync("deadletter", null, null, _client.Object, _cache, CancellationToken.None);

        var row = Assert.Single(JsonSerializer.Deserialize<ConsoleQueuePage>(Assert.IsType<ContentHttpResult>(result).ResponseContent!, JsonOptions)!.Items);
        Assert.Null(row.DeadLetterCount);
        Assert.Equal("orders", row.ParentId);
    }

    [Fact]
    public async Task Queues_EmptyCatalog_MakesNoDepthCall()
    {
        Worker("internal/admin/catalog", 200, JsonSerializer.Serialize(new CatalogPage(), JsonOptions));

        var result = await ConsoleEndpoints.HandleQueuesAsync(null, null, null, _client.Object, _cache, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        _client.Verify(c => c.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Topic", null)]
    [InlineData("nope", null)]
    [InlineData(null, 0)]
    [InlineData(null, ConsoleEndpoints.MaxQueueListLimit + 1)]
    public async Task Queues_RejectsBadKindOrLimit(string? kind, int? limit)
    {
        var result = await ConsoleEndpoints.HandleQueuesAsync(kind, null, limit, _client.Object, _cache, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result);
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Queues_CatalogFailure_Is503()
    {
        Worker("internal/admin/catalog", 503);

        var result = await ConsoleEndpoints.HandleQueuesAsync(null, null, null, _client.Object, _cache, CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
    }

    // --- overview ---

    private void CatalogPage(string? cursor, CatalogPage page) =>
        _client.Setup(c => c.GetAsync("internal/admin/catalog",
                It.Is<IReadOnlyCollection<KeyValuePair<string, string>>>(q =>
                    q.Contains(new("kind", "Queue")) && (cursor == null ? q.All(p => p.Key != "cursor") : q.Contains(new("cursor", cursor)))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkerReply(200, JsonSerializer.Serialize(page, JsonOptions)));

    private static CatalogPage Queues(string? nextCursor, params string[] ids) => new()
    {
        Entries = ids.Select(id => new CatalogEntry { Id = id, Kind = CatalogEntryKind.Queue }).ToList(),
        NextCursor = nextCursor,
        Totals = new() { [CatalogEntryKind.Queue] = 3, [CatalogEntryKind.Topic] = 1 }
    };

    private void Depths(params QueueDepthResult[] results) =>
        _client.Setup(c => c.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueueDepthRequest r, CancellationToken _) => new QueueDepthResponse
            {
                Results = r.Queries.Select(q => results.FirstOrDefault(d => d.QueueId == q.QueueId) ?? new QueueDepthResult { QueueId = q.QueueId }).ToList()
            });

    private static ConsoleOverview OverviewOf(IResult result) =>
        JsonSerializer.Deserialize<ConsoleOverview>(Assert.IsType<ContentHttpResult>(result).ResponseContent!, JsonOptions)!;

    [Fact]
    public async Task Overview_SumsDepths_AndRanksTopQueuesAndDeadLetterQueues()
    {
        CatalogPage(null, Queues(null, "emails", "idle", "orders"));
        Depths(
            new() { QueueId = "orders", Ready = 8, Locked = 2 },
            new() { QueueId = "orders-deadletter", Ready = 3 },
            new() { QueueId = "emails", Ready = 1, Locked = 1 },
            new() { QueueId = "emails-deadletter", Ready = 5, Locked = 1 });

        var overview = OverviewOf(await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None));

        Assert.Equal(9, overview.Ready);
        Assert.Equal(3, overview.Locked);
        Assert.Equal(9, overview.DeadLettered);
        Assert.Equal(2, overview.DeadLetterQueuesWithMessages);
        Assert.Equal(["orders", "emails"], overview.TopQueues.Select(q => q.Id));
        Assert.Equal(["emails", "orders"], overview.DeadLetterQueues.Select(q => q.Id));
        Assert.Equal(6, overview.DeadLetterQueues[0].Ready + overview.DeadLetterQueues[0].Locked);
        Assert.Equal(3, overview.QueuesRead);
        Assert.Equal(1, overview.Totals[CatalogEntryKind.Topic]);
        Assert.False(overview.Truncated);
    }

    [Fact]
    public async Task Overview_PagesThroughTheWholeCatalog()
    {
        CatalogPage(null, Queues("b", "a", "b"));
        CatalogPage("b", Queues(null, "c"));
        Depths(new QueueDepthResult { QueueId = "c", Ready = 4 });

        var overview = OverviewOf(await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None));

        Assert.Equal(3, overview.QueuesRead);
        Assert.Equal(4, overview.Ready);
    }

    [Fact]
    public async Task Overview_SplitsDepthReadsToTheEndpointLimit()
    {
        var ids = Enumerable.Range(0, 120).Select(i => $"q{i:D3}").ToArray();
        CatalogPage(null, Queues(null, ids));
        var sent = new List<int>();
        _client.Setup(c => c.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>()))
            .Callback((QueueDepthRequest r, CancellationToken _) => { lock (sent) sent.Add(r.Queries.Count); })
            .ReturnsAsync((QueueDepthRequest r, CancellationToken _) => new QueueDepthResponse
            {
                Results = r.Queries.Select(q => new QueueDepthResult { QueueId = q.QueueId }).ToList()
            });

        await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None);

        Assert.Equal(240, sent.Sum());
        Assert.All(sent, count => Assert.True(count <= 100));
    }

    [Fact]
    public async Task Overview_StopsAtTheQueueCap_AndSaysSo()
    {
        CatalogPage(null, Queues("b", "a", "b"));
        Depths();

        var overview = OverviewOf(await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None, maxQueues: 2));

        Assert.True(overview.Truncated);
        Assert.Equal(2, overview.QueuesRead);
        _client.Verify(c => c.GetAsync("internal/admin/catalog",
            It.Is<IReadOnlyCollection<KeyValuePair<string, string>>>(q => q.Contains(new("limit", "2"))), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task Overview_CountsFailedDepthReads_WithoutFailing()
    {
        CatalogPage(null, Queues(null, "bad", "good"));
        Depths(new() { QueueId = "bad", Error = "boom" }, new() { QueueId = "good", Ready = 2 });

        var overview = OverviewOf(await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None));

        Assert.Equal(1, overview.Errors);
        Assert.Equal(2, overview.Ready);
    }

    [Fact]
    public async Task Overview_IsCachedForEveryCaller()
    {
        CatalogPage(null, Queues(null, "orders"));
        Depths();

        await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None);
        await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None);

        _client.Verify(c => c.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Overview_CatalogFailure_Is503()
    {
        Worker("internal/admin/catalog", 503);

        var result = await ConsoleEndpoints.HandleOverviewAsync(_client.Object, _cache, CancellationToken.None);

        Assert.Equal(503, StatusOf(result));
    }

    // --- concurrency limit ---

    [Fact]
    public async Task ConcurrencyLimit_HoldsCallsBeyondTheCap()
    {
        var inner = new Mock<IWorkerAdminClient>();
        var release = new TaskCompletionSource<WorkerReply>();
        var inFlight = 0;
        var maxInFlight = 0;
        inner.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                var reply = await release.Task;
                Interlocked.Decrement(ref inFlight);
                return reply;
            });
        var limited = new ConcurrencyLimitedWorkerAdminClient(inner.Object, 8);

        var calls = Enumerable.Range(0, 20).Select(_ => limited.GetAsync("p", [], CancellationToken.None)).ToList();
        await Task.Delay(50);
        Assert.Equal(8, Volatile.Read(ref inFlight));

        release.SetResult(new WorkerReply(200, "{}"));
        await Task.WhenAll(calls);
        Assert.Equal(8, maxInFlight);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
