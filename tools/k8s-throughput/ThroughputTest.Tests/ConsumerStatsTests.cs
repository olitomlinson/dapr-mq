namespace ThroughputTest.Tests;

public class ConsumerStatsTests
{
    private static ConsumerStatsReport Report(string pod, long seq, long sentAtMs, long handled, int busy, long? firstHandledMs = 1_000) =>
        new(pod, seq, sentAtMs, handled, busy, firstHandledMs, new LatencyHistogram().Snapshot());

    [Fact]
    public void Latest_KeepsEachPodsMostRecentReport_HoweverTheyArrive()
    {
        // Reports are cumulative, so the newest per pod is all that matters, and a lost or repeated
        // report changes nothing.
        var reports = new[]
        {
            Report("a", 2, 7_000, 40, 3),
            Report("a", 1, 2_000, 10, 3),
            Report("b", 1, 2_000, 15, 2),
            Report("a", 2, 7_000, 40, 3),
        };

        var latest = ConsumerStats.Latest(reports);

        Assert.Equal(40 + 15, latest.Sum(r => r.Handled));
    }

    [Fact]
    public void Timeline_BucketsTheRunsHandledMessagesAndBusyHandlers_AcrossPods()
    {
        // t = 0 at the first publish (1,000 ms). Pod a reports at 2s and 7s, pod b at 3s and 8s.
        var reports = new[]
        {
            Report("a", 1, 3_000, 100, 4), Report("a", 2, 8_000, 600, 2),
            Report("b", 1, 4_000, 50, 3), Report("b", 2, 9_000, 350, 1),
        };

        var timeline = ConsumerStats.Timeline(reports, startMs: 1_000, bucketMs: 5_000, endMs: 11_000);

        // Bucket [0s,5s): a=100, b=50 -> 150 handled; busy at the latest reports, 4 + 3.
        // Bucket [5s,10s): a=600, b=350 -> 800 more; busy 2 + 1.
        Assert.Equal([150 / 5.0, 800 / 5.0], timeline.MessagesPerSecond);
        Assert.Equal([7.0, 3.0], timeline.BusyHandlers);
    }

    [Fact]
    public void LastHandled_IsTheLatestAcrossPods()
    {
        var reports = new[]
        {
            Report("a", 1, 9_000, 1, 0) with { LastHandledMs = 8_200 },
            Report("b", 1, 9_000, 1, 0) with { LastHandledMs = 8_900 },
            Report("c", 1, 9_000, 0, 0)
        };

        Assert.Equal(8_900, ConsumerStats.LastHandledMs(reports));
    }

    [Fact]
    public void FifoViolations_AddUpAcrossPods()
    {
        var reports = new[] { Report("a", 1, 9_000, 1, 0) with { FifoViolations = 2 }, Report("b", 1, 9_000, 1, 0) with { FifoViolations = 1 } };

        Assert.Equal(3, reports.Sum(r => r.FifoViolations));
    }

    [Fact]
    public void FirstHandled_IsTheEarliestAcrossPods()
    {
        var reports = new[] { Report("a", 1, 9_000, 1, 0, firstHandledMs: 4_200), Report("b", 1, 9_000, 1, 0, firstHandledMs: 3_100) };

        Assert.Equal(3_100, ConsumerStats.FirstHandledMs(reports));
    }
}
