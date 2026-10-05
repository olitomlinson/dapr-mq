using System.Text.Json.Nodes;

namespace DaprMQ.PerfReport.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perf-results-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Save(string runId, string sdk, string branch, double messagesPerSecond, int minute, bool passed = true, double[]? timeline = null)
    {
        var run = JsonNode.Parse($$"""
            {
              "schemaVersion": 2, "runId": "{{runId}}", "timestampUtc": "2026-10-05T10:{{minute:D2}}:00Z",
              "sdk": { "name": "{{sdk}}" },
              "environment": { "label": "ci", "gitBranch": "{{branch}}" },
              "topology": { "apiReplicas": 1 },
              "scale": "pr",
              "scenario": { "id": "P-01", "name": "enqueue", "profile": "enqueue", "key": "enqueue:c8/q8/b1/256B/3+15s", "params": {} },
              "metrics": { "messagesPerSecond": {{messagesPerSecond}}, "latencyMs": { "p50": 1, "p95": 2, "p99": 3 } },
              "checks": { "passed": {{(passed ? "true" : "false")}}, "failures": [{{(passed ? "" : "\"2 errors\"")}}] }
            }
            """)!.AsObject();
        if (timeline != null)
        {
            run["timeline"] = new JsonObject { ["bucketMs"] = 1000, ["series"] = new JsonObject { ["messagesPerSecond"] = new JsonArray(timeline.Select(v => (JsonNode)v).ToArray()) } };
        }

        PerfResults.Save(_root, run);
    }

    [Fact]
    public void Check_GatesOnRegressionsOnly_WhenAsked()
    {
        for (var i = 0; i < 3; i++)
        {
            Save($"base{i}", "python", "main", 1000, i);
        }

        Save("slow", "python", "feature", 500, 10);

        Assert.Equal(0, Cli.Run(["check", "--results", _root, "--runs", "slow"], TextWriter.Null));
        Assert.Equal(3, Cli.Run(["check", "--results", _root, "--runs", "slow", "--gate"], TextWriter.Null));
    }

    [Fact]
    public void Check_ListsFailedChecks()
    {
        Save("bad", "java", "feature", 1000, 1, passed: false);

        var (markdown, _) = Cli.Check(_root, ["bad"], "main");

        Assert.Contains("`enqueue` failed its checks: 2 errors", markdown);
    }

    [Fact]
    public void Check_RejectsUnknownRuns()
    {
        Assert.Throws<ArgumentException>(() => Cli.Check(_root, ["nope"], "main"));
    }

    [Fact]
    public void Report_EmbedsEverySdksHistory_AndOnlyTheLatestTimelinePerSeries()
    {
        Save("py-old", "python", "main", 900, 1, timeline: [1, 2]);
        Save("py-new", "python", "main", 1000, 2, timeline: [3, 4]);
        Save("ts", "typescript", "main", 800, 3, timeline: [5, 6]);

        var path = HtmlReport.Write(_root);

        var html = File.ReadAllText(path);
        Assert.Equal(Path.Combine(_root, "report.html"), path);
        Assert.Contains("\"runId\":\"py-old\"", html);
        Assert.Contains("\"py-new\":{\"messagesPerSecond\":[3,4]}", html);
        Assert.Contains("\"ts\":{\"messagesPerSecond\":[5,6]}", html);
        Assert.DoesNotContain("\"py-old\":{", html);
    }

    [Fact]
    public void Merge_UnionsHistoryByRunId_AndCopiesRunFiles_KeepingRunsPublishedMeanwhile()
    {
        var into = Directory.CreateTempSubdirectory("perf-into-").FullName;
        try
        {
            // `_root` is a job's copy: an old checkout plus its own new run.
            Save("old", "dotnet", "main", 1, 1);
            Save("mine", "dotnet", "main", 1, 3);
            Directory.CreateDirectory(Path.Combine(_root, "sdk-dotnet", "state-reads"));
            File.WriteAllText(Path.Combine(_root, "sdk-dotnet", "state-reads", "history.jsonl"), """{"runId":"sr1"}""" + "\n");

            // The branch moved on meanwhile: someone else published "theirs".
            PerfResults.Save(into, PerfResults.LoadRun(_root, "dotnet", "old")!);
            var theirs = PerfResults.LoadRun(_root, "dotnet", "old")!;
            theirs["runId"] = "theirs";
            theirs["timestampUtc"] = "2026-10-05T10:02:00Z";
            PerfResults.Save(into, theirs);

            Assert.Equal(0, Cli.Run(["merge", "--results", into, "--from", _root], TextWriter.Null));
            Cli.Run(["merge", "--results", into, "--from", _root], TextWriter.Null); // idempotent

            Assert.Equal(["old", "theirs", "mine"], PerfResults.LoadHistory(into).Select(r => r["runId"]!.GetValue<string>()));
            Assert.NotNull(PerfResults.LoadRun(into, "dotnet", "mine"));
            Assert.Single(File.ReadAllLines(Path.Combine(into, "sdk-dotnet", "state-reads", "history.jsonl")));
        }
        finally
        {
            Directory.Delete(into, recursive: true);
        }
    }
}
