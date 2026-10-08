namespace DaprMQ.Client;

public interface IDaprMQClient
{
    Task<EnqueueResult> EnqueueAsync(string queueId, IEnumerable<EnqueueItemDto> items, CancellationToken ct = default);

    Task<DequeueLockedResult?> DequeueLockedAsync(string queueId, int count = 1, int ttlSeconds = 30, string? leaseId = null, bool allowCompetingConsumers = false, CancellationToken ct = default);

    Task AcknowledgeAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default);

    /// <summary>
    /// Acknowledges up to 1,000 locks in one call, with an outcome per lock: one lock that expired or
    /// was already settled does not fail the rest. Throws only for whole-call failures (bad lease,
    /// invalid request). An unknown outcome is retried automatically, since re-sending is harmless;
    /// after such a retry, <see cref="AcknowledgeOutcomes.LockNotFound"/> can mean "already settled".
    /// </summary>
    Task<AcknowledgeBatchResult> AcknowledgeBatchAsync(string queueId, IReadOnlyList<string> lockIds, string? leaseId = null, CancellationToken ct = default);

    Task ExtendLockAsync(string queueId, string lockId, int additionalTtlSeconds, string? leaseId = null, CancellationToken ct = default);

    Task DeadLetterAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns a locked item to its original position in the queue. Counts as a delivery attempt:
    /// past the server's max delivery count the item is dead-lettered instead
    /// (<see cref="NackResult.DeadLettered"/>).
    /// </summary>
    Task<NackResult> NackAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default);

    Task<SessionLease?> AcceptSessionAsync(string queueId, string? sessionId = null, int leaseSeconds = 30, CancellationToken ct = default);

    Task<SessionLease> RenewSessionLeaseAsync(string queueId, string sessionId, string leaseId, int additionalSeconds = 30, CancellationToken ct = default);

    Task ReleaseSessionAsync(string queueId, string sessionId, string leaseId, CancellationToken ct = default);

    IAsyncEnumerable<SessionDelivery> ConsumeSessionAsync(
        string queueId, string? sessionId, int leaseSeconds, int prefetchCount, CancellationToken ct = default,
        int sessionIdleTimeoutSeconds = 0);

    Task WaitForReadyAsync(string service = DaprMQClient.OperationsHealthService, CancellationToken ct = default);
}
