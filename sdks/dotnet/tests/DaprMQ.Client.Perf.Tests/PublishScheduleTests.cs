using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class PublishScheduleTests
{
    [Fact]
    public void WithoutJitter_FirstMessageGoesImmediately_ThenOneEveryInterval()
    {
        var delays = PublishSchedule.Delays(messages: 4, intervalMs: 200, jitterMs: 0, new Random(1));

        Assert.Equal([0, 200, 200, 200], delays);
    }

    [Fact]
    public void Jitter_AddsUpToJitterMsToEveryDelay_IncludingTheStartOffset()
    {
        var delays = PublishSchedule.Delays(messages: 500, intervalMs: 100, jitterMs: 40, new Random(7));

        Assert.InRange(delays[0], 0, 40);
        Assert.All(delays.Skip(1), d => Assert.InRange(d, 100, 140));
        Assert.True(delays.Distinct().Count() > 10, "jitter should vary the delays");
    }
}
