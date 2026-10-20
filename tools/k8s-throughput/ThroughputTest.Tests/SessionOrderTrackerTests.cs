namespace ThroughputTest.Tests;

public class SessionOrderTrackerTests
{
    [Fact]
    public void InOrderDeliveries_AreNeitherViolationsNorRedeliveries()
    {
        var tracker = new SessionOrderTracker();

        foreach (var seq in new[] { 0L, 1, 2 })
        {
            tracker.Observe("s1", seq);
            tracker.Observe("s2", seq);
        }

        Assert.Equal(0, tracker.FifoViolations);
        Assert.Equal(0, tracker.Redeliveries);
    }

    [Fact]
    public void AMessageThatOvertakesAnEarlierOne_IsAViolation()
    {
        var tracker = new SessionOrderTracker();

        tracker.Observe("s1", 0);
        tracker.Observe("s1", 2);
        tracker.Observe("s1", 1); // 1 arrives after 2, the first time it's seen

        Assert.Equal(1, tracker.FifoViolations);
    }

    [Fact]
    public void ASeenMessageComingBack_IsARedelivery_NotAViolation()
    {
        // A lapsed lease returns a session's locked messages to its front, so they come round again.
        var tracker = new SessionOrderTracker();

        tracker.Observe("s1", 0);
        tracker.Observe("s1", 1);
        tracker.Observe("s1", 0);
        tracker.Observe("s1", 1);
        tracker.Observe("s1", 2);

        Assert.Equal(0, tracker.FifoViolations);
        Assert.Equal(2, tracker.Redeliveries);
    }

    [Fact]
    public void SessionsAreTrackedIndependently()
    {
        var tracker = new SessionOrderTracker();

        tracker.Observe("s1", 5);
        tracker.Observe("s2", 0);

        Assert.Equal(0, tracker.FifoViolations);
    }
}
