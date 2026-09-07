namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Configuration for the object store used to offload large item payloads.
/// </summary>
public class ObjectStoreConfig
{
    /// <summary>
    /// Operator-controlled prefix applied to every blob key, set once at deploy time
    /// (e.g. via DAPRMQ_BLOB_GLOBAL_PREFIX). Never accepted from a request.
    /// </summary>
    public required string GlobalPrefix { get; init; }

    /// <summary>
    /// Name of the Dapr output binding component used for object storage (e.g. bindings.localstorage).
    /// </summary>
    public required string BindingName { get; init; }
}
