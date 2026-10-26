namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Configuration for the object store used to offload large item payloads.
/// </summary>
public class ObjectStoreConfig
{
    /// <summary>
    /// Name of the Dapr Binary Store component (DAPRMQ_BLOB_STORE_NAME). A deployment-wide key prefix,
    /// if wanted, is set with the component's own <c>prefix</c> metadata.
    /// </summary>
    public required string StoreName { get; init; }

    /// <summary>
    /// Largest request body accepted by enqueue-object, in bytes (DAPRMQ_OBJECT_MAX_BYTES).
    /// </summary>
    public required long MaxObjectBytes { get; init; }
}
