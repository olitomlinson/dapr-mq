namespace DaprMQ.Client.Perf;

public static class StreamEndReasons
{
    /// <summary>The server ended the stream (idle-drain) and the consumer read it to the end.</summary>
    public const string Completed = "completed";

    /// <summary>The consumer stopped enumerating early (stop, handler failure under AbandonSession).</summary>
    public const string Abandoned = "abandoned";
}

/// <summary>
/// One ConsumeSession call as seen by a consumer slot. Times are ms from the start of the consume
/// phase. FirstDeliveryMs/SessionId are null when the claim itself failed (e.g. no sessions available).
/// </summary>
public sealed record StreamRecord(double OpenMs, double? FirstDeliveryMs, double EndMs, string? SessionId, string EndReason);

/// <summary>
/// One handler invocation (the simulated settle time), ms from the start of the consume phase.
/// DeliveryLatencyMs is enqueue -> handler start, from the publish timestamp in the payload.
/// </summary>
public sealed record HandlerRecord(string SessionId, int Seq, double StartMs, double EndMs, double? DeliveryLatencyMs = null);

public static class PublishModes
{
    /// <summary>Publish the whole backlog, then start the consumer.</summary>
    public const string Before = "before";

    /// <summary>Start the consumer and the publisher together; wall clock runs from publish start.</summary>
    public const string Concurrent = "concurrent";
}

public sealed record ScenarioParams(
    int Sessions,
    int MessagesPerSession,
    int SettleMs,
    int MaxConcurrentSessions,
    int PrefetchCount,
    int LeaseSeconds,
    int SessionIdleTimeoutSeconds,
    string PublishMode = PublishModes.Before,
    int PublishIntervalMs = 0,
    int PublishJitterMs = 0)
{
    private bool RateLimited => PublishIntervalMs > 0 || PublishJitterMs > 0;

    /// <summary>
    /// Groups comparable runs on the trend charts and for regression checks. Suffixes are only
    /// added for non-default settings, so the default scenario keeps its short key.
    /// </summary>
    public string Key => $"{Sessions}x{MessagesPerSession}@{SettleMs}ms/{MaxConcurrentSessions}slots"
        + (PublishMode == PublishModes.Before ? "" : $"+{PublishMode}")
        + (RateLimited ? $"+pub{PublishIntervalMs}ms~{PublishJitterMs}ms" : "")
        + (SessionIdleTimeoutSeconds != 0 ? $"+idle{SessionIdleTimeoutSeconds}s" : "")
        + (PrefetchCount != 10 ? $"+prefetch{PrefetchCount}" : "")
        + (LeaseSeconds != 30 ? $"+lease{LeaseSeconds}s" : "");

    /// <summary>
    /// Lower bound on the wall clock. Consumer-bound: ⌈sessions ÷ slots⌉ rounds of M x settle.
    /// When publishing runs on the clock (concurrent) and is rate limited, no session can finish
    /// before its last message is published (~M x mean delay) and settled.
    /// </summary>
    public double IdealSeconds
    {
        get
        {
            var consumeBound = Math.Ceiling(Sessions / (double)MaxConcurrentSessions) * MessagesPerSession * SettleMs / 1000.0;
            if (PublishMode != PublishModes.Concurrent || !RateLimited)
            {
                return consumeBound;
            }

            var publishBound = (MessagesPerSession * (PublishIntervalMs + PublishJitterMs / 2.0) + SettleMs) / 1000.0;
            return Math.Max(consumeBound, publishBound);
        }
    }
}

/// <summary>
/// Where every slot-second in a window went. CapacitySlotSeconds = slots x window, and it splits
/// exactly into handling + claim + in-session wait + drain wait + between streams.
/// </summary>
public sealed record SlotTimeBreakdown(
    double StartSeconds,
    double EndSeconds,
    double CapacitySlotSeconds,
    double HandlingSeconds,
    double ClaimSeconds,
    double InSessionWaitSeconds,
    double DrainWaitSeconds,
    double BetweenStreamsSeconds)
{
    public double IdleSlotSeconds => CapacitySlotSeconds - HandlingSeconds;
    public double Utilization => CapacitySlotSeconds > 0 ? HandlingSeconds / CapacitySlotSeconds : 0;
}

