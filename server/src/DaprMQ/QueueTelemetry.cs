using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace DaprMQ;

/// <summary>How the queue label is applied to metrics (DAPRMQ_METRICS_QUEUE_LABEL).</summary>
public enum QueueLabelMode
{
    /// <summary>One series per queue.</summary>
    All,

    /// <summary>No queue label: aggregate series only.</summary>
    None,

    /// <summary>A queue label only for the listed queues; the rest are labelled "_other".</summary>
    Allowlist
}

/// <summary>
/// Turns actor ids into metric labels. A per-session actor is labelled with its parent queue plus
/// session=true, and a DLQ with its parent queue plus kind=deadletter, so session ids and DLQ ids
/// never become label values of their own.
/// </summary>
public sealed class MetricLabelPolicy
{
    public const string OtherQueue = "_other";

    private readonly QueueLabelMode _mode;
    private readonly HashSet<string> _allowlist;

    public MetricLabelPolicy(QueueLabelMode mode = QueueLabelMode.All, IEnumerable<string>? allowlist = null)
    {
        _mode = mode;
        _allowlist = new HashSet<string>(allowlist ?? [], StringComparer.Ordinal);
    }

    /// <summary>The queue label for an entity, or null when queue labels are off.</summary>
    public string? Label(string id) => _mode switch
    {
        QueueLabelMode.None => null,
        QueueLabelMode.Allowlist => _allowlist.Contains(id) ? id : OtherQueue,
        _ => id
    };

    /// <summary>Tags for a queue actor: queue (unless off), kind (queue|deadletter) and session (true|false).</summary>
    public KeyValuePair<string, object?>[] QueueTags(string actorId)
    {
        var id = actorId;
        var kind = "queue";
        if (id.EndsWith(QueueActor.DeadLetterActorIdSuffix, StringComparison.Ordinal))
        {
            id = id[..^QueueActor.DeadLetterActorIdSuffix.Length];
            kind = "deadletter";
        }

        var marker = id.IndexOf(QueueActor.SessionActorIdMarker, StringComparison.Ordinal);
        var session = marker > 0;
        if (session)
        {
            id = id[..marker];
        }

        var label = Label(id);
        return label == null
            ? [new("kind", kind), new("session", session ? "true" : "false")]
            : [new("queue", label), new("kind", kind), new("session", session ? "true" : "false")];
    }
}

public enum QueueCounter
{
    Enqueued,
    Dequeued,
    Acknowledged,
    Nacked,
    DeadLettered,
    LockExpired
}

/// <summary>
/// The "DaprMQ.Queue" meter. Counters are incremented by QueueActor after the save that commits the
/// work, so nothing is counted for a turn whose save fails. The depth gauge reads a worker-local map
/// each actor updates from its cached metadata after every successful method - no state I/O. An
/// entry outlives the actor's deactivation on purpose: an idle queue's depth can only change inside
/// the actor, so its last value stays true. History only; never suitable for scaling decisions,
/// since a missing entry would read as zero.
/// </summary>
public sealed class QueueTelemetry : IDisposable
{
    public const string MeterName = "DaprMQ.Queue";

    private readonly TimeProvider _timeProvider;
    private readonly Counter<long>[] _counters;
    private readonly ConcurrentDictionary<string, DepthEntry> _depths = new();

    private readonly record struct DepthEntry(KeyValuePair<string, object?>[] Tags, long Ready, long Locked, DateTimeOffset UpdatedAt);

    public QueueTelemetry(MetricLabelPolicy labels, TimeProvider timeProvider)
    {
        Labels = labels ?? throw new ArgumentNullException(nameof(labels));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Meter = new Meter(MeterName);
        _counters =
        [
            Meter.CreateCounter<long>("daprmq.queue.enqueued", description: "Items enqueued"),
            Meter.CreateCounter<long>("daprmq.queue.dequeued", description: "Items dequeued, locked or not"),
            Meter.CreateCounter<long>("daprmq.queue.acknowledged", description: "Locks acknowledged"),
            Meter.CreateCounter<long>("daprmq.queue.nacked", description: "Locks nacked"),
            Meter.CreateCounter<long>("daprmq.queue.deadlettered", description: "Items moved to a dead-letter queue"),
            Meter.CreateCounter<long>("daprmq.queue.lock_expired", description: "Locks that lapsed and were reclaimed"),
        ];
        Meter.CreateObservableGauge("daprmq.queue.depth", ObserveDepth, description: "Ready and locked items, as last seen by this worker");
        Meter.CreateObservableGauge("daprmq.queue.depth_updated", ObserveDepthUpdated, description: "Unix time this worker last saw each queue's depth");
    }

