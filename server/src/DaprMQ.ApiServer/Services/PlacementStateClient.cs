using System.Net;
using System.Text.Json;

namespace DaprMQ.ApiServer.Services;

public interface IPlacementStateClient
{
    /// <summary>
    /// IPs of the hosts with <paramref name="appId"/> that registered <paramref name="actorType"/>; null when no
    /// placement replica answered.
    /// </summary>
    Task<IReadOnlyList<IPAddress>?> GetHostsAsync(string appId, string actorType, string? ns, CancellationToken cancellationToken);
}

/// <summary>
/// Reads the placement tables from placement's healthz port (GET /placement/state, needs --metadata-enabled). Only the
/// Raft leader serves it, so every address the placement host resolves to is tried until one answers.
/// </summary>
public class PlacementStateClient(
    IHttpClientFactory httpClientFactory,
    string address,
    TimeSpan requestTimeout,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : IPlacementStateClient
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? Dns.GetHostAddressesAsync;

    public async Task<IReadOnlyList<IPAddress>?> GetHostsAsync(
        string appId, string actorType, string? ns, CancellationToken cancellationToken)
    {
        var endpoint = SplitHostPort(address);
        IPAddress[] candidates;
        try
        {
            candidates = IPAddress.TryParse(endpoint.Host, out var ip) ? [ip] : await _resolve(endpoint.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return null;
        }

        var client = httpClientFactory.CreateClient();
        foreach (var candidate in candidates)
        {
            var state = await TryReadAsync(client, $"http://{Format(candidate)}:{endpoint.Port}/placement/state", cancellationToken);
            if (state is null)
            {
                continue;
            }

            return (state.Tables ?? [])
                .SelectMany(t => t.Value?.Hosts ?? [])
                .Where(h => h.Id == appId
                            && (h.Entities?.Contains(actorType) ?? false)
                            && (ns is null || h.Namespace == ns))
                .Select(h => TryParseHostIp(h.Name))
                .OfType<IPAddress>()
                .ToList();
        }

        return null;
    }

    private async Task<PlacementState?> TryReadAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(requestTimeout);

            using var response = await client.GetAsync(url, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return JsonSerializer.Deserialize<PlacementState>(body, DeserializeOptions);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static (string Host, int Port) SplitHostPort(string hostPort)
    {
        var colon = hostPort.LastIndexOf(':');
        return (hostPort[..colon].Trim('[', ']'), int.Parse(hostPort[(colon + 1)..]));
    }

    // Host names are daprd's internal gRPC endpoint ("10.0.0.1:50002" / "[fd00::5]:50002"); only the IP is used.
    private static IPAddress? TryParseHostIp(string? name) =>
        name is not null && IPEndPoint.TryParse(name, out var ep) ? ep.Address : null;

    private static string Format(IPAddress ip) =>
        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();

    private sealed record PlacementState(Dictionary<string, PlacementTable?>? Tables);

    private sealed record PlacementTable(List<PlacementHost>? Hosts);

    private sealed record PlacementHost(string? Name, string? Id, string? Namespace, List<string>? Entities);
}
