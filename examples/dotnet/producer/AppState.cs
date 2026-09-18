using DaprMQ.Client;

/// <summary>
/// Holds the container's mutable DaprMQ connection config and the in-process
/// DaprMQClient built from it. ASP.NET Core singleton services are normally
/// immutable, so PUT /config rebuilds the client behind this holder's lock
/// rather than trying to mutate DaprMQClient itself.
/// </summary>
public sealed class AppState
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private readonly string _defaultHttpBaseUrl;
    private readonly string _defaultGrpcAddress;
    private readonly string _defaultQueuePrefix;

    private string _httpBaseUrl;
    private string _grpcAddress;
    private string _queuePrefix;
    private string _source = "default";
    private DaprMQClient _client;

    public string Language { get; }
    public string Role { get; }

    public AppState(string language, string role)
    {
        Language = language;
        Role = role;

        _defaultHttpBaseUrl = EnvOrDefault("DAPRMQ_HTTP_BASE_URL", "http://localhost:8002");
        _defaultGrpcAddress = EnvOrDefault("DAPRMQ_GRPC_ADDRESS", "localhost:8003");
        _defaultQueuePrefix = EnvOrDefault("DAPRMQ_QUEUE_PREFIX", $"examples-{language}");

        _httpBaseUrl = _defaultHttpBaseUrl;
        _grpcAddress = _defaultGrpcAddress;
        _queuePrefix = _defaultQueuePrefix;
        _client = BuildClient(_httpBaseUrl, _grpcAddress);
    }

    private static string EnvOrDefault(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    /// <summary>
    /// GrpcChannel.ForAddress requires a scheme (see every example in CLIENT_SDK.md /
    /// CLUSTER_DISCOVERY.md), but the control-plane contract stores/returns grpcAddress
    /// as a bare host:port (API_CONTRACT.md). Add the scheme only when actually
    /// constructing the SDK client; the stored/returned config value stays bare.
    /// </summary>
    private static DaprMQClient BuildClient(string httpBaseUrl, string grpcAddress)
    {
        var normalizedHttp = httpBaseUrl.EndsWith('/') ? httpBaseUrl : httpBaseUrl + "/";
        var grpcTarget = grpcAddress.Contains("://", StringComparison.Ordinal)
            ? grpcAddress
            : $"http://{grpcAddress}";

        return new DaprMQClient(new DaprMQClientOptions
        {
            HttpBaseAddress = new Uri(normalizedHttp),
            GrpcAddress = grpcTarget
        });
    }

    public (IDaprMQClient Client, string QueuePrefix) Snapshot()
    {
        lock (_lock)
        {
            return (_client, _queuePrefix);
        }
    }

    public ConfigDto GetConfigDto()
    {
        lock (_lock)
        {
            return new ConfigDto(_httpBaseUrl, _grpcAddress, _queuePrefix, Language, Role, _source);
        }
    }

    public (bool Ok, string? Error) TryUpdateConfig(ConfigUpdateRequest? body)
    {
        body ??= new ConfigUpdateRequest(null, null, null);

        if (body.HttpBaseUrl is not null && !IsValidHttpUrl(body.HttpBaseUrl))
        {
            return (false, "httpBaseUrl must be a valid absolute http(s) URL");
        }

        if (body.GrpcAddress is not null && !IsValidHostPort(body.GrpcAddress))
        {
            return (false, "grpcAddress must be a non-empty host:port value");
        }

        if (body.QueuePrefix is not null && string.IsNullOrWhiteSpace(body.QueuePrefix))
        {
            return (false, "queuePrefix must not be empty");
        }

        DaprMQClient old;
        lock (_lock)
        {
            if (body.HttpBaseUrl is not null) _httpBaseUrl = body.HttpBaseUrl;
            if (body.GrpcAddress is not null) _grpcAddress = body.GrpcAddress;
            if (body.QueuePrefix is not null) _queuePrefix = body.QueuePrefix;
            _source = "override";

            old = _client;
            _client = BuildClient(_httpBaseUrl, _grpcAddress);
        }

        _ = SafeDisposeAsync(old);
        return (true, null);
    }

    public void Reset()
    {
        DaprMQClient old;
        lock (_lock)
        {
            _httpBaseUrl = _defaultHttpBaseUrl;
            _grpcAddress = _defaultGrpcAddress;
            _queuePrefix = _defaultQueuePrefix;
            _source = "default";

            old = _client;
            _client = BuildClient(_httpBaseUrl, _grpcAddress);
        }

        _ = SafeDisposeAsync(old);
        // No other in-memory scenario bookkeeping (lock ids / session ids / lease ids) is
        // cached across runs in this implementation - each scenario run is self-contained.
    }

    private static async Task SafeDisposeAsync(DaprMQClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch
        {
            // best-effort - the client may already be mid-request when replaced
        }
    }

    private static bool IsValidHttpUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsValidHostPort(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var stripped = value;
        var schemeIdx = stripped.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) stripped = stripped[(schemeIdx + 3)..];

        var colonIdx = stripped.LastIndexOf(':');
        if (colonIdx <= 0 || colonIdx == stripped.Length - 1) return false;

        var host = stripped[..colonIdx];
        var portText = stripped[(colonIdx + 1)..];
        return host.Length > 0 && int.TryParse(portText, out var port) && port is > 0 and <= 65535;
    }

    /// <summary>Non-blocking mutual exclusion for scenario runs - one run in flight per pod.</summary>
    public bool TryBeginRun() => _runLock.Wait(0);

    public void EndRun() => _runLock.Release();
}
