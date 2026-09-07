using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace DaprMQ.Interfaces;

/// <summary>
/// Opaque, self-encoding claim to a blob offloaded to the object store. Issued in place of a raw
/// BlobReference wherever items are exposed to end users, so the object store key is never exposed
/// directly. A signed JWT (HS256) carrying the blob reference, content type, and expiry - validated
/// standalone via <see cref="TryParse"/>, with no actor/state lookup required to resolve it.
/// </summary>
public record ObjectClaimToken
{
    private const string BlobReferenceClaimType = "blobReference";
    private const string ContentTypeClaimType = "contentType";

    public required string BlobReference { get; init; }
    public string? ContentType { get; init; }

    public static string Create(string blobReference, string? contentType, TimeSpan ttl, byte[] signingKey)
    {
        var claims = new List<Claim>
        {
            new(BlobReferenceClaimType, blobReference)
        };
        if (contentType != null)
        {
            claims.Add(new Claim(ContentTypeClaimType, contentType));
        }

        var credentials = new SigningCredentials(new SymmetricSecurityKey(signingKey), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow + ttl,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>
    /// Validates signature and expiry, then extracts the claim. Returns false for any invalid,
    /// tampered, expired, or malformed token.
    /// </summary>
    public static bool TryParse(string token, byte[] signingKey, out ObjectClaimToken? claim) =>
        TryParse(token, signingKey, out claim, out _);

    /// <summary>
    /// Same as <see cref="TryParse(string, byte[], out ObjectClaimToken?)"/>, additionally reporting
    /// whether a failed parse was specifically due to expiry (vs. any other invalid/tampered/malformed
    /// token), so callers can distinguish 410 Gone from 400 Bad Request.
    /// </summary>
    public static bool TryParse(string token, byte[] signingKey, out ObjectClaimToken? claim, out bool isExpired)
    {
        claim = null;
        isExpired = false;

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(signingKey),
            ClockSkew = TimeSpan.Zero
        };

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, validationParameters, out _);

            var blobReference = principal.Claims.FirstOrDefault(c => c.Type == BlobReferenceClaimType)?.Value;
            if (string.IsNullOrEmpty(blobReference))
            {
                return false;
            }

            claim = new ObjectClaimToken
            {
                BlobReference = blobReference,
                ContentType = principal.Claims.FirstOrDefault(c => c.Type == ContentTypeClaimType)?.Value
            };
            return true;
        }
        catch (SecurityTokenExpiredException)
        {
            isExpired = true;
            return false;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