public sealed record Distribution(int Count, double Mean, double P50, double P95, double P99, double Max)
{
    public static Distribution Of(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new Distribution(0, 0, 0, 0, 0, 0);
        }

        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

        return new Distribution(sorted.Length, sorted.Average(), Percentile(0.50), Percentile(0.95), Percentile(0.99), sorted[^1]);
    }
}

public sealed record SessionDrainResult(
    double SeedSeconds,
    double WallClockSeconds,
    double IdealSeconds,
    double Efficiency,
    double TimeToFirstMessageSeconds,
    double TailSeconds,
    SlotTimeBreakdown Peak,
    SlotTimeBreakdown Overall,
    Distribution ClaimLatencyMs,
    Distribution DrainWaitMs,
    Distribution InterMessageGapMs,
    Distribution DeliveryLatencyMs,
    int Streams,
    int FailedClaims,
    int SessionsClaimedMoreThanOnce,
    int MessagesHandled,
    int Duplicates,
    int Missing,
    int FifoViolations,
    int TimelineBucketMs,
    double[] BusySlotsTimeline);

/// <summary>
/// Turns the raw stream/handler records of one run into the comparable numbers. Pure, so it is
/// unit tested on hand-built timelines; see SessionDrainMetricsTests.
/// </summary>
public static class SessionDrainMetrics
{
    private const int TimelineBucketMs = 1000;

    public static SessionDrainResult Compute(
        ScenarioParams scenario,
        IReadOnlyList<StreamRecord> streams,
        IReadOnlyList<HandlerRecord> handlers,
        double completedMs,
        double seedSeconds)
    {
        var idealSeconds = scenario.IdealSeconds;
        var wallSeconds = completedMs / 1000.0;

        var handlersBySession = handlers.GroupBy(h => h.SessionId).ToDictionary(g => g.Key, g => g.OrderBy(h => h.StartMs).ToList());
        var delivered = streams
            .Where(s => s.FirstDeliveryMs.HasValue && s.SessionId != null)
            .Select(s => (Stream: s, Handlers: HandlersOf(s, handlersBySession)))
            .ToList();

        var firstHandlerStart = handlers.Count > 0 ? handlers.Min(h => h.StartMs) : 0;

        // Peak = from the first delivery until the last session gets its first delivery. After
        // that there is no unclaimed work left, so slots going idle in the tail is unavoidable
        // rather than a cost of claiming/draining. Degenerates when every session starts at once.
        var lastSessionStart = handlersBySession.Count > 0 ? handlersBySession.Values.Max(hs => hs[0].StartMs) : 0;
        var peakEnd = lastSessionStart > firstHandlerStart ? lastSessionStart : completedMs;

        var interMessageGaps = delivered.SelectMany(d => d.Handlers.Zip(d.Handlers.Skip(1), (a, b) => b.StartMs - a.EndMs));

        return new SessionDrainResult(
            SeedSeconds: seedSeconds,
            WallClockSeconds: wallSeconds,
            IdealSeconds: idealSeconds,
            Efficiency: wallSeconds > 0 ? idealSeconds / wallSeconds : 0,
            TimeToFirstMessageSeconds: firstHandlerStart / 1000.0,
            TailSeconds: (completedMs - peakEnd) / 1000.0,
            Peak: Breakdown(scenario.MaxConcurrentSessions, streams, delivered, firstHandlerStart, peakEnd),
            Overall: Breakdown(scenario.MaxConcurrentSessions, streams, delivered, 0, completedMs),
            ClaimLatencyMs: Distribution.Of(delivered.Select(d => d.Stream.FirstDeliveryMs!.Value - d.Stream.OpenMs)),
            DrainWaitMs: Distribution.Of(delivered.Where(d => d.Handlers.Count > 0).Select(d => d.Stream.EndMs - d.Handlers[^1].EndMs)),
            InterMessageGapMs: Distribution.Of(interMessageGaps),
            DeliveryLatencyMs: Distribution.Of(handlers.Select(h => h.DeliveryLatencyMs).OfType<double>()),
            Streams: streams.Count,
            FailedClaims: streams.Count(s => !s.FirstDeliveryMs.HasValue),
            SessionsClaimedMoreThanOnce: delivered.GroupBy(d => d.Stream.SessionId).Count(g => g.Count() > 1),
            MessagesHandled: handlers.Count,
            Duplicates: handlers.Count - handlers.Select(h => (h.SessionId, h.Seq)).Distinct().Count(),
            Missing: Math.Max(0, scenario.Sessions * scenario.MessagesPerSession - handlers.Select(h => (h.SessionId, h.Seq)).Distinct().Count()),
            FifoViolations: handlersBySession.Values.Sum(hs => hs.Zip(hs.Skip(1)).Count(p => p.Second.Seq < p.First.Seq)),
            TimelineBucketMs: TimelineBucketMs,
            BusySlotsTimeline: Timeline(handlers, completedMs));
    }

