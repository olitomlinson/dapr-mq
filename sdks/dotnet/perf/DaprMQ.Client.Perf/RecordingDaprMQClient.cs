using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DaprMQ.Client.Perf;

/// <summary>
/// Decorates the real client handed to SessionQueueConsumer, timestamping each ConsumeSession
/// stream (open, first delivery, end). With the handler intervals that's enough to account for
/// every slot-second without knowing which slot ran which stream - so no SDK changes are needed.
/// </summary>
public sealed class RecordingDaprMQClient(IDaprMQClient inner, Func<double> clockMs) : IDaprMQClient
{
    private readonly ConcurrentQueue<StreamRecord> _streams = new();

    public IReadOnlyList<StreamRecord> Streams => _streams.ToArray();

    public async IAsyncEnumerable<SessionDelivery> ConsumeSessionAsync(
        string queueId, string? sessionId, int leaseSeconds, int prefetchCount,
        [EnumeratorCancellation] CancellationToken ct = default, int sessionIdleTimeoutSeconds = 0)
    {
        var openMs = clockMs();
        double? firstDeliveryMs = null;
        string? deliveredSessionId = null;
        var endReason = StreamEndReasons.Abandoned;

        await using var enumerator = inner
            .ConsumeSessionAsync(queueId, sessionId, leaseSeconds, prefetchCount, ct, sessionIdleTimeoutSeconds)
            .GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    endReason = ex.GetType().Name;
                    throw;
                }

                if (!moved)
                {
                    endReason = StreamEndReasons.Completed;
                    break;
                }

                firstDeliveryMs ??= clockMs();
                deliveredSessionId ??= enumerator.Current.SessionId;
                yield return enumerator.Current;
            }
        }
        finally
        {
            _streams.Enqueue(new StreamRecord(openMs, firstDeliveryMs, clockMs(), deliveredSessionId, endReason));
        }
    }

    public Task<EnqueueResult> EnqueueAsync(string queueId, IEnumerable<EnqueueItemDto> items, CancellationToken ct = default) =>
        inner.EnqueueAsync(queueId, items, ct);

    public Task<DequeueLockedResult?> DequeueLockedAsync(string queueId, int count = 1, int ttlSeconds = 30, string? leaseId = null, CancellationToken ct = default) =>
        inner.DequeueLockedAsync(queueId, count, ttlSeconds, leaseId, ct);

    public Task AcknowledgeAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default) =>
        inner.AcknowledgeAsync(queueId, lockId, leaseId, ct);

    public Task ExtendLockAsync(string queueId, string lockId, int additionalTtlSeconds, string? leaseId = null, CancellationToken ct = default) =>
        inner.ExtendLockAsync(queueId, lockId, additionalTtlSeconds, leaseId, ct);

    public Task DeadLetterAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default) =>
        inner.DeadLetterAsync(queueId, lockId, leaseId, ct);

    public Task<NackResult> NackAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default) =>
        inner.NackAsync(queueId, lockId, leaseId, ct);

    public Task<SessionLease?> AcceptSessionAsync(string queueId, string? sessionId = null, int leaseSeconds = 30, CancellationToken ct = default) =>
        inner.AcceptSessionAsync(queueId, sessionId, leaseSeconds, ct);

    public Task<SessionLease> RenewSessionLeaseAsync(string queueId, string sessionId, string leaseId, int additionalSeconds = 30, CancellationToken ct = default) =>
        inner.RenewSessionLeaseAsync(queueId, sessionId, leaseId, additionalSeconds, ct);

    public Task ReleaseSessionAsync(string queueId, string sessionId, string leaseId, CancellationToken ct = default) =>
        inner.ReleaseSessionAsync(queueId, sessionId, leaseId, ct);

    public Task WaitForReadyAsync(CancellationToken ct = default) => inner.WaitForReadyAsync(ct);
}
