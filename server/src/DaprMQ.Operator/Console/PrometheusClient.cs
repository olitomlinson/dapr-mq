using System.Globalization;
using System.Text.Json;

namespace DaprMQ.Operator.Console;

/// <summary>One labelled series: [unixSeconds, value] points (a single point for an instant query).</summary>
public record MetricSeries
{
    public required string Name { get; init; }
    public Dictionary<string, string> Labels { get; init; } = [];
    public List<double[]> Points { get; init; } = [];
}

public record ConsoleMetricsResponse
{
    public List<MetricSeries> Series { get; init; } = [];
}

/// <summary>Runs PromQL against Prometheus' HTTP API. Callers pass only queries built by <see cref="ConsoleMetricQueries"/>.</summary>
public interface IPrometheusClient
{
    Task<List<(Dictionary<string, string> Labels, List<double[]> Points)>> QueryRangeAsync(
        string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step, CancellationToken cancellationToken);

    Task<List<(Dictionary<string, string> Labels, List<double[]> Points)>> QueryAsync(string query, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed class PrometheusClient : IPrometheusClient
{
    private readonly HttpClient _http;

    public PrometheusClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public Task<List<(Dictionary<string, string> Labels, List<double[]> Points)>> QueryRangeAsync(
        string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step, CancellationToken cancellationToken) =>
        GetAsync("api/v1/query_range", new()
        {
            ["query"] = query,
            ["start"] = Seconds(start),
            ["end"] = Seconds(end),
            ["step"] = step.TotalSeconds.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);

    public Task<List<(Dictionary<string, string> Labels, List<double[]> Points)>> QueryAsync(string query, DateTimeOffset at, CancellationToken cancellationToken) =>
        GetAsync("api/v1/query", new() { ["query"] = query, ["time"] = Seconds(at) }, cancellationToken);

    private async Task<List<(Dictionary<string, string> Labels, List<double[]> Points)>> GetAsync(
        string path, Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        var url = path + "?" + string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return Parse(document.RootElement);
    }

    /// <summary>Parses a Prometheus "matrix" or "vector" result.</summary>
    public static List<(Dictionary<string, string> Labels, List<double[]> Points)> Parse(JsonElement root)
    {
        var data = root.GetProperty("data");
        var series = new List<(Dictionary<string, string>, List<double[]>)>();
        foreach (var result in data.GetProperty("result").EnumerateArray())
        {
            var labels = result.GetProperty("metric").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
            var points = result.TryGetProperty("values", out var values)
                ? values.EnumerateArray().Select(Point).ToList()
                : [Point(result.GetProperty("value"))];
            series.Add((labels, points));
        }

        return series;
    }

    // [ <unix seconds>, "<value>" ]; NaN/Inf come through as strings too.
    private static double[] Point(JsonElement pair) =>
        [pair[0].GetDouble(), double.Parse(pair[1].GetString()!, CultureInfo.InvariantCulture)];

    private static string Seconds(DateTimeOffset at) =>
        (at.ToUnixTimeMilliseconds() / 1000.0).ToString(CultureInfo.InvariantCulture);
}
