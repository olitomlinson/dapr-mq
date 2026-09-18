namespace DaprMQ.Client;

public interface IDaprMQClient
{
    Task<EnqueueResult> EnqueueAsync(string queueId, IEnumerable<EnqueueItemDto> items, CancellationToken ct = default);

    Task<DequeueLockedResult?> DequeueLockedAsync(string queueId, int count = 1, int ttlSeconds = 30, string? leaseId = null, CancellationToken ct = default);

    Task AcknowledgeAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default);

    Task ExtendLockAsync(string queueId, string lockId, int additionalTtlSeconds, string? leaseId = null, CancellationToken ct = default);

    Task DeadLetterAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default);

    Task<SessionLease?> AcceptSessionAsync(string queueId, string? sessionId = null, int leaseSeconds = 30, CancellationToken ct = default);

    Task<SessionLease> RenewSessionLeaseAsync(string queueId, string sessionId, string leaseId, int additionalSeconds = 30, CancellationToken ct = default);

    Task ReleaseSessionAsync(string queueId, string sessionId, string leaseId, CancellationToken ct = default);

    IAsyncEnumerable<SessionDelivery> ConsumeSessionAsync(string queueId, string? sessionId, int leaseSeconds, int prefetchCount, CancellationToken ct = default);
}
