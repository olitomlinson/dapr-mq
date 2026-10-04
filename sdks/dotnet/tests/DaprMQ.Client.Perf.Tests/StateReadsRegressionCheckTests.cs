using System.Text.Json.Nodes;
using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class StateReadsRegressionCheckTests
{
    private static JsonObject Run(string runId, string branch, double readsPerOp, double writesPerOp = 2,
        string step = "dequeue-ack", int operations = 50, string env = "ci") =>
        JsonNode.Parse($$"""
        {
          "runId": "{{runId}}",
          "environment": { "label": "{{env}}", "gitBranch": "{{branch}}" },
          "steps": [
            { "name": "{{step}}", "operations": {{operations}}, "readsPerOp": {{readsPerOp}}, "writesPerOp": {{writesPerOp}} }
          ]
        }
        """)!.AsObject();

    private static List<JsonObject> MainBaseline(params double[] readsPerOp) =>
        readsPerOp.Select((r, i) => Run($"base{i}", "main", r)).ToList();

    private static MetricComparison Metric(RunComparison c, string name) => c.Metrics.Single(m => m.Metric == name);

    [Fact]
    public void ComparesEachStepAgainstTheMedianOfRecentBaselineRuns()
    {
        var history = MainBaseline(4, 4, 4.2, 9);

        var comparison = Assert.Single(StateReadsRegressionCheck.Compare(Run("now", "feature", 4.1), history, "main"));

        Assert.Equal("dequeue-ack", comparison.Profile);
        Assert.Equal(4, comparison.BaselineRuns);
        var reads = Metric(comparison, "Reads/op");
        Assert.Equal(4.1, reads.Baseline!.Value, 3); // median of 4, 4, 4.2, 9
        Assert.Equal(ComparisonStatus.Ok, reads.Status);
    }

    [Fact]
    public void FlagsASmallButRealIncrease_SinceCountsAreDeterministic()
    {
        var history = MainBaseline(10, 10, 10);

        var comparison = Assert.Single(StateReadsRegressionCheck.Compare(Run("now", "feature", 11, writesPerOp: 1), history, "main"));

        Assert.Equal(ComparisonStatus.Regressed, Metric(comparison, "Reads/op").Status);
        Assert.Equal(ComparisonStatus.Improved, Metric(comparison, "Writes/op").Status);
    }

    [Fact]
    public void IgnoresChangesUnderTheAbsoluteFloor()
    {
        var history = MainBaseline(1, 1, 1);

        var comparison = Assert.Single(StateReadsRegressionCheck.Compare(Run("now", "feature", 1.4), history, "main"));

        Assert.Equal(ComparisonStatus.Ok, Metric(comparison, "Reads/op").Status);
    }

    [Fact]
    public void BaselineOnlyCountsTheSameStepSizeEnvironmentAndBranch()
    {
        var history = new List<JsonObject>
        {
            Run("a", "main", 4), Run("b", "main", 4), Run("c", "main", 4),
            Run("d", "main", 99, operations: 10),
            Run("e", "main", 99, env: "local"),
            Run("f", "feature", 99),
        };

        var comparison = Assert.Single(StateReadsRegressionCheck.Compare(Run("now", "feature", 4), history, "main"));

        Assert.Equal(3, comparison.BaselineRuns);
        Assert.Equal(4, Metric(comparison, "Reads/op").Baseline!.Value, 3);
    }

    [Fact]
    public void TooFewBaselineRuns_ReportsNoBaseline()
    {
        var comparison = Assert.Single(StateReadsRegressionCheck.Compare(Run("now", "feature", 4), MainBaseline(4, 4), "main"));

        Assert.All(comparison.Metrics, m => Assert.Equal(ComparisonStatus.NoBaseline, m.Status));
    }
}
