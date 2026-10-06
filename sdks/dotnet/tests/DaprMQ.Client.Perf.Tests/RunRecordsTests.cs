using System.Text.Json.Nodes;
using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class RunRecordsTests
{
    private static readonly RunContext Context = new(
        new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero),
        new RunEnvironment("ci", "abc123", "main", false, "Linux", 4, ".NET 10.0.0", "testcontainers daprmq-api:test"),
        Scale: "pr",
        ApiReplicas: 2,
        SchedulerReplicas: 3,
        SdkVersion: "0.1.0");

    private static LoadStep Step(int concurrency, double messagesPerSecond, int errors = 0) =>
        new(concurrency, concurrency, 30, 100, (int)(messagesPerSecond * 30), errors, messagesPerSecond, messagesPerSecond, new Distribution(100, 2, 1, 3, 4, 5));

    private static StepTimeline Timeline(int concurrency, params int[] ops) =>
        new(ops, ops, ops.Select(o => (double?)o).ToArray(), ops.Select(o => (double?)o).ToArray(), ops.Select(o => (double?)o).ToArray(), ops.Select(_ => 0).ToArray(), ops.Select(_ => concurrency).ToArray());

    private static readonly LoadParams Ramp = PerfOptions.LoadProfiles["enqueue-ramp"];

    [Fact]
    public void Load_RecordsIdentityTopologyAndScenario()
    {
        var run = RunRecords.Load(Context, "enqueue", PerfOptions.LoadProfiles["enqueue"],
            new LoadResult([Step(8, 500)], [Timeline(8, 500, 500)], Drained: false, []));

        Assert.Equal(2, run["schemaVersion"]!.GetValue<int>());
        Assert.Equal("20261005T100000Z_dotnet_ci_enqueue", run["runId"]!.GetValue<string>());
        Assert.Equal("dotnet", run["sdk"]!["name"]!.GetValue<string>());
        Assert.Equal("0.1.0", run["sdk"]!["version"]!.GetValue<string>());
        Assert.Equal(2, run["topology"]!["apiReplicas"]!.GetValue<int>());
        Assert.Equal(3, run["topology"]!["schedulerReplicas"]!.GetValue<int>());
        Assert.True(run["topology"]!["loadBalancer"]!.GetValue<bool>());
        Assert.Equal("pr", run["scale"]!.GetValue<string>());
        Assert.Equal("P-01", run["scenario"]!["id"]!.GetValue<string>());
        Assert.Equal("enqueue:c8/q8/b1/256B/3+15s", run["scenario"]!["key"]!.GetValue<string>());
        Assert.Equal(8, run["scenario"]!["params"]!["queues"]!.GetValue<int>());
        Assert.True(run["checks"]!["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void LoadRamp_HeadlinesTheBestStep_AndConcatenatesTheTimelines()
    {
        var run = RunRecords.Load(Context, "enqueue-ramp", Ramp,
            new LoadResult([Step(1, 100), Step(2, 300), Step(4, 250)], [Timeline(1, 1), Timeline(2, 2), Timeline(4, 3, 3)], false, []));

        Assert.Equal(300, run["metrics"]!["messagesPerSecond"]!.GetValue<double>());
        Assert.Equal(3, run["steps"]!.AsArray().Count);
        Assert.Equal("[1,2,3,3]", run["timeline"]!["series"]!["opsPerSecond"]!.ToJsonString());
        Assert.Equal("[1,2,4,4]", run["timeline"]!["series"]!["concurrency"]!.ToJsonString());
        Assert.Null(run["scenario"]!["params"]!["queues"]);
    }

    [Fact]
    public void Load_FailsItsChecks_OnErrorsOrADrainedQueue()
    {
        var run = RunRecords.Load(Context, "dequeue-ack", PerfOptions.LoadProfiles["dequeue-ack"],
            new LoadResult([Step(8, 100, errors: 2)], [Timeline(8, 1)], Drained: true, ["TimeoutException: boom"]));

        Assert.False(run["checks"]!["passed"]!.GetValue<bool>());
        Assert.Equal(
            ["2 errors (first: TimeoutException: boom)", "a queue drained before the window ended: raise seedPerQueue"],
            run["checks"]!["failures"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public void SessionDrain_MovesTheBusySlotsTimelineOutOfTheMetrics()
    {
        var scenario = PerfOptions.Parse(["--profile", "steady-drain"]).Scenario;
        var result = SessionDrainMetrics.Compute(scenario, [], [new HandlerRecord("s0", 0, 0, 1500)], 2000, 1) with { Missing = 0 };

        var run = RunRecords.SessionDrain(Context, "steady-drain", scenario, result);

        Assert.Equal("P-04", run["scenario"]!["id"]!.GetValue<string>());
        Assert.Equal("session-drain", run["scenario"]!["name"]!.GetValue<string>());
        Assert.Equal(scenario.Key, run["scenario"]!["key"]!.GetValue<string>());
        Assert.Null(run["metrics"]!["busySlotsTimeline"]);
        Assert.Equal("[1,0.5]", run["timeline"]!["series"]!["busySlots"]!.ToJsonString());
        Assert.True(run["checks"]!["passed"]!.GetValue<bool>());
    }

    [Fact]
    public void MigrateV1_ConvertsASessionDrainRun_AndLeavesV2Alone()
    {
        var v1 = JsonNode.Parse("""
            {
              "schemaVersion": 1, "runId": "20260301T000000Z_ci-ubuntu-latest_steady-drain", "timestampUtc": "2026-03-01T00:00:00+00:00",
              "environment": { "label": "ci-ubuntu-latest", "gitSha": "s", "gitBranch": "main", "gitDirty": false, "os": "Linux", "cpuCount": 4, "dotnet": ".NET 10.0.0", "server": "testcontainers daprmq-api:test" },
              "profile": "steady-drain",
              "scenario": { "sessions": 200, "messagesPerSession": 20, "settleMs": 100, "maxConcurrentSessions": 20, "prefetchCount": 10, "leaseSeconds": 30, "sessionIdleTimeoutSeconds": 1, "key": "stale" },
              "metrics": { "wallClockSeconds": 12, "missing": 0, "fifoViolations": 1, "timelineBucketMs": 1000, "busySlotsTimeline": [1, 2] }
            }
            """)!.AsObject();

        var v2 = RunRecords.MigrateV1(v1);

        Assert.Equal(2, v2["schemaVersion"]!.GetValue<int>());
        Assert.Equal(v1["runId"]!.GetValue<string>(), v2["runId"]!.GetValue<string>());
        Assert.Equal("dotnet", v2["sdk"]!["name"]!.GetValue<string>());
        Assert.Equal(".NET 10.0.0", v2["sdk"]!["runtime"]!.GetValue<string>());
        Assert.Equal("ci-ubuntu-latest", v2["environment"]!["label"]!.GetValue<string>());
        Assert.Equal(1, v2["topology"]!["apiReplicas"]!.GetValue<int>());
        Assert.Equal(1, v2["topology"]!["schedulerReplicas"]!.GetValue<int>());
        Assert.Equal("pr", v2["scale"]!.GetValue<string>());
        Assert.Equal("steady-drain", v2["scenario"]!["profile"]!.GetValue<string>());
        Assert.Equal("200x20@100ms/20slots+idle1s", v2["scenario"]!["key"]!.GetValue<string>());
        Assert.Equal(12, v2["metrics"]!["wallClockSeconds"]!.GetValue<double>());
        Assert.Null(v2["metrics"]!["busySlotsTimeline"]);
        Assert.Equal("[1,2]", v2["timeline"]!["series"]!["busySlots"]!.ToJsonString());
        Assert.False(v2["checks"]!["passed"]!.GetValue<bool>());

        Assert.Same(v2, RunRecords.MigrateV1(v2));
    }

    [Fact]
    public void MigrateV1Directory_RewritesHistoryAndRunsInPlace_Idempotently()
    {
        var dir = Directory.CreateTempSubdirectory("sdk-dotnet-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "runs"));
            const string line = """{"schemaVersion":1,"runId":"r1","timestampUtc":"2026-03-01T00:00:00+00:00","environment":{"label":"ci"},"profile":"full","scenario":{"sessions":1000,"messagesPerSession":100,"settleMs":1000,"maxConcurrentSessions":20,"prefetchCount":10,"leaseSeconds":30,"sessionIdleTimeoutSeconds":0},"metrics":{"missing":0,"fifoViolations":0}}""";
            File.WriteAllText(Path.Combine(dir, "history.jsonl"), line + "\n");
            File.WriteAllText(Path.Combine(dir, "runs", "r1.json"), line);

            Assert.Equal(2, RunRecords.MigrateV1Directory(dir));
            Assert.Equal(0, RunRecords.MigrateV1Directory(dir));

            var history = JsonNode.Parse(File.ReadAllLines(Path.Combine(dir, "history.jsonl")).Single())!;
            Assert.Equal("extreme", history["scale"]!.GetValue<string>());
            Assert.Null(history["timeline"]);
            Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "runs", "r1.json")))!["schemaVersion"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
