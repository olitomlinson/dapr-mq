using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DaprMQ.Client;

namespace ThroughputTest;

/// <summary>What a run measured, before it becomes a result record.</summary>
public sealed record RunMeasurements(
    bool Drained,
    double? PublishSeconds,
    double? WallClockSeconds,
    double PeakDepth,
    double DeadLettered,
    long MessagesHandled,
    double? TimeToFirstMessageSeconds,
    int PeakConcurrentHandlers,
    Distribution DeliveryLatencyMs,
    StatsTimeline Timeline);

/// <summary>
/// Builds the run's record in the shared SDK harness schema (sdks/testing/perf/result.schema.json,
/// schema version 2) as a P-05 queue drain by the .NET SDK, with its own k8s-queue-drain-r{N} profiles.
/// So the report and regression check treat it like any harness run, and only ever compare it with runs
/// of the same shape: runs group by (environment.label, topology.apiReplicas, scenario.key), and the key
/// carries every parameter that changes the result.
/// </summary>
public static class RunRecord
{
    public static string Profile(Settings s) => $"k8s-queue-drain-r{s.Replicas}";

    public static string Key(Settings s) =>
        $"k8s-queue:{s.Messages}@{s.ItemDelayMs}ms/active{s.Window}/handlers{s.Handlers}/replicas{s.Replicas}"
        + (s.ScalingMode == "fixed" ? "+fixed" : "");

    public static JsonObject Build(Settings s, RunMeasurements m, DateTimeOffset timestamp)
    {
        var profile = Profile(s);
        var concurrency = s.Replicas * Math.Min(s.Handlers, s.Window);
        // The handler work spread over every handler that can run at once, but never less than one handler.
        double? ideal = s.ItemDelayMs > 0 ? Math.Max(s.Messages * s.ItemDelayMs / 1000.0 / concurrency, s.ItemDelayMs / 1000.0) : null;
        double? perSecond = m.WallClockSeconds > 0 ? m.MessagesHandled / m.WallClockSeconds : null;
        var missing = Math.Max(0, s.Messages - m.MessagesHandled - (long)m.DeadLettered);

        var failures = new List<string>();
        if (!m.Drained)
        {
            failures.Add("the queue did not drain");
        }
        if (missing > 0)
        {
            failures.Add($"{missing} messages missing");
        }
        if (m.DeadLettered > 0)
        {
            failures.Add($"{m.DeadLettered:0} messages dead-lettered");
        }

        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["runId"] = $"{timestamp.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_dotnet_{s.EnvLabel}_{profile}",
            ["timestampUtc"] = timestamp.ToUniversalTime().ToString("O"),
            ["sdk"] = new JsonObject
            {
                ["name"] = "dotnet",
                ["version"] = typeof(DaprMQClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
                ["runtime"] = RuntimeInformation.FrameworkDescription
            },
            ["environment"] = new JsonObject
            {
                ["label"] = s.EnvLabel, ["gitSha"] = s.GitSha, ["gitBranch"] = s.GitBranch, ["gitDirty"] = s.GitDirty,
                ["os"] = RuntimeInformation.OSDescription, ["cpuCount"] = Environment.ProcessorCount
            },
            ["topology"] = new JsonObject
            {
                ["apiReplicas"] = s.GatewayReplicas, ["loadBalancer"] = s.GatewayReplicas > 1, ["server"] = s.ServerLabel
            },
            ["scale"] = "extreme",
            ["scenario"] = new JsonObject
            {
                ["id"] = "P-05", ["name"] = "queue-drain", ["profile"] = profile, ["key"] = Key(s),
                ["params"] = new JsonObject
                {
                    ["messages"] = s.Messages, ["settleMs"] = s.ItemDelayMs, ["maxActiveMessages"] = s.Window,
                    ["maxConcurrentHandlers"] = s.Handlers, ["replicas"] = s.Replicas, ["scaling"] = s.ScalingMode,
                    ["lockTtlSeconds"] = s.LockTtlSeconds, ["publishers"] = s.Publishers
                }
            },
            ["metrics"] = new JsonObject
            {
                ["seedSeconds"] = m.PublishSeconds,
                ["wallClockSeconds"] = m.WallClockSeconds,
                ["messagesPerSecond"] = perSecond,
                ["idealSeconds"] = ideal,
                ["efficiency"] = ideal != null && m.WallClockSeconds > 0 ? ideal / m.WallClockSeconds : null,
                ["timeToFirstMessageSeconds"] = m.TimeToFirstMessageSeconds,
                ["peakConcurrentHandlers"] = m.PeakConcurrentHandlers,
                ["deliveryLatencyMs"] = JsonSerializer.SerializeToNode(m.DeliveryLatencyMs, JsonSerializerOptions.Web),
                ["messagesHandled"] = m.MessagesHandled,
                ["duplicates"] = Math.Max(0, m.MessagesHandled - s.Messages),
                ["missing"] = missing,
                ["orderViolations"] = 0,
                ["deadLettered"] = m.DeadLettered,
                ["peakDepth"] = m.PeakDepth
            },
            ["timeline"] = new JsonObject
            {
                ["bucketMs"] = m.Timeline.BucketMs,
                ["series"] = new JsonObject
                {
                    ["messagesPerSecond"] = JsonSerializer.SerializeToNode(m.Timeline.MessagesPerSecond),
                    ["busyHandlers"] = JsonSerializer.SerializeToNode(m.Timeline.BusyHandlers)
                }
            },
            ["checks"] = new JsonObject
            {
                ["passed"] = failures.Count == 0,
                ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f).ToArray())
            }
        };
    }
}
