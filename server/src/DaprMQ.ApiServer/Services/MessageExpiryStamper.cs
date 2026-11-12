using DaprMQ.Interfaces;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Validates a producer's time to live and turns it into an absolute expiry, once, when the API server
/// receives the request. Actors never compute an expiry or apply the default.
/// </summary>
public class MessageExpiryStamper(ExpiryConfig config, TimeProvider timeProvider)
{
    /// <summary>The 400 / InvalidArgument message for an out-of-range time to live, or null if it is valid or omitted.</summary>
    public string? Validate(int? expiryTtlSeconds) =>
        expiryTtlSeconds is int ttl && !config.IsValidExpiryTtl(ttl)
            ? $"expiryTtlSeconds must be between {config.MinExpiryTtlSeconds} and {ExpiryConfig.MaxExpiryTtlSeconds}"
            : null;

    /// <summary>Sets ExpiryTtlSeconds (the producer's, or the default) and ExpiryAt = now + ExpiryTtlSeconds.</summary>
    public EnqueueItem Stamp(EnqueueItem item, int? expiryTtlSeconds)
    {
        int ttl = expiryTtlSeconds ?? config.DefaultExpiryTtlSeconds;
        return item with
        {
            ExpiryTtlSeconds = ttl,
            ExpiryAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() + ttl
        };
    }
}
