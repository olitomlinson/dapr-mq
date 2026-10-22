using System.Text.Json.Nodes;

namespace ThroughputTest.Tests;

public class RunRecordTests
{
    private static Settings TwentyReplicas(string mode = "keda") => Settings.Parse(new Dictionary<string, string>
    {
        ["QUEUE_ID"] = "tp-1", ["REPLICAS"] = "20", ["SCALING_MODE"] = mode, ["ENV_LABEL"] = "ci-ubuntu-latest",
        ["GATEWAY_REPLICAS"] = "3", ["GIT_SHA"] = "abc123", ["GIT_BRANCH"] = "main", ["SERVER_LABEL"] = "kubernetes kind-daprmq"
    });

    private static RunMeasurements Drained(double wallClockSeconds = 300, long handled = 100_000, double deadLettered = 0) => new(
        Drained: true, PublishSeconds: 6, WallClockSeconds: wallClockSeconds, PeakDepth: 99_000, DeadLettered: deadLettered,
        MessagesHandled: handled, TimeToFirstMessageSeconds: 2.5, PeakConcurrentHandlers: 200,
        DeliveryLatencyMs: new Distribution(100_000, 150_000, 140_000, 280_000, 295_000, 300_000),
        Timeline: new StatsTimeline(5_000, [300.0, 340.0], [190.0, 200.0]));

    private static JsonObject Build(Settings settings, RunMeasurements m) =>
        RunRecord.Build(settings, m, new DateTimeOffset(2026, 10, 19, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Envelope_FollowsSchema2_AsAP05QueueDrainOfTheDotnetSdk()
    {
        var run = Build(TwentyReplicas(), Drained());

        Assert.Equal(2, run["schemaVersion"]!.GetValue<int>());
        Assert.Equal("20261019T140000Z_dotnet_ci-ubuntu-latest_k8s-queue-drain-r20", run["runId"]!.GetValue<string>());
        Assert.Equal("dotnet", run["sdk"]!["name"]!.GetValue<string>());
        Assert.Equal("ci-ubuntu-latest", run["environment"]!["label"]!.GetValue<string>());
        Assert.Equal("abc123", run["environment"]!["gitSha"]!.GetValue<string>());
        Assert.Equal(3, run["topology"]!["apiReplicas"]!.GetValue<int>());
        Assert.Equal("kubernetes kind-daprmq", run["topology"]!["server"]!.GetValue<string>());
        Assert.Equal("extreme", run["scale"]!.GetValue<string>());
        Assert.Equal("P-05", run["scenario"]!["id"]!.GetValue<string>());
        Assert.Equal("queue-drain", run["scenario"]!["name"]!.GetValue<string>());
        Assert.Equal("k8s-queue-drain-r20", run["scenario"]!["profile"]!.GetValue<string>());
    }

    [Fact]
    public void Key_GroupsOnlyRunsOfTheSameShape()
    {
        // Runs are compared by (environment.label, topology.apiReplicas, scenario.key), so every
        // parameter that changes the result has to be in the key.
        Assert.Equal("k8s-queue:100000@200ms/active40/handlers10/replicas20",
            Build(TwentyReplicas(), Drained())["scenario"]!["key"]!.GetValue<string>());
        Assert.Equal("k8s-queue:100000@200ms/active40/handlers10/replicas20+fixed",
            Build(TwentyReplicas("fixed"), Drained())["scenario"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void Key_NamesAFastScaleUp_OnlyWithKeda()
    {
        // The HPA's ramp is part of every KEDA run's wall clock, so runs that skip it aren't comparable
        // with those that don't. A fixed run has no ramp either way.
        Settings With(string mode) => TwentyReplicas(mode) with { FastScaleUp = true };

        Assert.Equal("k8s-queue:100000@200ms/active40/handlers10/replicas20+fastscale",
            Build(With("keda"), Drained())["scenario"]!["key"]!.GetValue<string>());
        Assert.Equal("k8s-queue:100000@200ms/active40/handlers10/replicas20+fixed",
            Build(With("fixed"), Drained())["scenario"]!["key"]!.GetValue<string>());
    }

    [Fact]
    public void Key_NamesASplitControlPlane()
    {
        // Coordinators on their own deployment is a different system under test, so its runs are only
        // compared with each other.
        var run = Build(TwentyReplicas() with { ControlPlaneReplicas = 2 }, Drained());

        Assert.Equal("k8s-queue:100000@200ms/active40/handlers10/replicas20+controlplane",
            run["scenario"]!["key"]!.GetValue<string>());
        Assert.Equal(2, run["scenario"]!["params"]!["controlPlaneReplicas"]!.GetValue<int>());
    }

    [Fact]
    public void Metrics_AreTheQueueDrainHeadlines()
    {
        var metrics = Build(TwentyReplicas(), Drained(wallClockSeconds: 250))["metrics"]!;

        Assert.Equal(400, metrics["messagesPerSecond"]!.GetValue<double>(), 3);
        Assert.Equal(250, metrics["wallClockSeconds"]!.GetValue<double>());
        // 100,000 x 200ms over 20 replicas x 10 handlers = 100s at best.
        Assert.Equal(100, metrics["idealSeconds"]!.GetValue<double>(), 3);
        Assert.Equal(0.4, metrics["efficiency"]!.GetValue<double>(), 3);
        Assert.Equal(280_000, metrics["deliveryLatencyMs"]!["p95"]!.GetValue<double>());
        Assert.Equal(200, metrics["peakConcurrentHandlers"]!.GetValue<int>());
        Assert.Equal(100_000, metrics["messagesHandled"]!.GetValue<long>());
    }

    [Fact]
    public void Timeline_CarriesThePerBucketSeries()
    {
        var timeline = Build(TwentyReplicas(), Drained())["timeline"]!;

        Assert.Equal(5_000, timeline["bucketMs"]!.GetValue<int>());
        Assert.Equal(2, timeline["series"]!["messagesPerSecond"]!.AsArray().Count);
        Assert.Equal(2, timeline["series"]!["busyHandlers"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(false, 100_000, 0, "the queue did not drain")]
    [InlineData(true, 99_000, 0, "1000 messages missing")]
    [InlineData(true, 100_000, 12, "12 messages dead-lettered")]
    public void Checks_FailWhenTheRunDidNotDeliverEveryMessage(bool drained, long handled, double deadLettered, string failure)
    {
        var run = Build(TwentyReplicas(), Drained(handled: handled, deadLettered: deadLettered) with { Drained = drained });

        Assert.False(run["checks"]!["passed"]!.GetValue<bool>());
        Assert.Contains(failure, run["checks"]!["failures"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public void Checks_PassForACleanDrain()
    {
        Assert.True(Build(TwentyReplicas(), Drained())["checks"]!["passed"]!.GetValue<bool>());
    }
}
