using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DaprMQ.Client;

namespace ThroughputTest;

/// <summary>
/// What a run measured, before it becomes a result record. For sessions, PeakDepth is the most sessions
/// with work at once and PeakConcurrentHandlers the most sessions being handled at once.
/// </summary>
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
    StatsTimeline Timeline)
{
    /// <summary>Sessions only: messages a consumer handled after a later message of their session.</summary>
    public int FifoViolations { get; init; }

    /// <summary>Sessions only: when the scaler's count of sessions with work first read 0.</summary>
    public double? ScalerDrainSeconds { get; init; }
}

/// <summary>
/// Builds the run's record in the shared SDK harness schema (sdks/testing/perf/result.schema.json,
/// schema version 2): a P-05 queue drain by the .NET SDK with its own k8s-queue-drain-r{N} profiles, or
/// for the sessions workload a P-04 session drain with k8s-session-drain-r{N} profiles.
/// So the report and regression check treat it like any harness run, and only ever compare it with runs
/// of the same shape: runs group by (environment.label, topology.apiReplicas, scenario.key), and the key
/// carries every parameter that changes the result.
/// </summary>
public static class RunRecord
{
    private static bool IsSessions(Settings s) => s.Workload == "sessions";

    public static string Profile(Settings s) => IsSessions(s) ? $"k8s-session-drain-r{s.Replicas}" : $"k8s-queue-drain-r{s.Replicas}";

    public static string Key(Settings s) =>
        (IsSessions(s)
            ? $"k8s-sessions:{s.Sessions}x{s.MessagesPerSession}@{s.ItemDelayMs}ms/{s.ConcurrentSessions}slots/prefetch{s.Prefetch}"
              + $"/lease{s.LeaseSeconds}s/idle{s.SessionIdleTimeoutSeconds}s/replicas{s.Replicas}"
              + (s.SessionLengthJitterPercent > 0 ? $"+len{s.SessionLengthJitterPercent}" : "")
              + (s.PublishJitterSeconds > 0 ? $"+pubjitter{s.PublishJitterSeconds}s" : "")
            : $"k8s-queue:{s.Messages}@{s.ItemDelayMs}ms/active{s.Window}/handlers{s.Handlers}/replicas{s.Replicas}")
        + (s.ScalingMode == "fixed" ? "+fixed" : s.FastScaleUp ? "+fastscale" : "")
        + (s.ControlPlaneReplicas > 0 ? "+controlplane" : "");

    public static JsonObject Build(Settings s, RunMeasurements m, DateTimeOffset timestamp) =>
        IsSessions(s) ? BuildSessions(s, m, timestamp) : BuildQueue(s, m, timestamp);

    private static JsonObject BuildQueue(Settings s, RunMeasurements m, DateTimeOffset timestamp)
    {
        var concurrency = s.Replicas * Math.Min(s.Handlers, s.Window);
        // The handler work spread over every handler that can run at once, but never less than one handler.
        double? ideal = s.ItemDelayMs > 0 ? Math.Max(s.Messages * s.ItemDelayMs / 1000.0 / concurrency, s.ItemDelayMs / 1000.0) : null;
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

        var parameters = new JsonObject
        {
            ["messages"] = s.Messages, ["settleMs"] = s.ItemDelayMs, ["maxActiveMessages"] = s.Window,
            ["maxConcurrentHandlers"] = s.Handlers, ["replicas"] = s.Replicas, ["scaling"] = s.ScalingMode,
            ["fastScaleUp"] = s.FastScaleUp, ["controlPlaneReplicas"] = s.ControlPlaneReplicas,
            ["lockTtlSeconds"] = s.LockTtlSeconds, ["publishers"] = s.Publishers
        };
        var metrics = Headlines(m, ideal, s.Messages);
        metrics["missing"] = missing;
        metrics["orderViolations"] = 0;
        metrics["deadLettered"] = m.DeadLettered;
        metrics["peakDepth"] = m.PeakDepth;

        return Envelope(s, timestamp, "P-05", "queue-drain", parameters, metrics,
            new JsonObject
            {
                ["messagesPerSecond"] = JsonSerializer.SerializeToNode(m.Timeline.MessagesPerSecond),
                ["busyHandlers"] = JsonSerializer.SerializeToNode(m.Timeline.BusyHandlers)
            },
            m.Timeline.BucketMs, failures);
    }

