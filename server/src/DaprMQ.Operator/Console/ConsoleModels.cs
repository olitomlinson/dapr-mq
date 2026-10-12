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
