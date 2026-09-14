namespace DaprMQ.Interfaces;

/// <summary>
/// Configuration for idempotency-key deduplication on Push. A single global TTL window; no
/// per-request override.
/// </summary>
public class IdempotencyConfig
{
    /// <summary>
    /// How long a used idempotency key is remembered before it can be reused. Enforced natively
    /// by the actor state store's per-entry TTL.
    /// </summary>
    public required int TtlSeconds { get; init; }

    /// <summary>
    /// Whether to unload each idempotency-key state entry from the actor's in-memory tracker
    /// right after it commits. True (default) bounds actor memory to roughly the keys touched
    /// by the in-flight batch, at the cost of a state-store round-trip the next time an
    /// already-seen key is rechecked. False keeps every checked key resident in memory for
    /// fast rechecks, but grows unbounded for as long as the actor stays continuously active.
    /// </summary>
    public bool UnloadAfterCommit { get; init; } = true;
}
