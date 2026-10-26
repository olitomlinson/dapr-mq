namespace DaprMQ.Interfaces;

/// <summary>
/// The object store holds nothing under the requested blob reference (never uploaded, or already reaped).
/// </summary>
public class ObjectNotFoundException(string blobReference, Exception? innerException = null)
    : Exception($"Object '{blobReference}' not found", innerException);

/// <summary>
/// The object store rejected an upload because it exceeds the store's maximum object size.
/// </summary>
public class ObjectTooLargeException(string blobReference, Exception? innerException = null)
    : Exception($"Object '{blobReference}' exceeds the object store's maximum size", innerException);

/// <summary>
/// The object store rejected an upload because it has no space left.
/// </summary>
public class ObjectStoreFullException(string blobReference, Exception? innerException = null)
    : Exception($"Object store is full; cannot store '{blobReference}'", innerException);
