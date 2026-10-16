using DaprMQ.Interfaces;

namespace DaprMQ.Operator.Console;

/// <summary>A row of the console's queue list: a catalog entry joined with its depth (and its DLQ's).</summary>
public record ConsoleQueueRow
{
    public required string Id { get; init; }
    public CatalogEntryKind Kind { get; init; }
    public string? ParentId { get; init; }
    public long Ready { get; init; }
    public long Locked { get; init; }

    /// <summary>Items in the queue's DLQ, ready or locked; null on a row that is itself a DLQ.</summary>
    public long? DeadLetterCount { get; init; }

    /// <summary>Set when the depth read failed; the counts are then meaningless.</summary>
    public string? Error { get; init; }
}

public record ConsoleQueuePage
{
    public List<ConsoleQueueRow> Items { get; init; } = [];
    public string? NextCursor { get; init; }
    public Dictionary<CatalogEntryKind, int> Totals { get; init; } = [];
}

/// <summary>A queue's depth on the console's overview; for a DLQ row, Id is the queue it belongs to.</summary>
public record ConsoleQueueDepth
{
    public required string Id { get; init; }
    public long Ready { get; init; }
    public long Locked { get; init; }
}

/// <summary>
/// The console's overview, read from committed state: every catalogued queue's depth and its DLQ's,
/// summed and ranked. Session items aren't included (a session-only queue reads as empty), the
/// same as the queue list.
/// </summary>
public record ConsoleOverview
{
    public Dictionary<CatalogEntryKind, int> Totals { get; init; } = [];
    public long Ready { get; init; }
    public long Locked { get; init; }

    /// <summary>Items in every queue's DLQ, ready or locked.</summary>
    public long DeadLettered { get; init; }
    public int DeadLetterQueuesWithMessages { get; init; }
    public List<ConsoleQueueDepth> TopQueues { get; init; } = [];
    public List<ConsoleQueueDepth> DeadLetterQueues { get; init; } = [];

    /// <summary>Queues whose depth was read; with Truncated set, the catalog holds more.</summary>
    public int QueuesRead { get; init; }
    public bool Truncated { get; init; }

    /// <summary>Depth reads that failed; those queues count as empty.</summary>
    public int Errors { get; init; }
}

/// <summary>
/// GET /api/console/scaling: the ScaledObjects scaling on DaprMQ queues, with recent history from
/// the console's sampler (in memory, per pod; empty again after a restart).
/// </summary>
public record ConsoleScaling
{
    /// <summary>Whether the sampler reads ScaledObjects and their HPAs (Helm console.kubernetesRead).</summary>
    public bool KubernetesRead { get; init; }
    public int SampleIntervalSeconds { get; init; }
    public int HistorySeconds { get; init; }
    public List<ConsoleScalingTarget> Targets { get; init; } = [];
}

/// <summary>One KEDA trigger on a DaprMQ queue, as the scaler last saw it.</summary>
public record ConsoleScalingTarget
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required string QueueId { get; init; }
    public QueueDepthMode Mode { get; init; }
    public double TargetValue { get; init; }
    public double ActivationValue { get; init; }
    public bool IncludeDeadLetter { get; init; }

    /// <summary>Unix seconds of KEDA's last call about it.</summary>
    public long LastSeenAt { get; init; }

    /// <summary>The latest Kubernetes read; null without Kubernetes read access.</summary>
    public KubernetesScalingStatus? Kubernetes { get; init; }

    /// <summary>Oldest first.</summary>
    public List<ScalingSample> Samples { get; init; } = [];
}

/// <summary>
/// One sample. Metric is what the scaler would report (null if the depth read failed);
/// DesiredByMetric is the replica count it asks for, ceil(metric / target), or 0 at or below the
/// activation value - before min/max, HPA tolerance and stabilization. The rest need Kubernetes read.
/// </summary>
public record ScalingSample
{
    public long T { get; init; }
    public double? Metric { get; init; }
    public int? DesiredByMetric { get; init; }
    public int? CurrentReplicas { get; init; }
    public int? DesiredReplicas { get; init; }

    /// <summary>ScaledJob only: Jobs still running.</summary>
    public int? ActiveJobs { get; init; }
}

/// <summary>A ScaledObject's (or ScaledJob's) spec and status, and its HPA's replica counts.</summary>
public record KubernetesScalingStatus
{
    /// <summary>ScaledObject or ScaledJob.</summary>
    public string? Kind { get; init; }
    public int? MinReplicas { get; init; }
    public int? MaxReplicas { get; init; }
    public int? CurrentReplicas { get; init; }
    public int? DesiredReplicas { get; init; }
    public int? ActiveJobs { get; init; }
    public List<ScalingCondition> Conditions { get; init; } = [];

    /// <summary>Set when the read failed (e.g. no RBAC access, or no such object).</summary>
    public string? Error { get; init; }
}

public record ScalingCondition
{
    public required string Type { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
}
