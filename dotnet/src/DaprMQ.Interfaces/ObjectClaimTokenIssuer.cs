namespace DaprMQ.Interfaces;

public class ObjectClaimTokenIssuer : IObjectClaimTokenIssuer
{
    private readonly ObjectClaimTokenConfig _config;

    public ObjectClaimTokenIssuer(ObjectClaimTokenConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string Issue(string blobReference, string? contentType) =>
        ObjectClaimToken.Create(blobReference, contentType, _config.TokenTtl, _config.SigningKey);

    public bool TryResolve(string token, out ObjectClaimToken? claim, out bool isExpired) =>
        ObjectClaimToken.TryParse(token, _config.SigningKey, out claim, out isExpired);
}
