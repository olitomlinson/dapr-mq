namespace ThroughputTest.Tests;

public class LatencyHistogramTests
{
    [Fact]
    public void Summary_GivesCountMeanAndMaxExactly_AndPercentilesWithinABucket()
    {
        var histogram = new LatencyHistogram();
        for (int ms = 1; ms <= 1000; ms++)
        {
            histogram.Record(ms);
        }

        var summary = histogram.Summary();

        Assert.Equal(1000, summary.Count);
        Assert.Equal(500.5, summary.Mean, 3);
        Assert.Equal(1000, summary.Max);
        // Buckets grow by 10%, so a percentile is within 10% of the true value.
        Assert.InRange(summary.P50, 500, 550);
        Assert.InRange(summary.P95, 950, 1045);
        Assert.InRange(summary.P99, 990, 1089);
    }

    [Fact]
    public void Merge_IsTheSameAsRecordingEverythingInOne()
    {
        // Every consumer pod keeps its own histogram; the run merges them.
        var a = new LatencyHistogram();
        var b = new LatencyHistogram();
        var all = new LatencyHistogram();
        for (int ms = 1; ms <= 2000; ms++)
        {
            (ms % 2 == 0 ? a : b).Record(ms);
            all.Record(ms);
        }

        var merged = LatencyHistogram.Merge([a.Snapshot(), b.Snapshot()]);

        Assert.Equal(all.Summary(), merged.Summary());
    }

    [Fact]
    public void AnEmptyHistogram_SummarisesToZeros()
    {
        Assert.Equal(new Distribution(0, 0, 0, 0, 0, 0), new LatencyHistogram().Summary());
    }
}
