using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class QueueDrainMetricsTests
{
    private static QueueDrainParams Params(int messages = 4, int settleMs = 1000, int active = 100, int handlers = 2, bool strict = false) =>
        new(messages, settleMs, active, handlers, strict);

    [Fact]
    public void Throughput_WallClock_AndFirstMessage_ComeFromTheHandlerRecords()
    {
        QueueHandlerRecord[] handled =
        [
            new(0, StartMs: 100, EndMs: 1100, DeliveryLatencyMs: 50),
            new(1, StartMs: 120, EndMs: 1120, DeliveryLatencyMs: 70),
            new(2, StartMs: 1100, EndMs: 2100, DeliveryLatencyMs: 1050),
            new(3, StartMs: 1120, EndMs: 2000, DeliveryLatencyMs: 1070),
        ];

        var r = QueueDrainMetrics.Compute(Params(), handled, completedMs: 2100, seedSeconds: 0.5);

        Assert.Equal(2.1, r.WallClockSeconds, 3);
        Assert.Equal(4 / 2.1, r.MessagesPerSecond, 3);
        Assert.Equal(0.1, r.TimeToFirstMessageSeconds, 3);
        Assert.Equal(0.5, r.SeedSeconds);
        Assert.Equal(4, r.DeliveryLatencyMs.Count);
        Assert.Equal(1070, r.DeliveryLatencyMs.Max);
        Assert.Equal(4, r.MessagesHandled);
        Assert.Equal(0, r.Missing);
        Assert.Equal(0, r.Duplicates);
    }

    [Fact]
    public void Ideal_IsTheHandlerWorkSpreadOverTheConcurrency_AndEfficiencyIsIdealOverWallClock()
    {
        // 4 messages x 1 s over 2 handlers: 2 s at best.
        QueueHandlerRecord[] handled = [new(0, 0, 1000, 0), new(1, 0, 1000, 0), new(2, 1000, 2000, 0), new(3, 1000, 2500, 0)];

        var r = QueueDrainMetrics.Compute(Params(), handled, completedMs: 2500, seedSeconds: 0);

        Assert.Equal(2, r.IdealSeconds!.Value, 3);
        Assert.Equal(0.8, r.Efficiency!.Value, 3);
    }

    [Fact]
    public void Ideal_IsUnsetForAnInstantHandler()
    {
        var r = QueueDrainMetrics.Compute(Params(settleMs: 0), [new(0, 0, 1, 0)], completedMs: 1, seedSeconds: 0);

        Assert.Null(r.IdealSeconds);
        Assert.Null(r.Efficiency);
    }

    [Theory]
    [InlineData(0, 100, false, 100)] // unlimited handlers: the window bounds them
    [InlineData(10, 100, false, 10)]
    [InlineData(500, 100, false, 100)]
    [InlineData(10, 100, true, 1)] // strict order: one at a time
    public void Concurrency_IsWhatBoundsHandlersRunningAtOnce(int handlers, int active, bool strict, int expected) =>
        Assert.Equal(expected, Params(handlers: handlers, active: active, strict: strict).Concurrency);

    [Fact]
    public void Ideal_CoversASlowTail_AndARateLimitedPublisher()
    {
        // 20 messages of 100 ms, every 10th taking 5 s instead, over 100 handlers: no faster than the 5 s tail.
        Assert.Equal(5, new QueueDrainParams(20, 100, 100, 0, TailEvery: 10, TailMs: 5000).IdealSeconds!.Value, 3);

        // 50 messages published one every 200 ms: the last can't be handled before ~9.8 s + settle.
        Assert.Equal(9.9, new QueueDrainParams(50, 100, 100, 0, PublishIntervalMs: 200).IdealSeconds!.Value, 3);
    }

    [Fact]
    public void PeakConcurrentHandlers_IsTheMostHandlersRunningAtOnce()
    {
        QueueHandlerRecord[] handled = [new(0, 0, 1000, 0), new(1, 100, 900, 0), new(2, 200, 300, 0), new(3, 1000, 1500, 0)];

        var r = QueueDrainMetrics.Compute(Params(), handled, completedMs: 1500, seedSeconds: 0);

        Assert.Equal(3, r.PeakConcurrentHandlers);
    }

    [Fact]
    public void MissingAndDuplicateMessages_AreCounted()
    {
        QueueHandlerRecord[] handled = [new(0, 0, 10, 0), new(0, 20, 30, 0), new(2, 40, 50, 0)];

        var r = QueueDrainMetrics.Compute(Params(messages: 4), handled, completedMs: 50, seedSeconds: 0);

        Assert.Equal(2, r.MessagesHandled);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal(2, r.Missing);
    }

    [Fact]
    public void OrderViolations_CountMessagesStartedBeforeAnEarlierOne()
    {
        QueueHandlerRecord[] handled = [new(0, 0, 10, 0), new(2, 10, 20, 0), new(1, 20, 30, 0), new(3, 30, 40, 0)];

        var r = QueueDrainMetrics.Compute(Params(strict: true), handled, completedMs: 40, seedSeconds: 0);

        Assert.Equal(1, r.OrderViolations);
    }

    [Fact]
    public void Timeline_CountsMessagesFinishedAndAverageBusyHandlersPerSecond()
    {
        QueueHandlerRecord[] handled = [new(0, 0, 1000, 0), new(1, 0, 500, 0), new(2, 1000, 1500, 0)];

        var r = QueueDrainMetrics.Compute(Params(messages: 3), handled, completedMs: 1500, seedSeconds: 0);

        // Second 0: one ends at 500; the one ending at 1000 belongs to second 1.
        Assert.Equal([1, 2], r.MessagesPerSecondTimeline);
        Assert.Equal([1.5, 0.5], r.BusyHandlersTimeline);
    }
}
