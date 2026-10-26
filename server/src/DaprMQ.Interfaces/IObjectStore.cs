namespace DaprMQ.Interfaces;

/// <summary>
/// Abstraction over an external object store used to offload large item payloads.
/// Lives in DaprMQ.Interfaces (rather than the ApiServer project) because BlobReaperActor,
/// hosted in the DaprMQ actor project, needs to call DeleteAsync directly.
/// </summary>
public interface IObjectStore
{
    /// <summary>
    /// Uploads content under {userPrefix}/{objectId} and returns the resulting blob reference.
    /// Throws <see cref="ObjectTooLargeException"/> or <see cref="ObjectStoreFullException"/> when the store rejects it.
    /// </summary>
    Task<string> UploadAsync(string userPrefix, string objectId, Stream content, string? contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a stream to read back the content at the given blob reference.
    /// Throws <see cref="ObjectNotFoundException"/> before returning when nothing is stored there.
    /// </summary>
    Task<Stream> DownloadAsync(string blobReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the content at the given blob reference. Deleting a missing object succeeds.
    /// </summary>
    Task DeleteAsync(string blobReference, CancellationToken cancellationToken = default);
}
