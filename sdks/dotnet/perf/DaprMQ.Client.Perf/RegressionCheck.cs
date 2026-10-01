using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace DaprMQ.Client.Perf;

public enum ComparisonStatus { Ok, Regressed, Improved, NoBaseline }

public sealed record MetricComparison(string Metric, double? Baseline, double Current, double? DeltaPercent, ComparisonStatus Status);

public sealed record RunComparison(string Profile, string ScenarioKey, int BaselineRuns, IReadOnlyList<MetricComparison> Metrics)
{
    public bool Regressed => Metrics.Any(m => m.Status == ComparisonStatus.Regressed);
}

/// <summary>
/// Compares a run against the median of the last few runs of the same scenario, in the same
/// environment, on the baseline branch. Shared CI runners are noisy, so a change only counts once
/// it clears both a relative tolerance and an absolute floor.
/// </summary>
public static class RegressionCheck
{
    private const int Window = 10;
    private const int MinBaselineRuns = 3;

    /// <summary>Below this the peak window (and so utilisation over it) is too short to be signal.</summary>
    private const double MinPeakWindowSeconds = 5;

    private sealed record Rule(string Name, string[] Path, bool HigherIsWorse, double RelativeTolerance, double AbsoluteFloor);

    private static readonly Rule[] Rules =
    [
        new("Wall clock (s)", ["wallClockSeconds"], HigherIsWorse: true, 0.25, 2),
        new("Efficiency", ["efficiency"], HigherIsWorse: false, 0.15, 0.03),
        new("Peak utilisation", ["peak", "utilization"], HigherIsWorse: false, 0.15, 0.03),
        new("Claim p95 (ms)", ["claimLatencyMs", "p95"], HigherIsWorse: true, 0.5, 50),
        new("Msg gap p95 (ms)", ["interMessageGapMs", "p95"], HigherIsWorse: true, 0.5, 50),
        new("Delivery p95 (ms)", ["deliveryLatencyMs", "p95"], HigherIsWorse: true, 0.5, 100),
    ];

    public static RunComparison Compare(JsonObject current, IReadOnlyList<JsonObject> history, string baselineBranch)
    {
        var key = Str(current, "scenario", "key");
        var env = Str(current, "environment", "label");
        var runId = Str(current, "runId");

        var baseline = history
            .Where(r => Str(r, "runId") != runId
                        && Str(r, "scenario", "key") == key
                        && Str(r, "environment", "label") == env
                        && Str(r, "environment", "gitBranch") == baselineBranch)
            .TakeLast(Window)
            .ToList();

        var metrics = new List<MetricComparison>();
        foreach (var rule in Rules)
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
            if (values.Count < MinBaselineRuns)
            {
                metrics.Add(new MetricComparison(rule.Name, null, value, null, ComparisonStatus.NoBaseline));
                continue;
            }

            var median = Median(values);
            var worseBy = rule.HigherIsWorse ? value - median : median - value;
            var threshold = Math.Max(rule.RelativeTolerance * Math.Abs(median), rule.AbsoluteFloor);
            var status = worseBy > threshold ? ComparisonStatus.Regressed
                : -worseBy > threshold ? ComparisonStatus.Improved
                : ComparisonStatus.Ok;
            double? delta = median != 0 ? (value - median) / median * 100 : null;

            metrics.Add(new MetricComparison(rule.Name, median, value, delta, status));
        }

        return new RunComparison(Str(current, "profile") ?? "", key ?? "", baseline.Count, metrics);
    }

    public static string ToMarkdown(IEnumerable<RunComparison> comparisons, string baselineBranch)
    {
        var md = new StringBuilder()
            .AppendLine($"### Session drain perf vs `{baselineBranch}` (median of last ≤{Window} runs)")
            .AppendLine()
            .AppendLine("| Profile | Metric | Baseline | This run | Δ | Status |")
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

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static string? Str(JsonNode? node, params string[] path) => Walk(node, path)?.GetValue<string>();

    private static double? Num(JsonNode? node, string[] path) => Walk(node, path) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static JsonNode? Walk(JsonNode? node, string[] path)
    {
        foreach (var p in path)
        {
            node = node is JsonObject o ? o[p] : null;
        }

        return node;
    }
}
