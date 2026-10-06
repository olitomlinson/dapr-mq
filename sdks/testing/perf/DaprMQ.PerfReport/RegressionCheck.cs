using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DaprMQ.PerfReport;

public enum ComparisonStatus { Ok, Regressed, Improved, NoBaseline }

public sealed record MetricComparison(string Metric, double? Baseline, double Current, double? DeltaPercent, ComparisonStatus Status);

public sealed record RunComparison(string Profile, string ScenarioKey, int BaselineRuns, IReadOnlyList<MetricComparison> Metrics)
{
    public bool Regressed => Metrics.Any(m => m.Status == ComparisonStatus.Regressed);
}

/// <summary>
/// Compares a run against the median of the last few passing runs of the same SDK, scenario key,
/// environment and API replica count on the baseline branch. Shared CI runners are noisy, so a
/// change only counts once it clears both a relative tolerance and an absolute floor.
/// </summary>
public static class RegressionCheck
{
    public const int Window = 10;
    private const int MinBaselineRuns = 3;

    /// <summary>Below this the peak window (and so utilisation over it) is too short to be signal.</summary>
    private const double MinPeakWindowSeconds = 5;

    private sealed record Rule(string Name, string[] Path, bool HigherIsWorse, double RelativeTolerance, double AbsoluteFloor);

    private static readonly Rule[] SessionDrainRules =
    [
        new("Wall clock (s)", ["wallClockSeconds"], HigherIsWorse: true, 0.25, 2),
        new("Efficiency", ["efficiency"], HigherIsWorse: false, 0.15, 0.03),
        new("Peak utilisation", ["peak", "utilization"], HigherIsWorse: false, 0.15, 0.03),
        new("Claim p95 (ms)", ["claimLatencyMs", "p95"], HigherIsWorse: true, 0.5, 50),
        new("Msg gap p95 (ms)", ["interMessageGapMs", "p95"], HigherIsWorse: true, 0.5, 50),
        new("Delivery p95 (ms)", ["deliveryLatencyMs", "p95"], HigherIsWorse: true, 0.5, 100),
    ];

    /// <summary>P-01..P-03: closed-loop load.</summary>
    private static readonly Rule[] LoadRules =
    [
        new("Messages/s", ["messagesPerSecond"], HigherIsWorse: false, 0.2, 20),
        new("Latency p95 (ms)", ["latencyMs", "p95"], HigherIsWorse: true, 0.5, 5),
        new("Latency p99 (ms)", ["latencyMs", "p99"], HigherIsWorse: true, 1.0, 10),
    ];

    public static RunComparison Compare(JsonObject current, IReadOnlyList<JsonObject> history, string baselineBranch)
    {
        var key = Str(current, "scenario", "key");
        var runId = Str(current, "runId");

        var baseline = history
            .Where(r => Str(r, "runId") != runId
                        && Str(r, "scenario", "key") == key
                        && SeriesKey(r) == SeriesKey(current)
                        && Str(r, "environment", "gitBranch") == baselineBranch
                        && r["checks"]?["passed"]?.GetValue<bool>() != false)
            .TakeLast(Window)
            .ToList();

        var rules = Str(current, "scenario", "name") == "session-drain" ? SessionDrainRules : LoadRules;
        var metrics = new List<MetricComparison>();
        foreach (var rule in rules)
        {
            if (Num(current["metrics"], rule.Path) is not { } value)
            {
                continue; // recorded before this metric existed
            }

            if (rule.Path[0] == "peak" && PeakWindowSeconds(current) is < MinPeakWindowSeconds)
            {
                continue;
            }

            var values = baseline.Select(r => Num(r["metrics"], rule.Path)).OfType<double>().ToList();
            metrics.Add(Assess(rule.Name, value, values, rule.HigherIsWorse, rule.RelativeTolerance, rule.AbsoluteFloor));
        }

        return new RunComparison(Str(current, "scenario", "profile") ?? "", key ?? "", baseline.Count, metrics);
    }

    /// <summary>
    /// Runs are only comparable within one SDK, environment, API replica count and worker count
    /// (0, or absent on older runs, = combined: the API replicas host the actors).
    /// </summary>
    public static string SeriesKey(JsonObject run) =>
        $"{Str(run, "sdk", "name")}|{Str(run, "environment", "label")}|{Num(run["topology"], ["apiReplicas"]) ?? 1}|{Num(run["topology"], ["workers"]) ?? 0}";

    /// <summary>
    /// Judges <paramref name="value"/> against the median of <paramref name="baseline"/>: a change
    /// only counts once it clears both the relative tolerance and the absolute floor.
    /// </summary>
    public static MetricComparison Assess(string name, double value, IReadOnlyList<double> baseline,
        bool higherIsWorse, double relativeTolerance, double absoluteFloor)
    {
        if (baseline.Count < MinBaselineRuns)
        {
            return new MetricComparison(name, null, value, null, ComparisonStatus.NoBaseline);
        }

        var median = Median(baseline);
        var worseBy = higherIsWorse ? value - median : median - value;
        var threshold = Math.Max(relativeTolerance * Math.Abs(median), absoluteFloor);
        var status = worseBy > threshold ? ComparisonStatus.Regressed
            : -worseBy > threshold ? ComparisonStatus.Improved
            : ComparisonStatus.Ok;
        double? delta = median != 0 ? (value - median) / median * 100 : null;

        return new MetricComparison(name, median, value, delta, status);
    }

    public static string ToMarkdown(IEnumerable<RunComparison> comparisons, string baselineBranch,
        string title = "Perf", string firstColumn = "Profile")
    {
        var md = new StringBuilder()
            .AppendLine($"### {title} vs `{baselineBranch}` (median of last ≤{Window} runs)")
            .AppendLine()
            .AppendLine($"| {firstColumn} | Metric | Baseline | This run | Δ | Status |")
            .AppendLine("|---|---|---|---|---|---|");

        foreach (var c in comparisons)
        {
            foreach (var m in c.Metrics)
            {
                var baseline = m.Baseline is { } b ? $"{Fmt(b)} (n={c.BaselineRuns})" : "–";
                var delta = m.DeltaPercent is { } d ? d.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "–";
                var status = m.Status switch
                {
                    ComparisonStatus.Regressed => "❌ regressed",
                    ComparisonStatus.Improved => "🟢 improved",
                    ComparisonStatus.Ok => "✅ ok",
                    _ => "– no baseline",
                };
                md.AppendLine($"| {c.Profile} | {m.Metric} | {baseline} | {Fmt(m.Current)} | {delta} | {status} |");
            }
        }

        return md.ToString();
    }

    private static double? PeakWindowSeconds(JsonObject run) =>
        Num(run["metrics"], ["peak", "endSeconds"]) - Num(run["metrics"], ["peak", "startSeconds"]);

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static string? Str(JsonNode? node, params string[] path) => Walk(node, path)?.GetValue<string>();

    public static double? Num(JsonNode? node, string[] path) => Walk(node, path) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static JsonNode? Walk(JsonNode? node, string[] path)
    {
        foreach (var p in path)
        {
            node = node is JsonObject o ? o[p] : null;
        }

        return node;
    }
}
