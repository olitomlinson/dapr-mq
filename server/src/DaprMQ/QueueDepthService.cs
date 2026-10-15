using Dapr.Actors;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// Measures queue depth from persisted actor state without activating any actor. Backs the worker's
/// internal queue-depth read, which DaprMQ.Operator's KEDA scaler calls - it has to run on a worker
/// because only the worker app-id can read QueueActor state (see IQueueActorStateReader).
/// </summary>
public interface IQueueDepthService
{
    /// <summary>Throws on a failed read or an invalid query; never reports a failure as zero.</summary>
    Task<QueueDepthResult> GetDepthAsync(QueueDepthQuery query, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IQueueDepthService" />
public class QueueDepthService : IQueueDepthService
{
    private const string DeadLetterActorIdSuffix = "-deadletter";

    private readonly IQueueActorStateReader _reader;

    public QueueDepthService(IQueueActorStateReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <inheritdoc />
    public Task<QueueDepthResult> GetDepthAsync(QueueDepthQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Mode == QueueDepthMode.Sessions && query.IncludeDeadLetter)
        {
            // Each session has its own DLQ ({queueId}-session-{id}-deadletter); counting them would
            // double the fan-out for a number nobody scales consumers on.
            throw new ArgumentException("IncludeDeadLetter is only supported in Messages mode", nameof(query));
        }

        return query.Mode == QueueDepthMode.Sessions
            ? GetSessionsDepthAsync(query.QueueId, cancellationToken)
            : GetMessagesDepthAsync(query, cancellationToken);
    }

    private async Task<QueueDepthResult> GetMessagesDepthAsync(QueueDepthQuery query, CancellationToken cancellationToken)
    {
        var depth = await _reader.ReadQueueDepthAsync(new ActorId(query.QueueId), cancellationToken);
        if (query.IncludeDeadLetter)
        {
            var dlq = await _reader.ReadQueueDepthAsync(new ActorId(query.QueueId + DeadLetterActorIdSuffix), cancellationToken);
            depth = new QueueDepth(depth.Ready + dlq.Ready, depth.Locked + dlq.Locked);
        }

        return new QueueDepthResult { QueueId = query.QueueId, Ready = depth.Ready, Locked = depth.Locked };
    }

    /// <summary>
    /// One small read: the SessionCoordinatorActor keeps the count of sessions with work as workers
    /// report changes, so no session is read here. Ready and Locked aren't filled in for this mode.
    /// </summary>
    private async Task<QueueDepthResult> GetSessionsDepthAsync(string queueId, CancellationToken cancellationToken)
    {
        var summary = await _reader.ReadDirectorySummaryAsync(queueId, cancellationToken);
        return new QueueDepthResult { QueueId = queueId, NonEmptySessions = summary.ActiveCount };
    }
}
