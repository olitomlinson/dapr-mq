using System.Text.Json.Nodes;

namespace ThroughputTest.Tests;

public class SessionRunRecordTests
{
    private static Settings TwentyReplicas(string mode = "keda") => Settings.Parse(new Dictionary<string, string>
    {
        ["QUEUE_ID"] = "tp-1", ["WORKLOAD"] = "sessions", ["REPLICAS"] = "20", ["SCALING_MODE"] = mode,
        ["ENV_LABEL"] = "ci-ubuntu-latest", ["GATEWAY_REPLICAS"] = "3", ["GIT_SHA"] = "abc123", ["GIT_BRANCH"] = "main",
        ["SERVER_LABEL"] = "kubernetes kind-daprmq"
    });

    private static RunMeasurements Drained(double wallClockSeconds = 250, long handled = 100_000, int fifoViolations = 0) => new(
        Drained: true, PublishSeconds: 6, WallClockSeconds: wallClockSeconds, PeakDepth: 1000, DeadLettered: 0,
        MessagesHandled: handled, TimeToFirstMessageSeconds: 2.5, PeakConcurrentHandlers: 200,
        DeliveryLatencyMs: new Distribution(100_000, 150_000, 140_000, 280_000, 295_000, 300_000),
        Timeline: new StatsTimeline(5_000, [300.0, 340.0], [190.0, 200.0]))
    {
        FifoViolations = fifoViolations,
        ScalerDrainSeconds = wallClockSeconds + 35
    };

