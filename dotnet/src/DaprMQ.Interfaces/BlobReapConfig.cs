namespace DaprMQ.Interfaces;

/// <summary>
/// Configuration for the two-TTL blob reap schedule: a backstop scheduled at item finalization
/// (covers a claim token that's never redeemed) and a post-download extension applied when the
/// object is actually fetched via the claim-token download endpoint.
/// </summary>
public class BlobReapConfig
{
    /// <summary>
    /// Seconds after item finalization (plain Pop, or Acknowledge for PopWithAck) before the
    /// blob is deleted if its claim token is never redeemed.
    /// </summary>
    public required int BackstopSeconds { get; init; }

    /// <summary>
    /// Seconds from a successful claim-token download before the blob is deleted. Only applied
    /// if later than whatever deletion time is currently scheduled.
    /// </summary>
    public required int PostDownloadSeconds { get; init; }
}
