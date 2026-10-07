using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DaprMQ.ApiServer.Services;

public class OperationsHealthCheckOptions
{
    public required string DaprHttpEndpoint { get; init; }

    /// <summary>Dapr app-id of the workers (WORKER_APP_ID).</summary>
    public required string WorkerAppId { get; init; }

    public int Attempts { get; init; } = 3;

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Gateway side of the "daprmq.DaprMQ.operations" signal: can any worker serve queue operations?
/// Asks a worker's /internal/operations-ready (its own instance readiness, mapped only where queues
/// are hosted) through this gateway's sidecar. Service invocation reaches one load-balanced worker
/// per call, so any success proves one is available; a few attempts ride out an unready one.
/// For monitoring and clients that choose to wait - never part of this gateway's readiness, so a
/// worker outage doesn't take gateways out of rotation (proposals/readiness-and-retries.md, section 5).
/// </summary>
public class OperationsHealthCheck(IHttpClientFactory httpClientFactory, OperationsHealthCheckOptions options) : IHealthCheck
{
    public const string WorkerRoute = "internal/operations-ready";

    private readonly string _url = $"{options.DaprHttpEndpoint.TrimEnd('/')}/v1.0/invoke/{options.WorkerAppId}/method/{WorkerRoute}";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient();
        for (var attempt = 0; attempt < options.Attempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(options.AttemptTimeout);
                using var response = await client.GetAsync(_url, timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return HealthCheckResult.Healthy();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }

        return HealthCheckResult.Unhealthy("no worker available");
    }
}