    private static JsonObject Build(Settings settings, RunMeasurements m) =>
        RunRecord.Build(settings, m, new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Envelope_IsAP04SessionDrain_WithItsOwnK8sProfile()
    {
        var run = Build(TwentyReplicas(), Drained());

        Assert.Equal("20261020T140000Z_dotnet_ci-ubuntu-latest_k8s-session-drain-r20", run["runId"]!.GetValue<string>());
        Assert.Equal("P-04", run["scenario"]!["id"]!.GetValue<string>());
        Assert.Equal("session-drain", run["scenario"]!["name"]!.GetValue<string>());
        Assert.Equal("k8s-session-drain-r20", run["scenario"]!["profile"]!.GetValue<string>());
        Assert.Equal("extreme", run["scale"]!.GetValue<string>());
        Assert.Equal(3, run["topology"]!["apiReplicas"]!.GetValue<int>());
    }

    [Fact]
    public void Key_GroupsOnlyRunsOfTheSameShape()
    {
        Assert.Equal("k8s-sessions:1000x100@200ms/10slots/prefetch1/lease30s/idle1s/replicas20",
            Build(TwentyReplicas(), Drained())["scenario"]!["key"]!.GetValue<string>());
        Assert.Equal("k8s-sessions:1000x100@200ms/10slots/prefetch1/lease30s/idle1s/replicas20+fixed",
            Build(TwentyReplicas("fixed"), Drained())["scenario"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void Key_NamesTheJitter_OnlyWhenItIsOn()
    {
        var settings = Settings.Parse(new Dictionary<string, string>
        {
            ["QUEUE_ID"] = "tp-1", ["WORKLOAD"] = "sessions", ["REPLICAS"] = "20",
            ["SESSION_LENGTH_JITTER_PERCENT"] = "50", ["PUBLISH_JITTER_SECONDS"] = "30"
        });

        Assert.Equal("k8s-sessions:1000x100@200ms/10slots/prefetch1/lease30s/idle1s/replicas20+len50+pubjitter30s",
            Build(settings, Drained())["scenario"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void IdealSeconds_WithJitter_IsTheLowerBound_OverThePlannedSessions()
    {
        // The work spread over every slot, or the session that finishes last at best - published at its
        // offset, then handled one message after another - whichever is later.
        var settings = Settings.Parse(new Dictionary<string, string>
        {
            ["QUEUE_ID"] = "tp-1", ["WORKLOAD"] = "sessions", ["REPLICAS"] = "20",
            ["SESSION_LENGTH_JITTER_PERCENT"] = "50", ["PUBLISH_JITTER_SECONDS"] = "30"
        });
        var plan = SessionPlan.Build(settings);
        var expected = Math.Max(100_000 * 0.2 / 200, plan.Max(s => s.PublishAtSeconds + s.Messages * 0.2));

        Assert.Equal(expected, Build(settings, Drained())["metrics"]!["idealSeconds"]!.GetValue<double>(), 6);
    }

    [Fact]
    public void Key_NamesAFastScaleUp()
    {
        Assert.Equal("k8s-sessions:1000x100@200ms/10slots/prefetch1/lease30s/idle1s/replicas20+fastscale",
            Build(TwentyReplicas() with { FastScaleUp = true }, Drained())["scenario"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void Params_DescribeTheSessions()
    {
        var parameters = Build(TwentyReplicas(), Drained())["scenario"]!["params"]!;

        Assert.Equal(1000, parameters["sessions"]!.GetValue<int>());
        Assert.Equal(100, parameters["messagesPerSession"]!.GetValue<int>());
        Assert.Equal(200, parameters["settleMs"]!.GetValue<int>());
        Assert.Equal(10, parameters["maxConcurrentSessions"]!.GetValue<int>());
        Assert.Equal(1, parameters["prefetchCount"]!.GetValue<int>());
        Assert.Equal(30, parameters["leaseSeconds"]!.GetValue<int>());
        Assert.Equal(1, parameters["sessionIdleTimeoutSeconds"]!.GetValue<int>());
        Assert.Equal(20, parameters["replicas"]!.GetValue<int>());
    }

    [Fact]
    public void Metrics_AreTheSessionDrainHeadlines()
    {
        var metrics = Build(TwentyReplicas(), Drained(wallClockSeconds: 250))["metrics"]!;

        Assert.Equal(400, metrics["messagesPerSecond"]!.GetValue<double>(), 3);
        // 100,000 x 200ms over 20 replicas x 10 sessions = 100s, and no session's own 100 x 200ms is longer.
        Assert.Equal(100, metrics["idealSeconds"]!.GetValue<double>(), 3);
        Assert.Equal(0.4, metrics["efficiency"]!.GetValue<double>(), 3);
        Assert.Equal(0, metrics["fifoViolations"]!.GetValue<int>());
        Assert.Equal(1000, metrics["peakDepth"]!.GetValue<double>());
        Assert.Equal(285, metrics["scalerDrainSeconds"]!.GetValue<double>());
    }

    [Fact]
    public void IdealSeconds_IsNeverLessThanOneSessionsOwnWork()
    {
        // 10 sessions of 1,000: 200 slots, but only 10 sessions to run at once, each 1,000 x 200ms long.
        var settings = Settings.Parse(new Dictionary<string, string>
        {
            ["QUEUE_ID"] = "tp-1", ["WORKLOAD"] = "sessions", ["REPLICAS"] = "20", ["SESSIONS"] = "10", ["MESSAGES"] = "10000"
        });

        Assert.Equal(200, Build(settings, Drained())["metrics"]!["idealSeconds"]!.GetValue<double>(), 3);
    }

    [Fact]
    public void Timeline_CarriesBusySlots_AsTheSessionDrainChartsExpect()
    {
        var series = Build(TwentyReplicas(), Drained())["timeline"]!["series"]!;

        Assert.Equal(2, series["busySlots"]!.AsArray().Count);
        Assert.Equal(2, series["messagesPerSecond"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(false, 100_000, 0, "the sessions did not drain")]
    [InlineData(true, 99_000, 0, "1000 messages missing")]
    [InlineData(true, 100_000, 3, "3 FIFO violations")]
    public void Checks_FailWhenTheRunDidNotDeliverEveryMessageInOrder(bool drained, long handled, int fifo, string failure)
    {
        var run = Build(TwentyReplicas(), Drained(handled: handled, fifoViolations: fifo) with { Drained = drained });

        Assert.False(run["checks"]!["passed"]!.GetValue<bool>());
        Assert.Contains(failure, run["checks"]!["failures"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public void Checks_PassForACleanDrain()
    {
        Assert.True(Build(TwentyReplicas(), Drained())["checks"]!["passed"]!.GetValue<bool>());
    }
}