    private static List<HandlerRecord> HandlersOf(StreamRecord stream, Dictionary<string, List<HandlerRecord>> handlersBySession) =>
        handlersBySession.TryGetValue(stream.SessionId!, out var hs)
            ? hs.Where(h => h.StartMs >= stream.FirstDeliveryMs!.Value && h.StartMs <= stream.EndMs).ToList()
            : [];

    private static SlotTimeBreakdown Breakdown(
        int slots,
        IReadOnlyList<StreamRecord> streams,
        List<(StreamRecord Stream, List<HandlerRecord> Handlers)> delivered,
        double from,
        double to)
    {
        double Clip(double a, double b) => Math.Max(0, Math.Min(b, to) - Math.Max(a, from));

        double handling = 0, claim = 0, inSession = 0, drain = 0;

        foreach (var s in streams.Where(s => !s.FirstDeliveryMs.HasValue))
        {
            claim += Clip(s.OpenMs, s.EndMs);
        }

        foreach (var (s, hs) in delivered)
        {
            var first = s.FirstDeliveryMs!.Value;
            var lastHandlerEnd = hs.Count > 0 ? Math.Min(hs[^1].EndMs, s.EndMs) : first;
            var streamHandling = hs.Sum(h => Clip(h.StartMs, h.EndMs));

            claim += Clip(s.OpenMs, first);
            handling += streamHandling;
            inSession += Math.Max(0, Clip(first, lastHandlerEnd) - streamHandling);
            drain += Clip(lastHandlerEnd, s.EndMs);
        }

        var capacity = slots * Math.Max(0, to - from);
        var inStreams = streams.Sum(s => Clip(s.OpenMs, s.EndMs));

        return new SlotTimeBreakdown(
            StartSeconds: from / 1000.0,
            EndSeconds: to / 1000.0,
            CapacitySlotSeconds: capacity / 1000.0,
            HandlingSeconds: handling / 1000.0,
            ClaimSeconds: claim / 1000.0,
            InSessionWaitSeconds: inSession / 1000.0,
            DrainWaitSeconds: drain / 1000.0,
            BetweenStreamsSeconds: Math.Max(0, capacity - inStreams) / 1000.0);
    }

    private static double[] Timeline(IReadOnlyList<HandlerRecord> handlers, double completedMs)
    {
        var buckets = new double[(int)Math.Ceiling(completedMs / TimelineBucketMs)];
        foreach (var h in handlers)
        {
            for (var b = (int)(h.StartMs / TimelineBucketMs); b < buckets.Length && b * TimelineBucketMs < h.EndMs; b++)
            {
                var bucketStart = b * TimelineBucketMs;
                buckets[b] += Math.Max(0, Math.Min(h.EndMs, bucketStart + TimelineBucketMs) - Math.Max(h.StartMs, bucketStart));
            }
        }

        return buckets.Select(ms => Math.Round(ms / TimelineBucketMs, 3)).ToArray();
    }
}
