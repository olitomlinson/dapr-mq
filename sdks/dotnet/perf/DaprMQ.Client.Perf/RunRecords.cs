using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DaprMQ.PerfReport;

namespace DaprMQ.Client.Perf;

/// <summary>Everything about a run that isn't its scenario or result.</summary>
public sealed record RunContext(DateTimeOffset Timestamp, RunEnvironment Environment, string Scale, int ApiReplicas, int? SchedulerReplicas, string? SdkVersion)
{
    public static string? ClientVersion() =>
        typeof(DaprMQClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
}

/// <summary>
/// Builds the schema-2 run records of sdks/testing/perf/result.schema.json, and converts the
/// harness's older schema-1 session-drain records to them.
/// </summary>
public static class RunRecords
{
    private static readonly JsonSerializerOptions Json = PerfResults.Json;

    private static readonly HashSet<string> PrSessionProfiles = ["steady-drain", "session-churn", "deep-session", "live-publish", "sdk-defaults"];

    public static JsonObject Load(RunContext context, string profile, LoadParams load, LoadResult result)
    {
        // A ramp's headline is its best step; ops/messages/errors cover every step.
        var best = result.Steps.MaxBy(s => s.MessagesPerSecond)!;
        var metrics = new JsonObject
        {
            ["opsPerSecond"] = best.OpsPerSecond,
            ["messagesPerSecond"] = best.MessagesPerSecond,
            ["latencyMs"] = JsonSerializer.SerializeToNode(best.LatencyMs, Json),
            ["ops"] = result.Steps.Sum(s => s.Ops),
            ["messages"] = result.Steps.Sum(s => s.Messages),
            ["errors"] = result.Steps.Sum(s => s.Errors),
        };

        var parameters = new JsonObject
        {
            ["concurrency"] = new JsonArray(load.Concurrency.Select(c => (JsonNode)c).ToArray()),
            ["queues"] = load.Queues,
            ["batchSize"] = load.BatchSize,
            ["dequeueCount"] = load.DequeueCount,
            ["payloadBytes"] = load.PayloadBytes,
            ["seedPerQueue"] = load.SeedPerQueue,
            ["warmupSeconds"] = load.WarmupSeconds,
            ["durationSeconds"] = load.DurationSeconds,
        };

        var series = new JsonObject();
        void Series<T>(string name, Func<StepTimeline, IEnumerable<T>> pick) =>
            series[name] = JsonSerializer.SerializeToNode(result.Timelines.SelectMany(pick).ToArray(), Json);
        Series("opsPerSecond", t => t.OpsPerSecond);
        Series("messagesPerSecond", t => t.MessagesPerSecond);
        Series("latencyP50Ms", t => t.LatencyP50Ms);
        Series("latencyP95Ms", t => t.LatencyP95Ms);
        Series("latencyP99Ms", t => t.LatencyP99Ms);
        Series("errors", t => t.Errors);
        Series("concurrency", t => t.Concurrency);

        var failures = new List<string>();
        var errors = result.Steps.Sum(s => s.Errors);
        if (errors > 0)
        {
            failures.Add($"{errors} errors" + (result.SampleErrors.Count > 0 ? $" (first: {result.SampleErrors[0]})" : ""));
        }

        if (result.Drained)
        {
            failures.Add("a queue drained before the window ended: raise seedPerQueue");
        }

        var run = Envelope(context, profile, load.Id, load.Scenario, load.Key, parameters, metrics, failures);
        run["steps"] = JsonSerializer.SerializeToNode(result.Steps, Json);
        run["timeline"] = new JsonObject { ["bucketMs"] = LoadMetrics.BucketMs, ["series"] = series };
        return run;
    }

    public static JsonObject SessionDrain(RunContext context, string profile, ScenarioParams scenario, SessionDrainResult result)
    {
        var metrics = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
        var (timeline, failures) = SplitSessionDrainMetrics(metrics);
        var parameters = JsonSerializer.SerializeToNode(scenario, Json)!.AsObject();
        parameters.Remove("key");
        parameters.Remove("idealSeconds");

        var run = Envelope(context, profile, "P-04", "session-drain", scenario.Key, parameters, metrics, failures);
        run["timeline"] = timeline;
        return run;
    }

