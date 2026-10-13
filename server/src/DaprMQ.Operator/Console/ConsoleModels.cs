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
