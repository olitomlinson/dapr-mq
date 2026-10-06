using System.Text.Json.Nodes;

namespace DaprMQ.PerfReport.Tests;

public class RegressionCheckTests
{
    private static JsonObject Run(string runId, string branch, double wall, double efficiency = 0.9, double claimP95 = 100,
        string key = "10x10@100ms/4slots", string env = "ci", string sdk = "dotnet", int apiReplicas = 1, bool passed = true) =>
        JsonNode.Parse($$"""
        {
          "schemaVersion": 2, "runId": "{{runId}}",
          "sdk": { "name": "{{sdk}}" },
          "environment": { "label": "{{env}}", "gitBranch": "{{branch}}" },
          "topology": { "apiReplicas": {{apiReplicas}} },
          "scenario": { "id": "P-04", "name": "session-drain", "profile": "steady-drain", "key": "{{key}}" },
          "metrics": {
            "wallClockSeconds": {{wall}}, "efficiency": {{efficiency}},
            "peak": { "utilization": 0.9 },
            "claimLatencyMs": { "p95": {{claimP95}} },
            "interMessageGapMs": { "p95": 5 },
            "deliveryLatencyMs": { "p95": 200 }
          },
          "checks": { "passed": {{(passed ? "true" : "false")}} }
        }
        """)!.AsObject();

    private static JsonObject LoadRun(string runId, string branch, double messagesPerSecond, double p95 = 10, double p99 = 20) =>
        JsonNode.Parse($$"""
        {
          "schemaVersion": 2, "runId": "{{runId}}",
          "sdk": { "name": "python" },
          "environment": { "label": "ci", "gitBranch": "{{branch}}" },
          "topology": { "apiReplicas": 1 },
          "scenario": { "id": "P-01", "name": "enqueue", "profile": "enqueue", "key": "enqueue:c8/q8/b1/256B/3+15s" },
          "metrics": { "messagesPerSecond": {{messagesPerSecond}}, "latencyMs": { "p95": {{p95}}, "p99": {{p99}} } },
          "checks": { "passed": true }
        }
        """)!.AsObject();

    private static List<JsonObject> MainBaseline(params double[] walls) =>
        walls.Select((w, i) => Run($"base{i}", "main", w)).ToList();

    private static MetricComparison Metric(RunComparison c, string name) => c.Metrics.Single(m => m.Metric == name);

    [Fact]
    public void ComparesAgainstTheMedianOfRecentBaselineRuns()
    {
        var history = MainBaseline(100, 102, 98, 500);
        var current = Run("now", "feature", 101);

        var comparison = RegressionCheck.Compare(current, history, baselineBranch: "main");

        var wall = Metric(comparison, "Wall clock (s)");
        Assert.Equal(4, comparison.BaselineRuns);
        Assert.Equal(101, wall.Baseline!.Value, 3); // median of 98, 100, 102, 500
        Assert.Equal(ComparisonStatus.Ok, wall.Status);
    }

    [Fact]
    public void FlagsRegressionsPastTolerance_InEitherDirection()
    {
        var history = MainBaseline(100, 100, 100);
        var slower = Run("now", "feature", wall: 130, efficiency: 0.6, claimP95: 400);

        var comparison = RegressionCheck.Compare(slower, history, "main");

        Assert.Equal(ComparisonStatus.Regressed, Metric(comparison, "Wall clock (s)").Status);
        Assert.Equal(ComparisonStatus.Regressed, Metric(comparison, "Efficiency").Status);
        Assert.Equal(ComparisonStatus.Regressed, Metric(comparison, "Claim p95 (ms)").Status);
        Assert.True(comparison.Regressed);
    }

    [Fact]
    public void FlagsImprovements()
    {
        var comparison = RegressionCheck.Compare(Run("now", "feature", 50), MainBaseline(100, 100, 100), "main");

        Assert.Equal(ComparisonStatus.Improved, Metric(comparison, "Wall clock (s)").Status);
        Assert.False(comparison.Regressed);
    }

