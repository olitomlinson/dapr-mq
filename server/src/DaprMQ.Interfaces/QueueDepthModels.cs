using System.Text.Json.Serialization;

namespace DaprMQ.Interfaces;

/// <summary>
/// What a queue-depth query measures. Messages: items in the queue itself. Sessions: how many of the
/// queue's sessions ({queueId}-session-{sessionId}) hold any items, read from its SessionCoordinatorActor
/// directory.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueueDepthMode>))]
public enum QueueDepthMode
{
    Messages,
    Sessions
}

/// <summary>
/// One queue to measure in a worker's internal queue-depth read (POST /internal/queue-depth), the
/// call DaprMQ.Operator's KEDA scaler makes via Dapr service invocation.
/// </summary>
public record QueueDepthQuery
{
    public string QueueId { get; init; } = string.Empty;
    public QueueDepthMode Mode { get; init; } = QueueDepthMode.Messages;

    /// <summary>Messages mode only: also count {queueId}-deadletter.</summary>
    public bool IncludeDeadLetter { get; init; }
}

/// <summary>
/// Depth of one queue. Ready/Locked are summed across sessions in Sessions mode. Error is set (and
/// the counts are meaningless) when the read failed - callers must not treat that as zero.
/// </summary>
public record QueueDepthResult
{
    public string QueueId { get; init; } = string.Empty;
    public long Ready { get; init; }
    public long Locked { get; init; }
    public int NonEmptySessions { get; init; }
    public string? Error { get; init; }
}

public record QueueDepthRequest
{
    public List<QueueDepthQuery> Queries { get; init; } = new();
}

public record QueueDepthResponse
{
    public List<QueueDepthResult> Results { get; init; } = new();
}