    /// <summary>
    /// P-04, named and shaped like the SDK harnesses' session drains so the report charts it with them:
    /// busySlots is the sessions being handled at once. Claim latency and the idle-time breakdown need
    /// per-stream timings SessionQueueConsumer doesn't expose, so they're left out. Dead-letters aren't
    /// read (every session has its own DLQ); the handler never fails, so any would show as missing.
    /// </summary>
    private static JsonObject BuildSessions(Settings s, RunMeasurements m, DateTimeOffset timestamp)
    {
        var ideal = SessionPlan.IdealSeconds(s, SessionPlan.Build(s));
        var missing = Math.Max(0, s.Messages - m.MessagesHandled);

        var failures = new List<string>();
        if (!m.Drained)
        {
            failures.Add("the sessions did not drain");
        }
        if (missing > 0)
        {
            failures.Add($"{missing} messages missing");
        }
        if (m.FifoViolations > 0)
        {
            failures.Add($"{m.FifoViolations} FIFO violations");
        }

        var parameters = new JsonObject
        {
            ["sessions"] = s.Sessions, ["messagesPerSession"] = s.MessagesPerSession, ["settleMs"] = s.ItemDelayMs,
            ["maxConcurrentSessions"] = s.ConcurrentSessions, ["prefetchCount"] = s.Prefetch, ["leaseSeconds"] = s.LeaseSeconds,
            ["sessionIdleTimeoutSeconds"] = s.SessionIdleTimeoutSeconds, ["sessionLengthJitterPercent"] = s.SessionLengthJitterPercent,
            ["publishJitterSeconds"] = s.PublishJitterSeconds, ["replicas"] = s.Replicas, ["scaling"] = s.ScalingMode,
            ["fastScaleUp"] = s.FastScaleUp, ["controlPlaneReplicas"] = s.ControlPlaneReplicas,
            ["publishers"] = s.Publishers
        };
        var metrics = Headlines(m, ideal, s.Messages);
        metrics["missing"] = missing;
        metrics["fifoViolations"] = m.FifoViolations;
        metrics["peakDepth"] = m.PeakDepth;
        metrics["scalerDrainSeconds"] = m.ScalerDrainSeconds;

        return Envelope(s, timestamp, "P-04", "session-drain", parameters, metrics,
            new JsonObject
            {
                ["messagesPerSecond"] = JsonSerializer.SerializeToNode(m.Timeline.MessagesPerSecond),
                ["busySlots"] = JsonSerializer.SerializeToNode(m.Timeline.BusyHandlers)
            },
            m.Timeline.BucketMs, failures);
    }

    private static JsonObject Headlines(RunMeasurements m, double? ideal, int messages) => new()
    {
        ["seedSeconds"] = m.PublishSeconds,
        ["wallClockSeconds"] = m.WallClockSeconds,
        ["messagesPerSecond"] = m.WallClockSeconds > 0 ? m.MessagesHandled / m.WallClockSeconds : null,
        ["idealSeconds"] = ideal,
        ["efficiency"] = ideal != null && m.WallClockSeconds > 0 ? ideal / m.WallClockSeconds : null,
        ["timeToFirstMessageSeconds"] = m.TimeToFirstMessageSeconds,
        ["peakConcurrentHandlers"] = m.PeakConcurrentHandlers,
        ["deliveryLatencyMs"] = JsonSerializer.SerializeToNode(m.DeliveryLatencyMs, JsonSerializerOptions.Web),
        ["messagesHandled"] = m.MessagesHandled,
        ["duplicates"] = Math.Max(0, m.MessagesHandled - messages),
    };

    private static JsonObject Envelope(Settings s, DateTimeOffset timestamp, string id, string name, JsonObject parameters,
        JsonObject metrics, JsonObject series, int bucketMs, List<string> failures)
    {
        var profile = Profile(s);
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
                ["id"] = id, ["name"] = name, ["profile"] = profile, ["key"] = Key(s), ["params"] = parameters
            },
            ["metrics"] = metrics,
            ["timeline"] = new JsonObject { ["bucketMs"] = bucketMs, ["series"] = series },
            ["checks"] = new JsonObject
            {
                ["passed"] = failures.Count == 0,
                ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f).ToArray())
            }
        };
    }
}
