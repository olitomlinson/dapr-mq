using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class PerfOptionsTests
{
    [Fact]
    public void Defaults_AreTheFullScenario()
    {
        var options = PerfOptions.Parse([]);

        Assert.Equal("full", options.Profile);
        Assert.Equal(1000, options.Sessions);
        Assert.Equal(100, options.MessagesPerSession);
        Assert.Equal(1000, options.SettleMs);
        Assert.Equal(20, options.MaxConcurrentSessions);
        Assert.Equal(10, options.PrefetchCount);
        Assert.Equal(30, options.LeaseSeconds);
        Assert.Equal(0, options.SessionIdleTimeoutSeconds);
        Assert.Equal(PublishModes.Before, options.PublishMode);
        Assert.Null(options.HttpEndpoint);
        Assert.False(options.ReportOnly);
    }

    [Fact]
    public void PublishMode_CanStartConsumingAlongsidePublishing()
    {
        var options = PerfOptions.Parse(["--publish-mode", "concurrent"]);

        Assert.Equal(PublishModes.Concurrent, options.PublishMode);
        Assert.Equal(PublishModes.Concurrent, options.Scenario.PublishMode);
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--publish-mode", "sometimes"]));
    }

    [Fact]
    public void ScenarioKey_SeparatesConcurrentPublishRuns_AndKeepsExistingKeysUnchanged()
    {
        Assert.Equal("1000x100@1000ms/20slots", PerfOptions.Parse([]).Scenario.Key);
        Assert.Equal("1000x100@1000ms/20slots+concurrent", PerfOptions.Parse(["--publish-mode", "concurrent"]).Scenario.Key);
    }

    [Fact]
    public void PublishRate_DefaultsToUnthrottled_AndRejectsNegatives()
    {
        var options = PerfOptions.Parse(["--publish-interval-ms", "200", "--publish-jitter-ms", "50"]);

        Assert.Equal(0, PerfOptions.Parse([]).PublishIntervalMs);
        Assert.Equal(0, PerfOptions.Parse([]).PublishJitterMs);
        Assert.Equal(200, options.Scenario.PublishIntervalMs);
        Assert.Equal(50, options.Scenario.PublishJitterMs);
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--publish-interval-ms", "-1"]));
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--publish-jitter-ms", "-1"]));
    }

    [Fact]
    public void ScenarioKey_SeparatesRateLimitedPublishRuns()
    {
        Assert.Equal("1000x100@1000ms/20slots+concurrent+pub200ms~50ms",
            PerfOptions.Parse(["--publish-mode", "concurrent", "--publish-interval-ms", "200", "--publish-jitter-ms", "50"]).Scenario.Key);
        Assert.Equal("1000x100@1000ms/20slots+pub0ms~50ms", PerfOptions.Parse(["--publish-jitter-ms", "50"]).Scenario.Key);
    }

    [Fact]
    public void QuickProfile_ShrinksTheBacklog_AndExplicitFlagsWinRegardlessOfOrder()
    {
        var options = PerfOptions.Parse(["--sessions", "40", "--profile", "quick", "--idle-timeout", "2"]);

        Assert.Equal("quick", options.Profile);
        Assert.Equal(40, options.Sessions);
        Assert.Equal(20, options.MessagesPerSession);
        Assert.Equal(2, options.SessionIdleTimeoutSeconds);
    }

    [Fact]
    public void ExistingServer_RequiresBothEndpoints()
    {
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--http", "http://localhost:8002"]));

        var options = PerfOptions.Parse(["--http", "http://localhost:8002", "--grpc", "http://localhost:8102"]);

        Assert.Equal("http://localhost:8002", options.HttpEndpoint);
        Assert.Equal("http://localhost:8102", options.GrpcEndpoint);
    }

    [Theory]
    [InlineData("steady-drain", 200, 20, 100, 20, 1, PublishModes.Before)]
    [InlineData("session-churn", 300, 2, 50, 20, 1, PublishModes.Before)]
    [InlineData("deep-session", 4, 1000, 10, 4, 1, PublishModes.Before)]
    [InlineData("live-publish", 20, 50, 100, 20, 1, PublishModes.Concurrent)]
    [InlineData("sdk-defaults", 40, 5, 1000, 20, 0, PublishModes.Before)]
    public void CiProfiles_HaveTheirShapes(string profile, int sessions, int messages, int settleMs, int slots, int idleTimeout, string publishMode)
    {
        var options = PerfOptions.Parse(["--profile", profile]);

        Assert.Equal(profile, options.Profile);
        Assert.Equal(sessions, options.Sessions);
        Assert.Equal(messages, options.MessagesPerSession);
        Assert.Equal(settleMs, options.SettleMs);
        Assert.Equal(slots, options.MaxConcurrentSessions);
        Assert.Equal(idleTimeout, options.SessionIdleTimeoutSeconds);
        Assert.Equal(publishMode, options.PublishMode);
    }

    [Fact]
    public void LivePublish_IsRateLimited()
    {
        var options = PerfOptions.Parse(["--profile", "live-publish"]);

        Assert.Equal(200, options.PublishIntervalMs);
        Assert.Equal(100, options.PublishJitterMs);
    }

    [Fact]
    public void PrSuite_ExpandsToEveryPrProfile_SharingRunSettings()
    {
        var options = PerfOptions.Parse(["--suite", "pr", "--env-label", "ci-x", "--http", "http://h", "--grpc", "http://g"]);

        var runs = options.Runs();

        Assert.Equal(
            ["enqueue", "enqueue-hot", "enqueue-batch", "dequeue-ack", "steady-drain", "session-churn", "deep-session", "live-publish", "sdk-defaults"],
            runs.Select(r => r.Profile));
        Assert.All(runs, r =>
        {
            Assert.Equal("ci-x", r.EnvLabel);
            Assert.Equal("http://h", r.HttpEndpoint);
            Assert.Equal("http://g", r.GrpcEndpoint);
            Assert.Equal("pr", r.Scale);
            Assert.Equal(1, r.ApiReplicas);
        });
    }

    [Fact]
    public void ExtremeSuite_RunsTheRampsAndBigDrains_OnThreeReplicasByDefault()
    {
        var runs = PerfOptions.Parse(["--suite", "extreme"]).Runs();

        Assert.Equal(["enqueue-ramp", "enqueue-hot-ramp", "enqueue-batch-ramp", "dequeue-ack-ramp", "full", "wide-drain"], runs.Select(r => r.Profile));
        Assert.All(runs, r => Assert.Equal(3, r.ApiReplicas));
        Assert.All(runs, r => Assert.Equal("extreme", r.Scale));
        Assert.Equal(5, PerfOptions.Parse(["--suite", "extreme", "--api-replicas", "5"]).ApiReplicas);
    }

    [Fact]
    public void LoadProfiles_MatchTheSpec()
    {
        var enqueue = PerfOptions.Parse(["--profile", "enqueue"]).Load!;
        Assert.Equal(new LoadParams("enqueue", [8], Queues: 8, BatchSize: 1, DequeueCount: 1, PayloadBytes: 256, SeedPerQueue: 0, WarmupSeconds: 3, DurationSeconds: 15), enqueue);
        Assert.Equal("P-01", enqueue.Id);
        Assert.Equal("enqueue:c8/q8/b1/256B/3+15s", enqueue.Key);

        Assert.Equal(1, PerfOptions.Parse(["--profile", "enqueue-hot"]).Load!.Queues);
        Assert.Equal(100, PerfOptions.Parse(["--profile", "enqueue-batch"]).Load!.BatchSize);
        Assert.Equal("P-03", PerfOptions.Parse(["--profile", "dequeue-ack"]).Load!.Id);

        var ramp = PerfOptions.Parse(["--profile", "enqueue-ramp"]).Load!;
        Assert.Equal([1, 2, 4, 8, 16, 32, 64, 128, 256], ramp.Concurrency);
        Assert.Null(ramp.Queues); // = concurrency at each step
        Assert.Equal("enqueue:c1,2,4,8,16,32,64,128,256/q=c/b1/256B/5+30s", ramp.Key);

        Assert.Null(PerfOptions.Parse(["--profile", "steady-drain"]).Load);
    }

    [Fact]
    public void Scale_IsTheProfilesOwn_UnlessItsParametersWereOverridden()
    {
        Assert.Equal("pr", PerfOptions.Parse(["--profile", "steady-drain"]).Scale);
        Assert.Equal("extreme", PerfOptions.Parse(["--profile", "full"]).Scale);
        Assert.Equal("adhoc", PerfOptions.Parse(["--profile", "quick"]).Scale);
        Assert.Equal("adhoc", PerfOptions.Parse(["--profile", "steady-drain", "--sessions", "5"]).Scale);
    }

    [Fact]
    public void WithoutSuite_RunsIsJustThisRun()
    {
        var options = PerfOptions.Parse(["--profile", "quick"]);

        Assert.Equal([options], options.Runs());
    }

    [Fact]
    public void Suite_RejectsScenarioOverridesAndUnknownSuites()
    {
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--suite", "pr", "--sessions", "5"]));
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--suite", "pr", "--profile", "quick"]));
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--suite", "ci"]));
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--suite", "nightly"]));
    }

    [Fact]
    public void Gate_And_BaselineBranch_Parse()
    {
        Assert.False(PerfOptions.Parse([]).Gate);
        Assert.Equal("main", PerfOptions.Parse([]).BaselineBranch);

        var options = PerfOptions.Parse(["--gate", "--baseline-branch", "release"]);

        Assert.True(options.Gate);
        Assert.Equal("release", options.BaselineBranch);
    }

    [Fact]
    public void ScenarioKey_IncludesNonDefaultConsumerSettings()
    {
        Assert.Equal("1000x100@1000ms/20slots+idle2s", PerfOptions.Parse(["--idle-timeout", "2"]).Scenario.Key);
        Assert.Equal("1000x100@1000ms/20slots+idle1s+prefetch5+lease10s",
            PerfOptions.Parse(["--idle-timeout", "1", "--prefetch", "5", "--lease", "10"]).Scenario.Key);
    }

    [Fact]
    public void UnknownFlag_Throws()
    {
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--nope"]));
    }

    [Fact]
    public void Benchmark_DefaultsToSessionDrain_AndAcceptsStateReads()
    {
        Assert.Equal(Benchmarks.SessionDrain, PerfOptions.Parse([]).Benchmark);
        Assert.Equal(Benchmarks.StateReads, PerfOptions.Parse(["--benchmark", "state-reads"]).Benchmark);
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--benchmark", "nope"]));
    }

    [Theory]
    [InlineData("--suite", "pr")]
    [InlineData("--api-replicas", "2")]
    [InlineData("--sessions", "10")]
    [InlineData("--profile", "quick")]
    public void StateReads_RejectsScenarioAndTopologyFlags(string flag, string value)
    {
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--benchmark", "state-reads", flag, value]));
    }

    [Fact]
    public void StateReads_NeedsItsOwnStack_SoRejectsAnExternalServer()
    {
        Assert.Throws<ArgumentException>(() =>
            PerfOptions.Parse(["--benchmark", "state-reads", "--http", "http://x", "--grpc", "http://y"]));
    }

    [Fact]
    public void ApiReplicas_MustBePositive_AndCantCombineWithAnExternalServer()
    {
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--api-replicas", "0"]));
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse(["--api-replicas", "2", "--http", "http://x", "--grpc", "http://y"]));
    }

    [Fact]
    public void OutDir_DefaultsToTheRepoWidePerfResults()
    {
        Assert.Equal("perf-results", Path.GetFileName(PerfOptions.Parse([]).OutDir));
    }
}
