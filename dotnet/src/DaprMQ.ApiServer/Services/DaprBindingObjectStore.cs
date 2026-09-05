using System.Text.RegularExpressions;
using Dapr.Client;
using DaprMQ.Interfaces;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// IObjectStore implementation backed by a Dapr output binding (bindings.localstorage,
/// bindings.aws.s3, bindings.azure.blobstorage, ...), selected by component YAML per environment.
/// </summary>
public class DaprBindingObjectStore : IObjectStore
{
    private static readonly Regex ValidPrefixPattern = new("^[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*$", RegexOptions.Compiled);

    private readonly DaprClient _daprClient;
    private readonly ObjectStoreConfig _config;

    public DaprBindingObjectStore(DaprClient daprClient, ObjectStoreConfig config)
    {
        _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<string> UploadAsync(string userPrefix, string objectId, Stream content, string? contentType, CancellationToken cancellationToken = default)
    {
        if (!IsValidPrefix(userPrefix))
        {
            throw new ArgumentException($"Invalid prefix '{userPrefix}'", nameof(userPrefix));
        }

        var key = BuildKey(userPrefix, objectId);

        using var memoryStream = new MemoryStream();
        await content.CopyToAsync(memoryStream, cancellationToken);
        var data = memoryStream.ToArray();

        var request = new BindingRequest(_config.BindingName, "create")
        {
            Data = data
        };
        request.Metadata["fileName"] = key;

        await _daprClient.InvokeBindingAsync(request, cancellationToken);

        return key;
    }

    public async Task<Stream> DownloadAsync(string blobReference, CancellationToken cancellationToken = default)
    {
        var request = new BindingRequest(_config.BindingName, "get");
        request.Metadata["fileName"] = blobReference;

        var response = await _daprClient.InvokeBindingAsync(request, cancellationToken);
        return new MemoryStream(response.Data.ToArray());
    }

    public async Task DeleteAsync(string blobReference, CancellationToken cancellationToken = default)
    {
        var request = new BindingRequest(_config.BindingName, "delete");
        request.Metadata["fileName"] = blobReference;

        await _daprClient.InvokeBindingAsync(request, cancellationToken);
    }

    private string BuildKey(string userPrefix, string objectId)
    {
        return string.IsNullOrEmpty(_config.GlobalPrefix)
            ? $"{userPrefix}/{objectId}"
            : $"{_config.GlobalPrefix}/{userPrefix}/{objectId}";
    }

    private static bool IsValidPrefix(string prefix)
    {
        return !string.IsNullOrEmpty(prefix) && ValidPrefixPattern.IsMatch(prefix);
    }
}
