using System.Text.Json;
using DaprMQ.Operator.Console;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class PrometheusQueryClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly Mock<IPrometheusClient> _prometheus = new();
    private readonly ConsoleResponseCache _cache;

    public PrometheusQueryClientTests()
    {
        _cache = new ConsoleResponseCache(TimeSpan.FromSeconds(10), _time);
        _prometheus.Setup(p => p.QueryRangeAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new Dictionary<string, string>(), [[1.0, 2.0]])]);
        _prometheus.Setup(p => p.QueryAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new Dictionary<string, string> { ["queue"] = "orders" }, [[3.0, 4.0]])]);
    }

    private Task<IResult> Handle(string name, string? id = null, string? ids = null, string? range = null, IPrometheusClient? client = null) =>
        ConsoleEndpoints.HandleMetricsAsync(name, id, ids, range, client ?? _prometheus.Object, _cache, _time, CancellationToken.None);

    private static int? StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode;

    [Fact]
    public async Task NotConfigured_Is501()
    {
        var result = await ConsoleEndpoints.HandleMetricsAsync("throughput", null, null, null, null, _cache, _time, CancellationToken.None);

        Assert.Equal(501, StatusOf(result));
    }

    [Theory]
    [InlineData("rate(anything[1m])", null, null, null)]
    [InlineData("queue", null, null, null)]
    [InlineData("topic", null, null, null)]
    [InlineData("throughput", null, null, "2d")]
    public async Task OnlyNamedQueriesWithTheirInputs(string name, string? id, string? ids, string? range)
    {
        var result = await Handle(name, id, ids, range);

        Assert.IsType<BadRequest<string>>(result);
        _prometheus.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TooManyIds_Is400()
    {
        var ids = string.Join(",", Enumerable.Range(0, ConsoleMetricQueries.MaxIds + 1).Select(i => $"q{i}"));

        Assert.IsType<BadRequest<string>>(await Handle("queue-list", ids: ids));
    }

    [Fact]
    public async Task RangeQueries_SpanTheRangeIn120Steps_InstantQueriesRunAtNow()
    {
        var result = await Handle("queue", id: "orders", range: "6h");

        var response = JsonSerializer.Deserialize<ConsoleMetricsResponse>(Assert.IsType<ContentHttpResult>(result).ResponseContent!, JsonOptions)!;
        Assert.Equal(["enqueued", "dequeued", "acknowledged", "nacked", "lock_expired", "deadlettered", "ready", "locked", "dlq"],
            response.Series.Select(s => s.Name));
        _prometheus.Verify(p => p.QueryRangeAsync(It.IsAny<string>(), Now - TimeSpan.FromHours(6), Now, TimeSpan.FromMinutes(3), It.IsAny<CancellationToken>()),
            Times.Exactly(9));

        await Handle("depth-summary");
        _prometheus.Verify(p => p.QueryAsync(It.IsAny<string>(), Now, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Results_AreCachedForEveryone()
    {
        await Handle("throughput");
        await Handle("throughput");

        _prometheus.Verify(p => p.QueryRangeAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task PrometheusDown_Is503()
    {
        var down = new Mock<IPrometheusClient>();
        down.Setup(p => p.QueryRangeAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        Assert.Equal(503, StatusOf(await Handle("throughput", client: down.Object)));
    }

    [Fact]
    public void Ids_AreEscapedIntoLabelMatchers()
    {
        var single = ConsoleMetricQueries.Build("queue", "a\"} or vector(1) #", [], "120s")!;
        var many = ConsoleMetricQueries.Build("queue-list", null, ["orders", "a.b|c"], "120s")!;

        Assert.All(single, q => Assert.Contains("queue=\"a\\\"} or vector(1) #\"", q.PromQl));
        Assert.Contains("queue=~\"orders|a\\\\.b\\\\|c\"", many[0].PromQl);
    }

    [Fact]
    public void Depth_KeepsOnlyTheFreshestReporterPerQueue()
    {
        var promql = ConsoleMetricQueries.FreshDepth("kind=\"queue\"", "ready");

        Assert.Equal(
            "(daprmq_queue_depth{kind=\"queue\",state=\"ready\"} and on (queue, kind, instance) " +
            "(daprmq_queue_depth_updated{kind=\"queue\"} == on (queue, kind) group_left max by (queue, kind) (daprmq_queue_depth_updated{kind=\"queue\"})))",
            promql);
    }

    [Fact]
    public void Parse_ReadsMatrixAndVectorResults()
    {
        var matrix = JsonDocument.Parse("""{"status":"success","data":{"resultType":"matrix","result":[{"metric":{"queue":"a"},"values":[[1,"2"],[3,"NaN"]]}]}}""");
        var vector = JsonDocument.Parse("""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[5,"6.5"]}]}}""");

        var m = Assert.Single(PrometheusClient.Parse(matrix.RootElement));
        var v = Assert.Single(PrometheusClient.Parse(vector.RootElement));

        Assert.Equal("a", m.Labels["queue"]);
        Assert.Equal(2, m.Points[0][1]);
        Assert.True(double.IsNaN(m.Points[1][1]));
        Assert.Equal([5.0, 6.5], v.Points[0]);
    }
}
