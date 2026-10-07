using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaprMQ.PerfReport;

/// <summary>
/// The on-disk results layout every SDK harness writes (sdks/testing/PERFORMANCE_TESTS.md#result-format):
/// {root}/sdk-{sdk}/runs/{runId}.json holds a run in full; {root}/sdk-{sdk}/history.jsonl gets the
/// same record without its timeline, one line per run.
/// </summary>
public static class PerfResults
{
    public const int SchemaVersion = 2;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly JsonSerializerOptions Indented = new(Json) { WriteIndented = true };

    public static string SdkDir(string root, string sdk) => Path.Combine(root, $"sdk-{sdk}");

    public static string Save(string root, JsonObject run)
    {
        var sdkDir = SdkDir(root, run["sdk"]!["name"]!.GetValue<string>());
        var runsDir = Path.Combine(sdkDir, "runs");
        Directory.CreateDirectory(runsDir);

        var runPath = Path.Combine(runsDir, $"{run["runId"]!.GetValue<string>()}.json");
        File.WriteAllText(runPath, run.ToJsonString(Indented));

        var summary = run.DeepClone().AsObject();
        summary.Remove("timeline");
        File.AppendAllText(Path.Combine(sdkDir, "history.jsonl"), summary.ToJsonString(Json) + "\n");

        return runPath;
    }

    /// <summary>Every SDK's history (or one SDK's), oldest first. Lines of other schema versions are skipped.</summary>
    public static IReadOnlyList<JsonObject> LoadHistory(string root, string? sdk = null)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var dirs = sdk != null ? [SdkDir(root, sdk)] : Directory.GetDirectories(root, "sdk-*");
        return dirs
            .Select(d => Path.Combine(d, "history.jsonl"))
            .Where(File.Exists)
            .SelectMany(File.ReadLines)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(r => r["schemaVersion"]?.GetValue<int>() == SchemaVersion)
            .OrderBy(Timestamp)
            .ToList();
    }

    /// <summary>Parsed, since each language formats ISO-8601 a little differently.</summary>
    public static DateTimeOffset Timestamp(JsonObject run) =>
        DateTimeOffset.TryParse(run["timestampUtc"]?.GetValue<string>(), out var t) ? t : DateTimeOffset.MinValue;

    public static JsonObject? LoadRun(string root, string sdk, string runId)
    {
        var path = Path.Combine(SdkDir(root, sdk), "runs", $"{runId}.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;
    }
}