    public Meter Meter { get; }

    public MetricLabelPolicy Labels { get; }

    public void Add(QueueCounter counter, long count, KeyValuePair<string, object?>[] tags)
    {
        if (count > 0)
        {
            _counters[(int)counter].Add(count, tags.AsSpan());
        }
    }

    /// <summary>Records an actor's depth (session actors and their parent are summed at export).</summary>
    public void RecordDepth(string actorId, KeyValuePair<string, object?>[] tags, long ready, long locked) =>
        _depths[actorId] = new DepthEntry(tags, ready, locked, _timeProvider.GetUtcNow());

    private IEnumerable<Measurement<long>> ObserveDepth()
    {
        foreach (var group in GroupDepths())
        {
            yield return new Measurement<long>(group.Sum(e => e.Ready), [.. group.Key, new("state", "ready")]);
            yield return new Measurement<long>(group.Sum(e => e.Locked), [.. group.Key, new("state", "locked")]);
        }
    }

    private IEnumerable<Measurement<double>> ObserveDepthUpdated() =>
        GroupDepths().Select(group => new Measurement<double>(
            group.Max(e => e.UpdatedAt).ToUnixTimeMilliseconds() / 1000.0, group.Key));

    // Grouped by queue and kind; the session tag is dropped so sessions sum under their parent.
    private IEnumerable<IGrouping<KeyValuePair<string, object?>[], DepthEntry>> GroupDepths() =>
        _depths.Values.GroupBy(e => e.Tags.Where(t => t.Key != "session").ToArray(), TagsComparer.Instance);

    public void Dispose() => Meter.Dispose();

    private sealed class TagsComparer : IEqualityComparer<KeyValuePair<string, object?>[]>
    {
        public static readonly TagsComparer Instance = new();

        public bool Equals(KeyValuePair<string, object?>[]? x, KeyValuePair<string, object?>[]? y) =>
            x!.Length == y!.Length && x.Zip(y).All(p => p.First.Key == p.Second.Key && Equals(p.First.Value, p.Second.Value));

        public int GetHashCode(KeyValuePair<string, object?>[] tags) =>
            tags.Aggregate(0, (hash, t) => HashCode.Combine(hash, t.Key, t.Value));
    }
}

/// <summary>
/// The "DaprMQ.Topic" meter: items published, and items dispatched to each subscriber's queue.
/// Incremented after the commit, like <see cref="QueueTelemetry"/>.
/// </summary>
public sealed class TopicTelemetry : IDisposable
{
    public const string MeterName = "DaprMQ.Topic";

    private readonly MetricLabelPolicy _labels;
    private readonly Counter<long> _published;
    private readonly Counter<long> _dispatched;

    public TopicTelemetry(MetricLabelPolicy labels)
    {
        _labels = labels ?? throw new ArgumentNullException(nameof(labels));
        Meter = new Meter(MeterName);
        _published = Meter.CreateCounter<long>("daprmq.topic.published", description: "Items published");
        _dispatched = Meter.CreateCounter<long>("daprmq.topic.dispatched", description: "Items dispatched to a subscriber's queue");
    }

    public Meter Meter { get; }

    public void Published(string topicId, long count)
    {
        if (count > 0)
        {
            _published.Add(count, TopicTags(topicId));
        }
    }

    public void Dispatched(string topicId, string subscriberId, long count)
    {
        if (count > 0)
        {
            // The subscriber is only labelled alongside its topic; with topic labels off it would be
            // the only per-entity label left.
            var topic = _labels.Label(topicId);
            KeyValuePair<string, object?>[] tags = topic == null ? [] : [new("topic", topic), new("subscriber", topic == MetricLabelPolicy.OtherQueue ? MetricLabelPolicy.OtherQueue : subscriberId)];
            _dispatched.Add(count, tags.AsSpan());
        }
    }

    private KeyValuePair<string, object?>[] TopicTags(string topicId) =>
        _labels.Label(topicId) is { } topic ? [new("topic", topic)] : [];

    public void Dispose() => Meter.Dispose();
}
