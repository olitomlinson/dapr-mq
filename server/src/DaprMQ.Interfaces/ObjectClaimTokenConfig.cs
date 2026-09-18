namespace DaprMQ.Interfaces;

/// <summary>
/// Configuration for issuing/validating ObjectClaimTokens.
/// </summary>
public class ObjectClaimTokenConfig
{
    /// <summary>
    /// Operator-controlled HMAC signing key, set once at deploy time (e.g. via
    /// DAPRMQ_OBJECT_CLAIM_SIGNING_KEY). Never accepted from a request.
    /// </summary>
    public required byte[] SigningKey { get; init; }

    /// <summary>
    /// How long an issued token remains valid before expiring.
    /// </summary>
    public required TimeSpan TokenTtl { get; init; }
}
