namespace DaprMQ.Interfaces;

/// <summary>
/// Broker-wide message expiry settings (proposals/message-expiry.md). Every message gets an expiry:
/// the producer's time to live, or DefaultExpiryTtlSeconds. Expired items are removed lazily, when a
/// dequeue reaches them; there is no background sweep.
/// </summary>
public class ExpiryConfig
{
    /// <summary>Ten years, the longest time to live a producer may ask for.</summary>
    public const int MaxExpiryTtlSeconds = 315_360_000;

    /// <summary>
    /// Shortest time to live accepted (DAPRMQ_MIN_EXPIRY_TTL_SECONDS). Keeps every expiry well clear
    /// of clock skew between the node that stamps it and the one that checks it. Shorter values are
    /// rejected, not raised.
    /// </summary>
    public int MinExpiryTtlSeconds { get; init; } = 600;

    /// <summary>Applied when the producer gives none (DAPRMQ_DEFAULT_EXPIRY_TTL_SECONDS); one year.</summary>
    public int DefaultExpiryTtlSeconds { get; init; } = 31_536_000;

    /// <summary>
    /// Time to live a dead-lettered item gets, from the moment it is dead-lettered
    /// (DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS); 28 days.
    /// </summary>
    public int DeadLetterExpiryTtlSeconds { get; init; } = 2_419_200;

    /// <summary>
    /// Most expired items one actor turn removes before it commits and returns ExpiredMore
    /// (DAPRMQ_EXPIRY_SWEEP_BATCH_SIZE).
    /// </summary>
    public int ExpirySweepBatchSize { get; init; } = 200;

    /// <summary>
    /// Whether an expired item is dead-lettered rather than dropped (DAPRMQ_DEADLETTER_ON_EXPIRY).
    /// Never applies inside a dead-letter queue.
    /// </summary>
    public bool DeadLetterOnExpiry { get; init; }

    /// <summary>Fails startup on settings no request could satisfy.</summary>
    public void Validate()
    {
        if (MinExpiryTtlSeconds < 1 || MinExpiryTtlSeconds > MaxExpiryTtlSeconds)
        {
            throw new InvalidOperationException(
                $"DAPRMQ_MIN_EXPIRY_TTL_SECONDS must be between 1 and {MaxExpiryTtlSeconds}, got {MinExpiryTtlSeconds}");
        }

        RequireWithinRange("DAPRMQ_DEFAULT_EXPIRY_TTL_SECONDS", DefaultExpiryTtlSeconds);
        RequireWithinRange("DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS", DeadLetterExpiryTtlSeconds);

        if (ExpirySweepBatchSize < 1)
        {
            throw new InvalidOperationException($"DAPRMQ_EXPIRY_SWEEP_BATCH_SIZE must be at least 1, got {ExpirySweepBatchSize}");
        }
    }

    /// <summary>True if a producer's time to live is within the accepted range.</summary>
    public bool IsValidExpiryTtl(int expiryTtlSeconds) =>
        expiryTtlSeconds >= MinExpiryTtlSeconds && expiryTtlSeconds <= MaxExpiryTtlSeconds;

    private void RequireWithinRange(string name, int value)
    {
        if (!IsValidExpiryTtl(value))
        {
            throw new InvalidOperationException(
                $"{name} must be between DAPRMQ_MIN_EXPIRY_TTL_SECONDS ({MinExpiryTtlSeconds}) and {MaxExpiryTtlSeconds}, got {value}");
        }
    }
}
