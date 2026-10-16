using System.Net;
using System.Text.Json;
using k8s;
using k8s.Autorest;

namespace DaprMQ.Operator.Console;

/// <summary>Reads a ScaledObject's (or ScaledJob's) status and replica counts.</summary>
public interface IScalingStatusReader
{
    Task<KubernetesScalingStatus> ReadAsync(string @namespace, string name, CancellationToken cancellationToken);
}

/// <summary>
/// Reads from the Kubernetes API with the pod's service account (Helm console.kubernetesRead grants
/// get on scaledobjects, scaledjobs and horizontalpodautoscalers, and list on jobs). KEDA's calls to
/// the scaler don't say whether a name is a ScaledObject or a ScaledJob, so it tries both.
/// </summary>
public sealed class KubernetesScalingStatusReader : IScalingStatusReader
{
    private const string KedaGroup = "keda.sh";
    private const string KedaVersion = "v1alpha1";

    // KEDA's defaults for unset bounds, on both ScaledObjects and ScaledJobs.
    private const int DefaultMinReplicas = 0;
    private const int DefaultMaxReplicas = 100;

    private readonly IKubernetes _client;

    public KubernetesScalingStatusReader(IKubernetes client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<KubernetesScalingStatus> ReadAsync(string @namespace, string name, CancellationToken cancellationToken)
    {
        try
        {
            var scaledObject = await GetKedaObjectAsync(@namespace, "scaledobjects", name, cancellationToken);
            if (scaledObject is { } so)
            {
                var (status, hpaName) = ParseScaledObject(so);
                try
                {
                    var hpa = await _client.AutoscalingV2.ReadNamespacedHorizontalPodAutoscalerAsync(hpaName, @namespace, cancellationToken: cancellationToken);
                    return status with { CurrentReplicas = hpa.Status?.CurrentReplicas, DesiredReplicas = hpa.Status?.DesiredReplicas };
                }
                catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    // KEDA hasn't created it yet (or the ScaledObject is paused).
                    return status;
                }
            }

            var scaledJob = await GetKedaObjectAsync(@namespace, "scaledjobs", name, cancellationToken);
            if (scaledJob is { } sj)
            {
                var jobs = await _client.BatchV1.ListNamespacedJobAsync(
                    @namespace, labelSelector: $"scaledjob.keda.sh/name={name}", cancellationToken: cancellationToken);
                return ParseScaledJob(sj) with { ActiveJobs = jobs.Items.Count(j => j.Status?.Active > 0) };
            }

            return new KubernetesScalingStatus { Error = $"No ScaledObject or ScaledJob {@namespace}/{name}" };
        }
        catch (HttpOperationException ex)
        {
            return new KubernetesScalingStatus { Error = $"Kubernetes API {(int)ex.Response.StatusCode}: {ex.Response.ReasonPhrase}" };
        }
    }

    private async Task<JsonElement?> GetKedaObjectAsync(string @namespace, string plural, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(
                KedaGroup, KedaVersion, @namespace, plural, name, cancellationToken);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>A ScaledObject's bounds and conditions, and the name of the HPA KEDA made for it.</summary>
    public static (KubernetesScalingStatus Status, string HpaName) ParseScaledObject(JsonElement scaledObject)
    {
        var name = scaledObject.GetProperty("metadata").GetProperty("name").GetString();
        var hpaName = String(scaledObject, "status", "hpaName") ?? $"keda-hpa-{name}";
        return (Parse(scaledObject, "ScaledObject"), hpaName);
    }

    public static KubernetesScalingStatus ParseScaledJob(JsonElement scaledJob) => Parse(scaledJob, "ScaledJob");

    private static KubernetesScalingStatus Parse(JsonElement obj, string kind) => new()
    {
        Kind = kind,
        MinReplicas = Int(obj, "spec", "minReplicaCount") ?? DefaultMinReplicas,
        MaxReplicas = Int(obj, "spec", "maxReplicaCount") ?? DefaultMaxReplicas,
        Conditions = obj.TryGetProperty("status", out var status)
            && status.TryGetProperty("conditions", out var conditions)
            && conditions.ValueKind == JsonValueKind.Array
            ? conditions.EnumerateArray().Select(c => new ScalingCondition
            {
                Type = c.GetProperty("type").GetString() ?? string.Empty,
                Status = c.GetProperty("status").GetString() ?? string.Empty,
                Reason = c.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
                Message = c.TryGetProperty("message", out var message) ? message.GetString() : null
            }).ToList()
            : []
    };

    private static JsonElement? Property(JsonElement obj, string section, string name) =>
        obj.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    private static int? Int(JsonElement obj, string section, string name) => Property(obj, section, name)?.GetInt32();

    private static string? String(JsonElement obj, string section, string name) => Property(obj, section, name)?.GetString();
}
