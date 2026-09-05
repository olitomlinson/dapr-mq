using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class ObjectClaimTokenTests
{
    private static readonly byte[] SigningKey = "test-signing-key-that-is-long-enough-for-hmac-sha256"u8.ToArray();

    [Fact]
    public void CreateAndTryParse_RoundTripsBlobReferenceAndContentType()
    {
        var token = ObjectClaimToken.Create("my-queue/obj-1", "application/pdf", TimeSpan.FromMinutes(5), SigningKey);

        var parsed = ObjectClaimToken.TryParse(token, SigningKey, out var claim);

        Assert.True(parsed);
        Assert.Equal("my-queue/obj-1", claim!.BlobReference);
        Assert.Equal("application/pdf", claim.ContentType);
    }

    [Fact]
    public void TryParse_NullContentType_RoundTrips()
    {
        var token = ObjectClaimToken.Create("my-queue/obj-2", null, TimeSpan.FromMinutes(5), SigningKey);

        var parsed = ObjectClaimToken.TryParse(token, SigningKey, out var claim);

        Assert.True(parsed);
        Assert.Equal("my-queue/obj-2", claim!.BlobReference);
        Assert.Null(claim.ContentType);
    }

    [Fact]
    public void TryParse_TamperedToken_ReturnsFalse()
    {
        var token = ObjectClaimToken.Create("my-queue/obj-3", "application/pdf", TimeSpan.FromMinutes(5), SigningKey);
        var tampered = token[..^2] + "xx";

        var parsed = ObjectClaimToken.TryParse(tampered, SigningKey, out var claim);

        Assert.False(parsed);
        Assert.Null(claim);
    }

    [Fact]
    public void TryParse_ExpiredToken_ReturnsFalse()
    {
        var token = ObjectClaimToken.Create("my-queue/obj-4", "application/pdf", TimeSpan.FromSeconds(-1), SigningKey);

        var parsed = ObjectClaimToken.TryParse(token, SigningKey, out var claim);

        Assert.False(parsed);
        Assert.Null(claim);
    }

    [Fact]
    public void TryParse_WrongSigningKey_ReturnsFalse()
    {
        var token = ObjectClaimToken.Create("my-queue/obj-5", "application/pdf", TimeSpan.FromMinutes(5), SigningKey);
        var wrongKey = "a-completely-different-signing-key-value-here"u8.ToArray();

        var parsed = ObjectClaimToken.TryParse(token, wrongKey, out var claim);

        Assert.False(parsed);
        Assert.Null(claim);
    }

    [Fact]
    public void TryParse_MalformedToken_ReturnsFalse()
    {
        var parsed = ObjectClaimToken.TryParse("not-a-jwt", SigningKey, out var claim);

        Assert.False(parsed);
        Assert.Null(claim);
    }
}
