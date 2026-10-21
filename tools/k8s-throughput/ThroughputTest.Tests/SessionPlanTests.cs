namespace ThroughputTest.Tests;

public class SessionPlanTests
{
    private static Settings Sessions(int lengthJitterPercent = 0, int publishJitterSeconds = 0, int messages = 100_000, int sessions = 1000) =>
        Settings.Parse(new Dictionary<string, string>
        {
            ["QUEUE_ID"] = "tp-1", ["WORKLOAD"] = "sessions", ["MESSAGES"] = messages.ToString(), ["SESSIONS"] = sessions.ToString(),
            ["SESSION_LENGTH_JITTER_PERCENT"] = lengthJitterPercent.ToString(), ["PUBLISH_JITTER_SECONDS"] = publishJitterSeconds.ToString()
        });

    [Fact]
    public void WithoutJitter_EverySessionIsTheSameLength_AndPublishedAtTheStart()
    {
        var plan = SessionPlan.Build(Sessions());

        Assert.Equal(1000, plan.Count);
        Assert.All(plan, s => Assert.Equal(100, s.Messages));
        Assert.All(plan, s => Assert.Equal(0, s.PublishAtSeconds));
    }

    [Fact]
    public void LengthJitter_VariesTheSessions_WithinTheRange_AndKeepsTheTotal()
    {
        var plan = SessionPlan.Build(Sessions(lengthJitterPercent: 50));

        Assert.Equal(100_000, plan.Sum(s => s.Messages));
        // +-50% of 100, give or take one from rounding to the exact total.
        Assert.All(plan, s => Assert.InRange(s.Messages, 49, 151));
        Assert.True(plan.Select(s => s.Messages).Distinct().Count() > 50);
    }

    [Fact]
    public void PublishJitter_SpreadsTheSessionsOverTheWindow()
    {
        var plan = SessionPlan.Build(Sessions(publishJitterSeconds: 30));

        Assert.All(plan, s => Assert.InRange(s.PublishAtSeconds, 0, 30 - 1e-9));
        Assert.True(plan.Max(s => s.PublishAtSeconds) > 25);
        Assert.True(plan.Min(s => s.PublishAtSeconds) < 5);
    }

    [Fact]
    public void ThePlan_IsTheSameEveryRun()
    {
        // Runs with the same key must face the same sessions, or their ideal times and results differ.
        var first = SessionPlan.Build(Sessions(lengthJitterPercent: 50, publishJitterSeconds: 30));
        var second = SessionPlan.Build(Sessions(lengthJitterPercent: 50, publishJitterSeconds: 30));

        Assert.Equal(first, second);
    }

    [Fact]
    public void LengthAndPublishJitter_AreDrawnIndependently()
    {
        // Turning publish jitter on mustn't change which lengths the sessions get.
        var lengthsOnly = SessionPlan.Build(Sessions(lengthJitterPercent: 50));
        var both = SessionPlan.Build(Sessions(lengthJitterPercent: 50, publishJitterSeconds: 30));

        Assert.Equal(lengthsOnly.Select(s => s.Messages), both.Select(s => s.Messages));
    }
}
