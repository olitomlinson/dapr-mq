using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaprMQ.Client.Perf;

public sealed record RunEnvironment(string Label, string? GitSha, string? GitBranch, bool GitDirty, string Os, int CpuCount, string Dotnet, string Server);

public sealed record RunRecord(int SchemaVersion, string RunId, DateTimeOffset TimestampUtc, RunEnvironment Environment, string Profile, ScenarioParams Scenario, SessionDrainResult Metrics);

/// <summary>
/// results/runs/{runId}.json holds a run in full (incl. the busy-slots timeline);
/// results/history.jsonl gets one line per run without the timeline, for the trend charts.
/// </summary>
public static class ResultStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly JsonSerializerOptions Indented = new(Json) { WriteIndented = true };

    public static string Save(string outDir, RunRecord run)
    {
        var runsDir = Path.Combine(outDir, "runs");
        Directory.CreateDirectory(runsDir);

        var runPath = Path.Combine(runsDir, $"{run.RunId}.json");
        File.WriteAllText(runPath, JsonSerializer.Serialize(run, Indented));

        var summary = JsonSerializer.SerializeToNode(run, Json)!.AsObject();
        summary["metrics"]!.AsObject().Remove("busySlotsTimeline");
        File.AppendAllText(Path.Combine(outDir, "history.jsonl"), summary.ToJsonString(Json) + "\n");

        return runPath;
    }

    /// <summary>
    /// Scenario keys are recomputed from each record's parameters, so runs saved before a setting
    /// became part of the key still group correctly (missing parameters take their defaults).
    /// </summary>
    public static IReadOnlyList<JsonObject> LoadHistory(string outDir)
    {
        var path = Path.Combine(outDir, "history.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                var run = JsonNode.Parse(l)!.AsObject();
                var scenario = run["scenario"]!.AsObject();
                scenario["key"] = scenario.Deserialize<ScenarioParams>(Json)!.Key;
                return run;
            })
            .ToList();
    }

    public static JsonObject? LoadRun(string outDir, string runId)
    {
        var path = Path.Combine(outDir, "runs", $"{runId}.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;
    }

    public static RunEnvironment CaptureEnvironment(string label, string server) => new(
        label,
        Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Git("rev-parse HEAD"),
        Environment.GetEnvironmentVariable("GITHUB_REF_NAME") ?? Git("rev-parse --abbrev-ref HEAD"),
        !string.IsNullOrEmpty(Git("status --porcelain --untracked-files=no")),
        RuntimeInformation.OSDescription,
        Environment.ProcessorCount,
        RuntimeInformation.FrameworkDescription,
        server);

    private static string? Git(string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
