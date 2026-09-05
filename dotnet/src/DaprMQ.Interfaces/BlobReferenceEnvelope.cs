using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaprMQ.Interfaces;

/// <summary>
/// Sentinel JSON envelope used in place of ItemJson when a pushed item's payload has been
/// offloaded to an external object store. QueueActor never parses ordinary ItemJson content -
/// this is the one exception, used only to detect the offload marker.
/// </summary>
public record BlobReferenceEnvelope
{
    [JsonPropertyName("__daprmq_blob_ref__")]
    public required string BlobReference { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }

    public static string Build(string blobReference, long? size = null, string? contentType = null)
    {
        var envelope = new BlobReferenceEnvelope
        {
            BlobReference = blobReference,
            Size = size,
            ContentType = contentType
        };
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Attempts to parse itemJson as a blob reference envelope. Returns false for any normal
    /// item payload (including arbitrary JSON that doesn't happen to be this exact shape).
    /// </summary>
    public static bool TryParse(string itemJson, out string? blobReference)
    {
        var parsed = TryParseEnvelope(itemJson, out var envelope);
        blobReference = envelope?.BlobReference;
        return parsed;
    }

    /// <summary>
    /// Attempts to parse itemJson as a blob reference envelope, returning the full envelope
    /// (including size/contentType) rather than just the reference string.
    /// </summary>
    public static bool TryParseEnvelope(string itemJson, out BlobReferenceEnvelope? envelope)
    {
        envelope = null;

        if (string.IsNullOrEmpty(itemJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(itemJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("__daprmq_blob_ref__", out var refProperty) ||
                refProperty.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var blobReference = refProperty.GetString();
            if (string.IsNullOrEmpty(blobReference))
            {
                return false;
            }

            long? size = document.RootElement.TryGetProperty("size", out var sizeProperty) && sizeProperty.ValueKind == JsonValueKind.Number
                ? sizeProperty.GetInt64()
                : null;
            string? contentType = document.RootElement.TryGetProperty("contentType", out var contentTypeProperty) && contentTypeProperty.ValueKind == JsonValueKind.String
                ? contentTypeProperty.GetString()
                : null;

            envelope = new BlobReferenceEnvelope
            {
                BlobReference = blobReference,
                Size = size,
                ContentType = contentType
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