    public static JsonObject QueueDrain(RunContext context, string profile, QueueDrainParams scenario, QueueDrainResult result)
    {
        var metrics = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
        metrics.Remove("messagesPerSecondTimeline");
        metrics.Remove("busyHandlersTimeline");

        var parameters = JsonSerializer.SerializeToNode(scenario, Json)!.AsObject();
        foreach (var derived in new[] { "key", "livePublish", "concurrency", "tailMessages", "idealSeconds" })
        {
            parameters.Remove(derived);
        }

        var failures = new List<string>();
        if (result.Missing > 0)
        {
            failures.Add($"{result.Missing} messages missing");
        }

        // Above a window of 1, handlers legitimately start messages out of queue order.
        if (scenario.StrictOrder && result.OrderViolations > 0)
        {
            failures.Add($"{result.OrderViolations} order violations");
        }

        var run = Envelope(context, profile, "P-05", "queue-drain", scenario.Key, parameters, metrics, failures);
        run["timeline"] = new JsonObject
        {
            ["bucketMs"] = QueueDrainMetrics.BucketMs,
            ["series"] = new JsonObject
            {
                ["messagesPerSecond"] = JsonSerializer.SerializeToNode(result.MessagesPerSecondTimeline, Json),
                ["busyHandlers"] = JsonSerializer.SerializeToNode(result.BusyHandlersTimeline, Json),
            },
        };
        return run;
    }

