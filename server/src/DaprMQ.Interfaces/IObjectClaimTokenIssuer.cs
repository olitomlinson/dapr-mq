namespace DaprMQ.Interfaces;

/// <summary>
/// Issues and resolves ObjectClaimTokens using a deployment-configured signing key and TTL,
/// so callers (QueueActor, QueueController) don't each need direct access to the signing key.
/// </summary>
public interface IObjectClaimTokenIssuer
{
    string Issue(string blobReference, string? contentType);

    bool TryResolve(string token, out ObjectClaimToken? claim, out bool isExpired);
}
