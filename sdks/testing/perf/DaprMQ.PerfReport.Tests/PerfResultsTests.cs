using System.Text.Json.Nodes;

namespace DaprMQ.PerfReport.Tests;

public sealed class PerfResultsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perf-results-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static JsonObject Run(string runId, string sdk, string timestamp) => JsonNode.Parse($$"""
        {
          "schemaVersion": 2, "runId": "{{runId}}", "timestampUtc": "{{timestamp}}",
          "sdk": { "name": "{{sdk}}" },
          "scenario": { "key": "k" },
          "metrics": { "opsPerSecond": 1 },
          "timeline": { "bucketMs": 1000, "series": { "opsPerSecond": [1, 2] } },
          "checks": { "passed": true }
        }
        """)!.AsObject();

    [Fact]
    public void Save_WritesTheFullRunAndAHistoryLineWithoutTheTimeline()
    {
        var path = PerfResults.Save(_root, Run("r1", "python", "2026-10-05T10:00:00Z"));

        Assert.Equal(Path.Combine(_root, "sdk-python", "runs", "r1.json"), path);
        Assert.NotNull(PerfResults.LoadRun(_root, "python", "r1")!["timeline"]);

        var line = Assert.Single(PerfResults.LoadHistory(_root));
        Assert.Null(line["timeline"]);
        Assert.Equal(1, line["metrics"]!["opsPerSecond"]!.GetValue<double>());
    }

    [Fact]
    public void LoadHistory_ReadsEverySdk_InTimeOrder_AndSkipsOtherSchemas()
    {
        PerfResults.Save(_root, Run("late", "java", "2026-10-05T12:00:00Z"));
        PerfResults.Save(_root, Run("early", "dotnet", "2026-10-05T09:00:00Z"));
        File.AppendAllText(Path.Combine(_root, "sdk-dotnet", "history.jsonl"), """{"schemaVersion":1,"runId":"v1"}""" + "\n");

        Assert.Equal(["early", "late"], PerfResults.LoadHistory(_root).Select(r => r["runId"]!.GetValue<string>()));
        Assert.Equal(["late"], PerfResults.LoadHistory(_root, sdk: "java").Select(r => r["runId"]!.GetValue<string>()));
    }

    [Fact]
    public void LoadHistory_OfAMissingDirectory_IsEmpty()
    {
        Assert.Empty(PerfResults.LoadHistory(Path.Combine(_root, "nope")));
    }
}
