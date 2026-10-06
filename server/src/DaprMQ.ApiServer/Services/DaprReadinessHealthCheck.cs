using System.Text.Json;
using Dapr.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DaprMQ.ApiServer.Services;

public class DaprReadinessOptions
{
    public required string DaprHttpEndpoint { get; init; }

    /// <summary>
    /// Actor type this instance must host to be ready; null on a gateway (REGISTER_ACTORS=false).
    /// </summary>
    public string? RequiredActorType { get; init; }

    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gateway only: how to find the workers in placement and probe them; ready when one answers. Null on a worker or single process.
    /// </summary>
    public WorkerProbeOptions? WorkerProbe { get; init; }
}

public record WorkerProbeOptions
{
    public required string AppId { get; init; }

    public required string ActorType { get; init; }

    public required int AppPort { get; init; }

    public string? Namespace { get; init; }

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Readiness from the sidecar's own view of its actor runtime (/v1.0/metadata), without invoking or
/// activating an actor. A gateway hosts no actors, so it also reads the placement table for the
/// workers that registered QueueActor and probes their /health/ready directly, one at a time, until
/// one answers.
/// </summary>
public class DaprReadinessHealthCheck(
    DaprClient daprClient,
    IHttpClientFactory httpClientFactory,
    DaprReadinessOptions options,
    IPlacementStateClient? placement = null) : IHealthCheck
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _metadataUrl = $"{options.DaprHttpEndpoint.TrimEnd('/')}/v1.0/metadata";

    private volatile System.Net.IPAddress? _lastReadyWorker;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Outbound, not /healthz: /healthz also waits on the app channel, which in single-process
        // mode is this process.
        if (!await daprClient.CheckOutboundHealthAsync(cancellationToken))
        {
            return HealthCheckResult.Unhealthy("sidecar");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.MetadataTimeout);

            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(_metadataUrl, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy("actors");
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var runtime = JsonSerializer.Deserialize<SidecarMetadata>(body, DeserializeOptions)?.ActorRuntime;
            if (runtime is not { HostReady: true })
            {
                return HealthCheckResult.Unhealthy("actors");
            }

            if (options.RequiredActorType is { } actorType &&
                !(runtime.ActiveActors?.Any(a => a.Type == actorType) ?? false))
            {
                return HealthCheckResult.Unhealthy("actors");
            }

            if (options.WorkerProbe is { } probe)
            {
                return await CheckWorkersAsync(client, probe, cancellationToken);
            }

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return HealthCheckResult.Unhealthy("actors", ex);
        }
    }

    private async Task<HealthCheckResult> CheckWorkersAsync(
        HttpClient client, WorkerProbeOptions probe, CancellationToken cancellationToken)
    {
        if (placement is null)
        {
            throw new InvalidOperationException($"{nameof(IPlacementStateClient)} is required for the gateway worker check");
        }

        var hosts = await placement.GetHostsAsync(probe.AppId, probe.ActorType, probe.Namespace, cancellationToken);
        if (hosts is null)
        {
            return HealthCheckResult.Unhealthy("placement");
        }

        // Directly, not via service invocation: invocation reaches one load-balanced instance, and the
        // workers' own sidecar API isn't reachable off-pod. Their /health/ready checks their sidecar.
        // One ready worker is enough, so try the last one that answered, then the rest in random order.
        var order = hosts.OrderBy(_ => Random.Shared.Next()).OrderByDescending(h => h.Equals(_lastReadyWorker)).ToList();
        var probed = 0;
        foreach (var host in order)
        {
            probed++;
            if (await ProbeAsync(client, host, probe, cancellationToken))
            {
                _lastReadyWorker = host;
                return HealthCheckResult.Healthy(data: new Dictionary<string, object> { ["registered"] = hosts.Count, ["probed"] = probed });
            }
        }

        return HealthCheckResult.Unhealthy("workers", data: new Dictionary<string, object> { ["registered"] = hosts.Count, ["probed"] = probed });
    }

    private static async Task<bool> ProbeAsync(
        HttpClient client, System.Net.IPAddress host, WorkerProbeOptions probe, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(probe.ProbeTimeout);

            var hostText = host.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host.ToString();
            using var response = await client.GetAsync($"http://{hostText}:{probe.AppPort}/health/ready", timeoutCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private sealed record SidecarMetadata(ActorRuntimeMetadata? ActorRuntime);

    private sealed record ActorRuntimeMetadata(bool HostReady, List<ActiveActorType>? ActiveActors);

    private sealed record ActiveActorType(string Type);
}
