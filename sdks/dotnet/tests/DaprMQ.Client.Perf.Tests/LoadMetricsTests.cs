using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class LoadMetricsTests
{
    [Fact]
    public void OnlyOperationsFinishingInsideTheRecordedWindowCount()
    {
        OpRecord[] ops =
        [
            new(EndMs: 500, LatencyMs: 99, Messages: 1, Error: false),   // warmup
            new(EndMs: 1000, LatencyMs: 10, Messages: 1, Error: false),  // window starts at 1000
            new(EndMs: 1500, LatencyMs: 20, Messages: 1, Error: false),
            new(EndMs: 2999, LatencyMs: 30, Messages: 1, Error: false),
            new(EndMs: 3000, LatencyMs: 99, Messages: 1, Error: false),  // window ends at 3000 (exclusive)
        ];

        var (step, _) = LoadMetrics.ComputeStep(concurrency: 2, queues: 2, ops, warmupMs: 1000, durationMs: 2000);

        Assert.Equal(3, step.Ops);
        Assert.Equal(1.5, step.OpsPerSecond, 3);
        Assert.Equal(20, step.LatencyMs.P50);
        Assert.Equal(30, step.LatencyMs.Max);
        Assert.Equal(2, step.DurationSeconds);
    }

    [Fact]
    public void MessagesCountBatchItems_AndErrorsAreCountedButLeftOutOfLatency()
    {
        OpRecord[] ops =
        [
            new(100, 10, Messages: 100, Error: false),
            new(200, 5000, Messages: 0, Error: true),
            new(300, 20, Messages: 100, Error: false),
        ];

        var (step, _) = LoadMetrics.ComputeStep(1, 1, ops, warmupMs: 0, durationMs: 1000);

        Assert.Equal(3, step.Ops);
        Assert.Equal(200, step.Messages);
        Assert.Equal(200, step.MessagesPerSecond, 3);
        Assert.Equal(1, step.Errors);
        Assert.Equal(2, step.LatencyMs.Count);
        Assert.Equal(20, step.LatencyMs.Max);
    }

    [Fact]
    public void TimelineBucketsBySecondOfCompletion_WithNullPercentilesForEmptySeconds()
    {
        OpRecord[] ops =
        [
            new(1100, 10, 1, false),
            new(1900, 30, 1, false),
            new(3500, 40, 2, false),
            new(3600, 50, 0, true),
        ];

        var (_, timeline) = LoadMetrics.ComputeStep(4, 4, ops, warmupMs: 1000, durationMs: 3000);

        Assert.Equal([2, 0, 2], timeline.OpsPerSecond);
        Assert.Equal([2, 0, 2], timeline.MessagesPerSecond);
        Assert.Equal([30, null, 40], timeline.LatencyP95Ms);
        Assert.Equal([0, 0, 1], timeline.Errors);
        Assert.Equal([4, 4, 4], timeline.Concurrency);
    }

    [Fact]
    public void PercentilesUseNearestRank()
    {
        var d = Distribution.Of(Enumerable.Range(1, 100).Select(i => (double)i));

        Assert.Equal(50, d.P50);
        Assert.Equal(95, d.P95);
        Assert.Equal(99, d.P99);
        Assert.Equal(100, d.Max);
    }
}
