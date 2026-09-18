namespace DaprMQ.Interfaces;

/// <summary>
/// Abstraction over an external object store used to offload large item payloads.
/// Lives in DaprMQ.Interfaces (rather than the ApiServer project) because BlobReaperActor,
/// hosted in the DaprMQ actor project, needs to call DeleteAsync directly.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Uploads content under {globalPrefix}/{userPrefix}/{objectId} and returns the resulting
    /// blob reference. globalPrefix is applied internally by the implementation.
    /// </summary>
    Task<string> UploadAsync(string userPrefix, string objectId, Stream content, string? contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stream to read back the content at the given blob reference.
    /// </summary>
    Task<Stream> DownloadAsync(string blobReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the content at the given blob reference.
    /// </summary>
    Task DeleteAsync(string blobReference, CancellationToken cancellationToken = default);
}
