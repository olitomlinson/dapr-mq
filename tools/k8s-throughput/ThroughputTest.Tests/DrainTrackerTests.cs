namespace ThroughputTest.Tests;

public class DrainTrackerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void AnEmptyQueue_BeforePublishingFinishes_IsNotDrained()
    {
        // Consumers can empty the queue between publisher batches: that isn't the end of the run.
        var tracker = new DrainTracker(confirmations: 2);

        tracker.Observe(0, S(1), publishingDone: false);
        tracker.Observe(0, S(2), publishingDone: false);

        Assert.False(tracker.Drained);
    }

    [Fact]
    public void Drained_NeedsConsecutiveEmptyReads_AndDatesFromTheFirstOfThem()
    {
        // The scaler caches depth for a second, so one zero could be stale; the drain time is still
        // when the queue was first seen empty, not when that was confirmed.
        var tracker = new DrainTracker(confirmations: 2);

        tracker.Observe(500, S(10), publishingDone: true);
        tracker.Observe(0, S(11), publishingDone: true);
        Assert.False(tracker.Drained);
        tracker.Observe(0, S(12), publishingDone: true);

        Assert.True(tracker.Drained);
        Assert.Equal(S(11), tracker.DrainedAt);
    }

    [Fact]
    public void ANonEmptyRead_ResetsTheStreak()
    {
        // An expired lock returns its item to the queue: depth can go back up after reading zero.
        var tracker = new DrainTracker(confirmations: 2);

        tracker.Observe(0, S(11), publishingDone: true);
        tracker.Observe(40, S(12), publishingDone: true);
        tracker.Observe(0, S(13), publishingDone: true);
        tracker.Observe(0, S(14), publishingDone: true);

        Assert.Equal(S(13), tracker.DrainedAt);
    }

    [Fact]
    public void PeakDepth_IsTheLargestDepthSeen()
    {
        var tracker = new DrainTracker(confirmations: 2);

        tracker.Observe(300, S(1), publishingDone: false);
        tracker.Observe(90_000, S(2), publishingDone: true);
        tracker.Observe(10, S(3), publishingDone: true);

        Assert.Equal(90_000, tracker.PeakDepth);
    }
}
