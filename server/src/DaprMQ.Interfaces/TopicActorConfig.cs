namespace DaprMQ.Interfaces;

/// <summary>
/// Tunable defaults for TopicActor's relay/reaper/circuit-breaker behavior. Global for now
/// (PUBSUB-PLAN.md §9.4) - no per-topic override; exposed as config so tuning never requires a
/// schema change later.
/// </summary>
public class TopicActorConfig
{
    /// <summary>
    /// Max items read from the shared item log per subscriber per relay tick.
    /// </summary>
    public int RelayBatchSize { get; init; } = 100;

    /// <summary>
    /// Per-subscriber Enqueue timeout within a relay tick. Task.WhenAll bounds tick wall-clock cost
    /// by this value regardless of subscriber count.
    /// </summary>
    public int RelayTickTimeoutSeconds { get; init; } = 2;

    /// <summary>
    /// Ceiling for the relay reminder's dynamic backoff when a tick finds nothing new to send.
    /// </summary>
    public int RelayIntervalCeilingSeconds { get; init; } = 30;

    /// <summary>
    /// Fixed interval for the reap-items reminder.
    /// </summary>
    public int ReapItemsIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// How long a superseded subscriber-set generation snapshot is retained before deletion.
    /// </summary>
    public int GenerationRetentionDays { get; init; } = 7;

    /// <summary>
    /// Consecutive-failure streak duration after which a subscriber is blacklisted.
    /// </summary>
    public int CircuitBreakerBlacklistAfterSeconds { get; init; } = 3600;

    /// <summary>
    /// Cap on the exponential backoff delay between retry attempts for a failing subscriber.
    /// </summary>
    public int CircuitBreakerMaxBackoffSeconds { get; init; } = 300;
}
