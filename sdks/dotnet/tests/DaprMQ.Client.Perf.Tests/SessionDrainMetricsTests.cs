using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class SessionDrainMetricsTests
{
    private static ScenarioParams Scenario(int sessions, int messages, int slots, int settleMs = 1000) =>
        new(sessions, messages, settleMs, slots, PrefetchCount: 10, LeaseSeconds: 30, SessionIdleTimeoutSeconds: 0);

    private static StreamRecord Stream(string? sessionId, double open, double? first, double end, string endReason = StreamEndReasons.Completed) =>
        new(open, first, end, sessionId, endReason);

    private static HandlerRecord Handler(string sessionId, int seq, double start, double end) => new(sessionId, seq, start, end);

    [Fact]
    public void PerfectlyPackedSlots_AreFullyUtilised()
    {
        var streams = new[] { Stream("a", 0, 0, 2000), Stream("b", 0, 0, 2000) };
        var handlers = new[]
        {
            Handler("a", 0, 0, 1000), Handler("a", 1, 1000, 2000),
            Handler("b", 0, 0, 1000), Handler("b", 1, 1000, 2000),
        };

        var result = SessionDrainMetrics.Compute(Scenario(2, 2, 2), streams, handlers, completedMs: 2000, seedSeconds: 0.5);

        Assert.Equal(2.0, result.WallClockSeconds, 3);
        Assert.Equal(2.0, result.IdealSeconds, 3);
        Assert.Equal(1.0, result.Efficiency, 3);
        Assert.Equal(0.5, result.SeedSeconds, 3);
        Assert.Equal(1.0, result.Peak.Utilization, 3);
        Assert.Equal(0.0, result.Peak.IdleSlotSeconds, 3);
        Assert.Equal(4, result.MessagesHandled);
    }

    [Fact]
    public void IdealSeconds_RoundsSessionsUpToWholeSlotRounds()
    {
        var result = SessionDrainMetrics.Compute(Scenario(sessions: 1000, messages: 100, slots: 20), [], [], completedMs: 0, seedSeconds: 0);

        Assert.Equal(5000.0, result.IdealSeconds, 3);

        result = SessionDrainMetrics.Compute(Scenario(sessions: 21, messages: 10, slots: 20, settleMs: 500), [], [], completedMs: 0, seedSeconds: 0);

        Assert.Equal(10.0, result.IdealSeconds, 3); // 2 rounds x 10 msgs x 0.5s
    }

    [Fact]
    public void IdealSeconds_InConcurrentMode_IsBoundedByTheSlowerOfConsumingAndPublishing()
    {
        // Consumer-bound: 1 round x 10 msgs x 1s = 10s. Publish-bound: 10 msgs x (2000 + 500/2)ms + 1s settle = 23.5s.
        var publishBound = Scenario(4, 10, 4) with { PublishMode = PublishModes.Concurrent, PublishIntervalMs = 2000, PublishJitterMs = 500 };
        Assert.Equal(23.5, publishBound.IdealSeconds, 3);

        // Publishing before consuming is outside the clock, so the publish rate doesn't move the ideal.
        Assert.Equal(10.0, (publishBound with { PublishMode = PublishModes.Before }).IdealSeconds, 3);

        // A fast publisher leaves the consumer as the bottleneck.
        Assert.Equal(10.0, (publishBound with { PublishIntervalMs = 100, PublishJitterMs = 0 }).IdealSeconds, 3);
    }

    [Fact]
    public void IdleSlotTime_IsAttributedToClaimDrainAndBetweenStreams()
    {
        // One slot, two single-message sessions. A: claim 100ms, 1s handler, 3s idle-drain.
        // 200ms client gap, then B: claim 100ms, 1s handler, 3s idle-drain (after completion).
        var streams = new[] { Stream("a", 0, 100, 4100), Stream("b", 4300, 4400, 8400) };
        var handlers = new[] { Handler("a", 0, 100, 1100), Handler("b", 0, 4400, 5400) };

        var result = SessionDrainMetrics.Compute(Scenario(2, 1, 1), streams, handlers, completedMs: 5400, seedSeconds: 0);

        // Peak window: first delivery (100) -> last session's first delivery (4400).
        Assert.Equal(0.1, result.Peak.StartSeconds, 3);
        Assert.Equal(4.4, result.Peak.EndSeconds, 3);
        Assert.Equal(4.3, result.Peak.CapacitySlotSeconds, 3);
        Assert.Equal(1.0, result.Peak.HandlingSeconds, 3);
        Assert.Equal(0.1, result.Peak.ClaimSeconds, 3);
        Assert.Equal(3.0, result.Peak.DrainWaitSeconds, 3);
        Assert.Equal(0.2, result.Peak.BetweenStreamsSeconds, 3);
        Assert.Equal(0.0, result.Peak.InSessionWaitSeconds, 3);
        Assert.Equal(3.3, result.Peak.IdleSlotSeconds, 3);
        Assert.Equal(1.0 / 4.3, result.Peak.Utilization, 3);

        // Overall window: 0 -> completion. B's drain lands after completion, so is excluded.
        Assert.Equal(5.4, result.Overall.CapacitySlotSeconds, 3);
        Assert.Equal(2.0, result.Overall.HandlingSeconds, 3);
        Assert.Equal(0.2, result.Overall.ClaimSeconds, 3);
        Assert.Equal(3.0, result.Overall.DrainWaitSeconds, 3);
        Assert.Equal(0.2, result.Overall.BetweenStreamsSeconds, 3);

        Assert.Equal(1.0, result.TailSeconds, 3);
        Assert.Equal(0.1, result.TimeToFirstMessageSeconds, 3);
        Assert.Equal(2, result.ClaimLatencyMs.Count);
        Assert.Equal(100, result.ClaimLatencyMs.P50, 3);
        Assert.Equal(3000, result.DrainWaitMs.Max, 3);
    }

    [Fact]
    public void GapsBetweenHandlersInOneStream_AreInSessionWait()
    {
        var streams = new[] { Stream("a", 0, 0, 2500) };
        var handlers = new[] { Handler("a", 0, 0, 1000), Handler("a", 1, 1500, 2500) };

        var result = SessionDrainMetrics.Compute(Scenario(1, 2, 1), streams, handlers, completedMs: 2500, seedSeconds: 0);

        Assert.Equal(0.5, result.Overall.InSessionWaitSeconds, 3);
        Assert.Equal(1, result.InterMessageGapMs.Count);
        Assert.Equal(500, result.InterMessageGapMs.P50, 3);
    }

    [Fact]
    public void StreamThatNeverDelivered_CountsAsFailedClaim()
    {
        var streams = new[] { Stream(null, 0, null, 300, endReason: "NoSessionsAvailableException"), Stream("a", 300, 400, 1400) };
        var handlers = new[] { Handler("a", 0, 400, 1400) };

        var result = SessionDrainMetrics.Compute(Scenario(1, 1, 1), streams, handlers, completedMs: 1400, seedSeconds: 0);

        Assert.Equal(1, result.FailedClaims);
        Assert.Equal(2, result.Streams);
        Assert.Equal(0.4, result.Overall.ClaimSeconds, 3);
    }

    [Fact]
    public void SessionClaimedAgain_IsCounted()
    {
        var streams = new[] { Stream("a", 0, 0, 1000), Stream("a", 1000, 1100, 2100) };
        var handlers = new[] { Handler("a", 0, 0, 1000), Handler("a", 1, 1100, 2100) };

        var result = SessionDrainMetrics.Compute(Scenario(1, 2, 1), streams, handlers, completedMs: 2100, seedSeconds: 0);

        Assert.Equal(1, result.SessionsClaimedMoreThanOnce);
    }

    [Fact]
    public void OrderingDuplicatesAndMissingMessages_AreDetected()
    {
        var handlers = new[]
        {
            Handler("a", 0, 0, 10), Handler("a", 2, 10, 20), Handler("a", 1, 20, 30), // out of order
            Handler("b", 0, 0, 10), Handler("b", 0, 10, 20),                          // redelivered
        };

        var result = SessionDrainMetrics.Compute(Scenario(2, 3, 2), [], handlers, completedMs: 30, seedSeconds: 0);

        Assert.Equal(1, result.FifoViolations);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(2, result.Missing); // b/1, b/2
        Assert.Equal(5, result.MessagesHandled);
    }

    [Fact]
    public void DeliveryLatency_IsDistributedOverHandlersThatRecordedIt()
    {
        var handlers = new[]
        {
            new HandlerRecord("a", 0, 0, 10, DeliveryLatencyMs: 100),
            new HandlerRecord("a", 1, 10, 20, DeliveryLatencyMs: 300),
            new HandlerRecord("a", 2, 20, 30),
        };

        var result = SessionDrainMetrics.Compute(Scenario(1, 3, 1), [Stream("a", 0, 0, 30)], handlers, completedMs: 30, seedSeconds: 0);

        Assert.Equal(2, result.DeliveryLatencyMs.Count);
        Assert.Equal(100, result.DeliveryLatencyMs.P50, 3);
        Assert.Equal(300, result.DeliveryLatencyMs.Max, 3);
    }

    [Fact]
    public void Timeline_ReportsAverageBusySlotsPerSecond()
    {
        var handlers = new[] { Handler("a", 0, 500, 2500) };

        var result = SessionDrainMetrics.Compute(Scenario(1, 1, 1), [Stream("a", 0, 500, 2500)], handlers, completedMs: 2500, seedSeconds: 0);

        Assert.Equal(1000, result.TimelineBucketMs);
        Assert.Equal([0.5, 1.0, 0.5], result.BusySlotsTimeline);
    }
}