    [Fact]
    public void SmallAbsoluteChanges_AreNotRegressions_EvenWhenLargeInRelativeTerms()
    {
        // 1.0s -> 2.5s is +150%, but under the 2s absolute floor for wall clock.
        var comparison = RegressionCheck.Compare(Run("now", "feature", 2.5), MainBaseline(1, 1, 1), "main");

        Assert.Equal(ComparisonStatus.Ok, Metric(comparison, "Wall clock (s)").Status);
    }

    [Fact]
    public void BaselineIsOnlySameSdkEnvTopologyScenarioBaselineBranch_PassingRunsOnly_ExcludingTheRunItself_LastTen()
    {
        var history = new List<JsonObject>
        {
            Run("other-branch", "feature", 1000),
            Run("other-env", "main", 1000, env: "local"),
            Run("other-key", "main", 1000, key: "1x1@1ms/1slots"),
            Run("other-sdk", "main", 1000, sdk: "java"),
            Run("other-topology", "main", 1000, apiReplicas: 3),
            Run("failed", "main", 1000, passed: false),
        };
        history.AddRange(Enumerable.Range(0, 3).Select(i => Run($"old{i}", "main", 1000)));
        history.AddRange(Enumerable.Range(0, 10).Select(i => Run($"recent{i}", "main", 100)));
        var current = Run("now", "main", 100);
        history.Add(current);

        var comparison = RegressionCheck.Compare(current, history, "main");

        Assert.Equal(10, comparison.BaselineRuns);
        Assert.Equal(100, Metric(comparison, "Wall clock (s)").Baseline!.Value, 3);
    }

    [Fact]
    public void PeakUtilisation_IsSkipped_WhenThePeakWindowIsTooShortToMeasure()
    {
        // e.g. live-publish: every session is claimed within ~0.1s, so the window is just noise.
        var current = Run("now", "feature", 100);
        current["metrics"]!["peak"]!["startSeconds"] = 0.1;
        current["metrics"]!["peak"]!["endSeconds"] = 0.4;

        var comparison = RegressionCheck.Compare(current, MainBaseline(100, 100, 100), "main");

        Assert.DoesNotContain(comparison.Metrics, m => m.Metric == "Peak utilisation");
        Assert.Contains(comparison.Metrics, m => m.Metric == "Efficiency");
    }

    [Fact]
    public void TooFewBaselineRuns_ReportsNoBaseline()
    {
        var comparison = RegressionCheck.Compare(Run("now", "feature", 500), MainBaseline(100, 100), "main");

        Assert.All(comparison.Metrics, m => Assert.Equal(ComparisonStatus.NoBaseline, m.Status));
        Assert.False(comparison.Regressed);
    }

    [Fact]
    public void Markdown_HasARowPerMetricPerRun()
    {
        var comparison = RegressionCheck.Compare(Run("now", "feature", 130), MainBaseline(100, 100, 100), "main");

        var markdown = RegressionCheck.ToMarkdown([comparison], "main");

        Assert.Contains("| steady-drain | Wall clock (s) | 100 (n=3) | 130 | +30.0% | ❌ regressed |", markdown);
    }

    [Fact]
    public void LoadScenarios_AreJudgedOnThroughputAndTailLatency()
    {
        var history = Enumerable.Range(0, 3).Select(i => LoadRun($"base{i}", "main", 1000)).ToList();

        var comparison = RegressionCheck.Compare(LoadRun("now", "feature", 700, p95: 30, p99: 40), history, "main");

        Assert.Equal(["Messages/s", "Latency p95 (ms)", "Latency p99 (ms)"], comparison.Metrics.Select(m => m.Metric));
        Assert.Equal(ComparisonStatus.Regressed, Metric(comparison, "Messages/s").Status);
        Assert.Equal(ComparisonStatus.Regressed, Metric(comparison, "Latency p95 (ms)").Status);
        Assert.Equal(ComparisonStatus.Ok, Metric(comparison, "Latency p99 (ms)").Status); // p99 is noisier: 100% tolerance
    }
}
