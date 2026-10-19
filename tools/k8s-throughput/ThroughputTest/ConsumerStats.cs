namespace ThroughputTest;

/// <summary>
/// One consumer pod's stats so far, sent every few seconds to the run's stats queue. Cumulative, so the
/// run keeps only each pod's newest report and a lost or repeated report changes nothing.
/// </summary>
public sealed record ConsumerStatsReport(
    string Pod, long Seq, long SentAtMs, long Handled, int Busy, long? FirstHandledMs, HistogramSnapshot Latency);

/// <summary>Per-bucket series, as result.schema.json's timeline wants them for P-05.</summary>
public sealed record StatsTimeline(int BucketMs, double[] MessagesPerSecond, double[] BusyHandlers);

/// <summary>Combines the consumer pods' reports into the run's measurements. Pure, so it is unit tested.</summary>
public static class ConsumerStats
{
    public static IReadOnlyList<ConsumerStatsReport> Latest(IEnumerable<ConsumerStatsReport> reports) =>
        reports.GroupBy(r => r.Pod).Select(g => g.MaxBy(r => r.Seq)!).ToList();

    public static long? FirstHandledMs(IEnumerable<ConsumerStatsReport> reports) =>
        reports.Select(r => r.FirstHandledMs).Where(t => t != null).Min();

    /// <summary>
    /// Buckets of <paramref name="bucketMs"/> from <paramref name="startMs"/> to <paramref name="endMs"/>.
    /// A bucket's throughput is how many more messages every pod had handled by its end; its busy
    /// handlers are the sum of each pod's last report within it.
    /// </summary>
    public static StatsTimeline Timeline(IReadOnlyList<ConsumerStatsReport> reports, long startMs, int bucketMs, long endMs)
    {
        var buckets = (int)Math.Max(1, Math.Ceiling((endMs - startMs) / (double)bucketMs));
        var byPod = reports.GroupBy(r => r.Pod).Select(g => g.OrderBy(r => r.SentAtMs).ToList()).ToList();
        var messagesPerSecond = new double[buckets];
        var busyHandlers = new double[buckets];
        long handledBefore = 0;

        for (int b = 0; b < buckets; b++)
        {
            var from = startMs + (long)b * bucketMs;
            var to = from + bucketMs;
            long handled = 0;
            double busy = 0;
            foreach (var pod in byPod)
            {
                handled += pod.LastOrDefault(r => r.SentAtMs <= to)?.Handled ?? 0;
                busy += pod.LastOrDefault(r => r.SentAtMs > from && r.SentAtMs <= to)?.Busy ?? 0;
            }
            messagesPerSecond[b] = (handled - handledBefore) / (bucketMs / 1000.0);
            busyHandlers[b] = busy;
            handledBefore = handled;
        }

        return new StatsTimeline(bucketMs, messagesPerSecond, busyHandlers);
    }
}