    private static JsonObject Envelope(RunContext context, string profile, string id, string name, string key,
        JsonObject parameters, JsonObject metrics, List<string> failures)
    {
        var env = context.Environment;
        return new JsonObject
        {
            ["schemaVersion"] = PerfResults.SchemaVersion,
            ["runId"] = $"{context.Timestamp.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_dotnet_{env.Label}_{profile}",
            ["timestampUtc"] = context.Timestamp.ToUniversalTime().ToString("O"),
            ["sdk"] = new JsonObject { ["name"] = "dotnet", ["version"] = context.SdkVersion, ["runtime"] = env.Dotnet },
            ["environment"] = new JsonObject
            {
                ["label"] = env.Label, ["gitSha"] = env.GitSha, ["gitBranch"] = env.GitBranch, ["gitDirty"] = env.GitDirty,
                ["os"] = env.Os, ["cpuCount"] = env.CpuCount,
            },
            ["topology"] = new JsonObject
            {
                ["apiReplicas"] = context.ApiReplicas, ["schedulerReplicas"] = context.SchedulerReplicas,
                ["loadBalancer"] = context.ApiReplicas > 1, ["server"] = env.Server,
            },
            ["scale"] = context.Scale,
            ["scenario"] = new JsonObject { ["id"] = id, ["name"] = name, ["profile"] = profile, ["key"] = key, ["params"] = parameters },
            ["metrics"] = metrics,
            ["checks"] = new JsonObject
            {
                ["passed"] = failures.Count == 0,
                ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f).ToArray()),
            },
        };
    }

    /// <summary>Takes the busy-slots timeline out of session-drain metrics and derives the checks.</summary>
    private static (JsonObject? Timeline, List<string> Failures) SplitSessionDrainMetrics(JsonObject metrics)
    {
        JsonObject? timeline = null;
        if (metrics["busySlotsTimeline"] is JsonArray busy)
        {
            timeline = new JsonObject
            {
                ["bucketMs"] = metrics["timelineBucketMs"]?.GetValue<int>() ?? 1000,
                ["series"] = new JsonObject { ["busySlots"] = busy.DeepClone() },
            };
        }

        metrics.Remove("busySlotsTimeline");
        metrics.Remove("timelineBucketMs");

        var failures = new List<string>();
        if (metrics["missing"]?.GetValue<int>() is > 0 and var missing)
        {
            failures.Add($"{missing} messages missing");
        }

        if (metrics["fifoViolations"]?.GetValue<int>() is > 0 and var fifo)
        {
            failures.Add($"{fifo} FIFO violations");
        }

        return (timeline, failures);
    }

    /// <summary>
    /// Converts one schema-1 session-drain record (history line or run file) to schema 2. Records
    /// already on schema 2 are returned as they are.
    /// </summary>
    public static JsonObject MigrateV1(JsonObject v1)
    {
        if (v1["schemaVersion"]?.GetValue<int>() == PerfResults.SchemaVersion)
        {
            return v1;
        }

        var env = v1["environment"]?.AsObject() ?? new JsonObject();
        var profile = v1["profile"]?.GetValue<string>() ?? "full";
        var scenarioV1 = v1["scenario"]!.AsObject();
        var scenario = scenarioV1.Deserialize<ScenarioParams>(Json)!;
        var parameters = scenarioV1.DeepClone().AsObject();
        parameters.Remove("key");

        var metrics = v1["metrics"]!.DeepClone().AsObject();
        var (timeline, failures) = SplitSessionDrainMetrics(metrics);

        var run = new JsonObject
        {
            ["schemaVersion"] = PerfResults.SchemaVersion,
            ["runId"] = v1["runId"]!.GetValue<string>(),
            ["timestampUtc"] = v1["timestampUtc"]!.GetValue<string>(),
            ["sdk"] = new JsonObject { ["name"] = "dotnet", ["version"] = null, ["runtime"] = env["dotnet"]?.DeepClone() },
            ["environment"] = new JsonObject
            {
                ["label"] = env["label"]?.DeepClone(), ["gitSha"] = env["gitSha"]?.DeepClone(), ["gitBranch"] = env["gitBranch"]?.DeepClone(),
                ["gitDirty"] = env["gitDirty"]?.DeepClone() ?? false, ["os"] = env["os"]?.DeepClone(), ["cpuCount"] = env["cpuCount"]?.DeepClone(),
            },
            // Schema-1 runs all used the single-instance integration stack.
            ["topology"] = new JsonObject { ["apiReplicas"] = 1, ["schedulerReplicas"] = 1, ["loadBalancer"] = false, ["server"] = env["server"]?.DeepClone() },
            ["scale"] = PrSessionProfiles.Contains(profile) ? "pr" : profile == "full" ? "extreme" : "adhoc",
            ["scenario"] = new JsonObject { ["id"] = "P-04", ["name"] = "session-drain", ["profile"] = profile, ["key"] = scenario.Key, ["params"] = parameters },
            ["metrics"] = metrics,
            ["checks"] = new JsonObject
            {
                ["passed"] = failures.Count == 0,
                ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f).ToArray()),
            },
        };
        if (timeline != null)
        {
            run["timeline"] = timeline;
        }

        return run;
    }

    /// <summary>
    /// Rewrites {dir}/history.jsonl and {dir}/runs/*.json in place. Returns how many records it
    /// converted, so a second pass returns 0. Leaves subdirectories (state-reads/) alone.
    /// </summary>
    public static int MigrateV1Directory(string dir)
    {
        var converted = 0;
        JsonObject Convert(JsonObject record)
        {
            var migrated = MigrateV1(record);
            if (!ReferenceEquals(migrated, record))
            {
                converted++;
            }

            return migrated;
        }

        var historyPath = Path.Combine(dir, "history.jsonl");
        if (File.Exists(historyPath))
        {
            var lines = File.ReadAllLines(historyPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l =>
                {
                    var migrated = Convert(JsonNode.Parse(l)!.AsObject());
                    migrated.Remove("timeline");
                    return migrated.ToJsonString(Json);
                })
                .ToList();
            File.WriteAllLines(historyPath, lines);
        }

        var runsDir = Path.Combine(dir, "runs");
        if (Directory.Exists(runsDir))
        {
            var indented = new JsonSerializerOptions(Json) { WriteIndented = true };
            foreach (var path in Directory.GetFiles(runsDir, "*.json"))
            {
                var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                var migrated = Convert(record);
                if (!ReferenceEquals(migrated, record))
                {
                    File.WriteAllText(path, migrated.ToJsonString(indented));
                }
            }
        }

        return converted;
    }
}
