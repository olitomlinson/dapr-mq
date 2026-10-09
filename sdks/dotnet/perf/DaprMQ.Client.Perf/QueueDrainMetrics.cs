namespace DaprMQ.Client.Perf;

/// <summary>
/// One P-05 queue-drain profile (sdks/testing/PERFORMANCE_TESTS.md): publish Messages to a plain
/// queue and drain them with one QueueConsumer whose handler takes SettleMs per message (TailMs for
/// every TailEvery-th one). PublishIntervalMs above 0 publishes one message at a time alongside the
/// consumer instead of seeding the backlog first.
/// </summary>
public sealed record QueueDrainParams(
    int Messages,
    int SettleMs,
    int MaxActiveMessages,
    int MaxConcurrentHandlers,
    bool StrictOrder = false,
    int PublishIntervalMs = 0,
    int PublishJitterMs = 0,
    int TailEvery = 0,
    int TailMs = 0)
{
    public bool LivePublish => PublishIntervalMs > 0 || PublishJitterMs > 0;

    /// <summary>Canonical form of the parameters; groups comparable runs across SDKs.</summary>
    public string Key => $"queue:{Messages}@{SettleMs}ms/active{MaxActiveMessages}"
        + (MaxConcurrentHandlers > 0 ? $"/handlers{MaxConcurrentHandlers}" : "")
        + (StrictOrder ? "+strict" : "")
        + (LivePublish ? $"+pub{PublishIntervalMs}ms~{PublishJitterMs}ms" : "")
        + (TailEvery > 0 ? $"+tail{TailMs}ms/{TailEvery}" : "");

    /// <summary>Handlers that can run at once: strict order runs one, otherwise the window bounds them.</summary>
    public int Concurrency => StrictOrder ? 1
        : MaxConcurrentHandlers > 0 ? Math.Min(MaxConcurrentHandlers, MaxActiveMessages)
        : MaxActiveMessages;

    public int TailMessages => TailEvery > 0 ? Messages / TailEvery : 0;

    /// <summary>The handler time of message <paramref name="seq"/> (0-based).</summary>
    public int HandlerMs(int seq) => TailEvery > 0 && (seq + 1) % TailEvery == 0 ? TailMs : SettleMs;

    /// <summary>
    /// Lower bound on the wall clock: the handler work spread over <see cref="Concurrency"/>, but no
    /// less than the slowest single handler, nor than the last live-published message plus its
    /// handler. Null for an instant handler, where the run measures overhead and has no ideal.
    /// </summary>
    public double? IdealSeconds
    {
        get
        {
            if (SettleMs == 0 && TailMs == 0 && !LivePublish)
            {
                return null;
            }

            double workMs = (double)(Messages - TailMessages) * SettleMs + (double)TailMessages * TailMs;
            var slowestMs = TailMessages > 0 ? Math.Max(SettleMs, TailMs) : SettleMs;
            var lastPublishedMs = LivePublish ? (Messages - 1) * (double)PublishIntervalMs + HandlerMs(Messages - 1) : 0;
            return Math.Max(Math.Max(workMs / Concurrency, slowestMs), lastPublishedMs) / 1000;
        }
    }
}

/// <summary>
/// One handler invocation, ms from the start of the consume phase. DeliveryLatencyMs is publish ->
/// handler start, from the publish timestamp in the payload.
/// </summary>
public sealed record QueueHandlerRecord(int Seq, double StartMs, double EndMs, double DeliveryLatencyMs);

public sealed record QueueDrainResult(
    double SeedSeconds,
    double WallClockSeconds,
    double MessagesPerSecond,
    double? IdealSeconds,
    double? Efficiency,
    double TimeToFirstMessageSeconds,
    int PeakConcurrentHandlers,
    Distribution DeliveryLatencyMs,
    int MessagesHandled,
    int Duplicates,
    int Missing,
    int OrderViolations,
    int[] MessagesPerSecondTimeline,
    double[] BusyHandlersTimeline);

/// <summary>Turns a queue drain's handler records into its P-05 metrics. Pure, so it is unit tested; see QueueDrainMetricsTests.</summary>
public static class QueueDrainMetrics
{
    public const int BucketMs = 1000;

    public static QueueDrainResult Compute(QueueDrainParams p, IReadOnlyList<QueueHandlerRecord> handled, double completedMs, double seedSeconds)
    {
        var wallSeconds = completedMs / 1000;
        var unique = handled.Select(h => h.Seq).Distinct().Count();
        var ideal = p.IdealSeconds;

        // Handler starts in time order; a message started after a later one is out of queue order.
        var starts = handled.OrderBy(h => h.StartMs).Select(h => h.Seq).ToList();
        var orderViolations = starts.Zip(starts.Skip(1)).Count(pair => pair.Second < pair.First);

        var buckets = Math.Max(1, (int)Math.Ceiling(completedMs / BucketMs));
        var finished = new int[buckets];
        var busy = new double[buckets];
        foreach (var h in handled)
        {
            finished[Math.Min((int)(h.EndMs / BucketMs), buckets - 1)]++;
            for (var b = (int)(h.StartMs / BucketMs); b < buckets && b * BucketMs < h.EndMs; b++)
            {
                busy[b] += (Math.Min(h.EndMs, (b + 1) * BucketMs) - Math.Max(h.StartMs, b * BucketMs)) / BucketMs;
            }
        }

        return new QueueDrainResult(
            SeedSeconds: seedSeconds,
            WallClockSeconds: wallSeconds,
            MessagesPerSecond: wallSeconds > 0 ? unique / wallSeconds : 0,
            IdealSeconds: ideal,
            Efficiency: ideal is { } i && wallSeconds > 0 ? i / wallSeconds : null,
            TimeToFirstMessageSeconds: handled.Count > 0 ? handled.Min(h => h.StartMs) / 1000 : 0,
            PeakConcurrentHandlers: PeakOverlap(handled),
            DeliveryLatencyMs: Distribution.Of(handled.Select(h => h.DeliveryLatencyMs)),
            MessagesHandled: unique,
            Duplicates: handled.Count - unique,
            Missing: p.Messages - unique,
            OrderViolations: orderViolations,
            MessagesPerSecondTimeline: finished,
            BusyHandlersTimeline: busy.Select(b => Math.Round(b, 3)).ToArray());
    }

    private static int PeakOverlap(IReadOnlyList<QueueHandlerRecord> handled)
    {
        // Ends sort before starts at the same instant: back-to-back handlers don't overlap.
        var events = handled.SelectMany(h => new[] { (At: h.StartMs, Delta: 1), (At: h.EndMs, Delta: -1) })
            .OrderBy(e => e.At).ThenBy(e => e.Delta);
        int running = 0, peak = 0;
        foreach (var (_, delta) in events)
        {
            running += delta;
            peak = Math.Max(peak, running);
        }

        return peak;
    }
}
