namespace DaprMQ.Client.Perf;

/// <summary>One operation of a closed-loop load step. Times are ms from the start of the step (warmup included).</summary>
public sealed record OpRecord(double EndMs, double LatencyMs, int Messages, bool Error);

public sealed record LoadStep(
    int Concurrency,
    int Queues,
    double DurationSeconds,
    int Ops,
    int Messages,
    int Errors,
    double OpsPerSecond,
    double MessagesPerSecond,
    Distribution LatencyMs);

/// <summary>Per-second series over a step's recorded window (t = 0 at the end of warmup).</summary>
public sealed record StepTimeline(
    int[] OpsPerSecond,
    int[] MessagesPerSecond,
    double?[] LatencyP50Ms,
    double?[] LatencyP95Ms,
    double?[] LatencyP99Ms,
    int[] Errors,
    int[] Concurrency);

/// <summary>
/// Turns one load step's raw operations into the numbers in sdks/testing/PERFORMANCE_TESTS.md:
/// only operations finishing inside [warmup, warmup + duration) count, errors are counted but
/// left out of latency. Pure, so it is unit tested; see LoadMetricsTests.
/// </summary>
public static class LoadMetrics
{
    public const int BucketMs = 1000;

    public static (LoadStep Step, StepTimeline Timeline) ComputeStep(int concurrency, int queues, IReadOnlyList<OpRecord> ops, double warmupMs, double durationMs)
    {
        var window = ops.Where(o => o.EndMs >= warmupMs && o.EndMs < warmupMs + durationMs).ToList();
        var seconds = durationMs / 1000.0;
        var messages = window.Sum(o => o.Messages);

        var step = new LoadStep(
            Concurrency: concurrency,
            Queues: queues,
            DurationSeconds: seconds,
            Ops: window.Count,
            Messages: messages,
            Errors: window.Count(o => o.Error),
            OpsPerSecond: window.Count / seconds,
            MessagesPerSecond: messages / seconds,
            LatencyMs: Distribution.Of(window.Where(o => !o.Error).Select(o => o.LatencyMs)));

        var buckets = new List<OpRecord>[(int)Math.Ceiling(durationMs / BucketMs)];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = [];
        }

        foreach (var op in window)
        {
            buckets[(int)((op.EndMs - warmupMs) / BucketMs)].Add(op);
        }

        double? Percentile(List<OpRecord> bucket, Func<Distribution, double> pick)
        {
            var ok = bucket.Where(o => !o.Error).Select(o => o.LatencyMs).ToList();
            return ok.Count == 0 ? null : Math.Round(pick(Distribution.Of(ok)), 3);
        }

        var timeline = new StepTimeline(
            OpsPerSecond: buckets.Select(b => b.Count).ToArray(),
            MessagesPerSecond: buckets.Select(b => b.Sum(o => o.Messages)).ToArray(),
            LatencyP50Ms: buckets.Select(b => Percentile(b, d => d.P50)).ToArray(),
            LatencyP95Ms: buckets.Select(b => Percentile(b, d => d.P95)).ToArray(),
            LatencyP99Ms: buckets.Select(b => Percentile(b, d => d.P99)).ToArray(),
            Errors: buckets.Select(b => b.Count(o => o.Error)).ToArray(),
            Concurrency: buckets.Select(_ => concurrency).ToArray());

        return (step, timeline);
    }
}
