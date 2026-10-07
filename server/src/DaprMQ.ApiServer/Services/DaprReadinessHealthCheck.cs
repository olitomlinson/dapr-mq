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
}

/// <summary>
/// Instance readiness from the sidecar's own view of its actor runtime (/v1.0/metadata), without
/// invoking or activating an actor. An actor host is ready once it hosts QueueActor; a gateway once
/// its sidecar is connected to placement. A gateway deliberately doesn't depend on any worker: a full
/// worker outage must not take every gateway out of rotation, since callers' requests can wait for a
/// worker within their deadline (proposals/readiness-and-retries.md, section 1).
/// </summary>
public class DaprReadinessHealthCheck(
    DaprClient daprClient,
    IHttpClientFactory httpClientFactory,
    DaprReadinessOptions options) : IHealthCheck
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _metadataUrl = $"{options.DaprHttpEndpoint.TrimEnd('/')}/v1.0/metadata";

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

    private sealed record SidecarMetadata(ActorRuntimeMetadata? ActorRuntime);

    private sealed record ActorRuntimeMetadata(bool HostReady, List<ActiveActorType>? ActiveActors);

    private sealed record ActiveActorType(string Type);
}
