using System.Text.Json;
using System.Text.Json.Nodes;
using DaprMQ.PerfReport;

namespace DaprMQ.Client.Perf;

public sealed record StateReadsStep(string Name, int Operations, int Reads, int Writes, IReadOnlyList<StateKeyCount> ByKey)
{
    public double ReadsPerOp => Operations == 0 ? 0 : (double)Reads / Operations;
    public double WritesPerOp => Operations == 0 ? 0 : (double)Writes / Operations;
}

public sealed record StateReadsRunRecord(int SchemaVersion, string RunId, DateTimeOffset TimestampUtc, RunEnvironment Environment, IReadOnlyList<StateReadsStep> Steps);

/// <summary>
/// {outDir}/state-reads/runs/{runId}.json holds a run in full (incl. the per-key breakdown);
/// {outDir}/state-reads/history.jsonl gets one line per run without it, for the regression check.
/// </summary>
public static class StateReadsStore
{
    private static readonly JsonSerializerOptions Indented = new(RunEnvironment.Json) { WriteIndented = true };

    public static string Dir(string outDir) => Path.Combine(outDir, "state-reads");

    public static string Save(string outDir, StateReadsRunRecord run)
    {
        var runsDir = Path.Combine(Dir(outDir), "runs");
        Directory.CreateDirectory(runsDir);

        var runPath = Path.Combine(runsDir, $"{run.RunId}.json");
        File.WriteAllText(runPath, JsonSerializer.Serialize(run, Indented));

        var summary = JsonSerializer.SerializeToNode(run, RunEnvironment.Json)!.AsObject();
        foreach (var step in summary["steps"]!.AsArray())
        {
            step!.AsObject().Remove("byKey");
        }
        File.AppendAllText(Path.Combine(Dir(outDir), "history.jsonl"), summary.ToJsonString(RunEnvironment.Json) + "\n");

        return runPath;
    }

    public static IReadOnlyList<JsonObject> LoadHistory(string outDir)
    {
        var path = Path.Combine(Dir(outDir), "history.jsonl");
        return File.Exists(path)
            ? File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonNode.Parse(l)!.AsObject()).ToList()
            : [];
    }
}

/// <summary>
/// Per-step reads/writes per operation against the median of recent baseline runs. Statement
/// counts don't suffer from runner noise the way timings do, so the tolerance is tight; steps
/// driven by a reminder's timing get a looser one.
/// </summary>
public static class StateReadsRegressionCheck
{
    private const double Tolerance = 0.05;
    private const double TimingDependentTolerance = 0.15;
    private const double FloorPerOp = 0.5;

    /// <summary>Steps whose counts depend on how a reminder's ticks interleave with the operations.</summary>
    public static readonly HashSet<string> TimingDependentSteps = ["topic-relay", "consume-session"];

    public static IReadOnlyList<RunComparison> Compare(JsonObject current, IReadOnlyList<JsonObject> history, string baselineBranch)
    {
        var runId = RegressionCheck.Str(current, "runId");
        var env = RegressionCheck.Str(current, "environment", "label");
        var baselineRuns = history
            .Where(r => RegressionCheck.Str(r, "runId") != runId
                        && RegressionCheck.Str(r, "environment", "label") == env
                        && RegressionCheck.Str(r, "environment", "gitBranch") == baselineBranch)
            .ToList();

        return current["steps"]!.AsArray().Select(s => CompareStep(s!.AsObject(), baselineRuns)).ToList();
    }

    private static RunComparison CompareStep(JsonObject step, List<JsonObject> baselineRuns)
    {
        var name = RegressionCheck.Str(step, "name")!;
        var operations = RegressionCheck.Num(step, ["operations"]);
        var baseline = baselineRuns
            .Select(r => r["steps"]?.AsArray()
                .Select(s => s!.AsObject())
                .FirstOrDefault(s => RegressionCheck.Str(s, "name") == name && RegressionCheck.Num(s, ["operations"]) == operations))
            .OfType<JsonObject>()
            .TakeLast(RegressionCheck.Window)
            .ToList();

        var tolerance = TimingDependentSteps.Contains(name) ? TimingDependentTolerance : Tolerance;
        var metrics = new[] { ("Reads/op", "readsPerOp"), ("Writes/op", "writesPerOp") }
            .Select(m => RegressionCheck.Assess(
                m.Item1,
                RegressionCheck.Num(step, [m.Item2]) ?? 0,
                baseline.Select(b => RegressionCheck.Num(b, [m.Item2])).OfType<double>().ToList(),
                higherIsWorse: true, tolerance, FloorPerOp))
            .ToList();

        return new RunComparison(name, $"{name} x{operations}", baseline.Count, metrics);
    }
}
