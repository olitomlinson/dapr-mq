using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace DaprMQ.Operator.Scaling;

/// <summary>
/// Which ScaledObjects scale on which queue, learned from KEDA's own calls: every call carries
/// ScaledObjectRef { name, namespace, scalerMetadata }, so no Kubernetes API access is needed. A
/// link expires <see cref="Expiry"/> after its last call, so deleted ScaledObjects drop out. Exported
/// for the console's scaling overlay, which joins them to kube-state-metrics' HPA series:
///   daprmq_scaler_binding{queue, mode, scaled_object_namespace, scaled_object, hpa, target_value, activation_value} 1
///   daprmq_scaler_metric_value{queue, mode, scaled_object_namespace, scaled_object}   (last value reported to KEDA)
/// The namespace label isn't called "namespace": a scrape would rename it, since the target has one.
/// </summary>
public sealed class ScalerBindingRegistry : IDisposable
{
    public const string MeterName = "DaprMQ.Operator";
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(string Namespace, string ScaledObject, string Queue, string Mode), Binding> _bindings = new();

    public record Binding(
        string Queue, string Mode, string Namespace, string ScaledObject, string Hpa,
        double TargetValue, double ActivationValue, DateTimeOffset LastSeen, double? MetricValue);

    public ScalerBindingRegistry(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Meter = new Meter(MeterName);
        Meter.CreateObservableGauge("daprmq.scaler.binding", ObserveBindings, description: "A ScaledObject that scales on a queue (1 while KEDA is calling)");
        Meter.CreateObservableGauge("daprmq.scaler.metric_value", ObserveMetricValues, description: "The metric value last reported to KEDA");
    }

    public Meter Meter { get; }

    /// <summary>Records a call; <paramref name="metricValue"/> when this call computed one.</summary>
    public void Record(string? @namespace, string? scaledObject, ScalerMetadata metadata, double? metricValue = null)
    {
        var key = (@namespace ?? string.Empty, scaledObject ?? string.Empty, metadata.QueueId, metadata.Mode.ToString().ToLowerInvariant());
        _bindings.AddOrUpdate(key,
            _ => Create(key, metadata, metricValue),
            (_, existing) => Create(key, metadata, metricValue ?? existing.MetricValue));
    }

    /// <summary>Links seen within <see cref="Expiry"/>; older ones are dropped.</summary>
    public IReadOnlyList<Binding> Active()
    {
        var cutoff = _timeProvider.GetUtcNow() - Expiry;
        foreach (var stale in _bindings.Where(b => b.Value.LastSeen < cutoff))
        {
            _bindings.TryRemove(stale);
        }

        return _bindings.Values.ToList();
    }

    private Binding Create((string Namespace, string ScaledObject, string Queue, string Mode) key, ScalerMetadata metadata, double? metricValue) =>
        new(key.Queue, key.Mode, key.Namespace, key.ScaledObject,
            // KEDA names the HPA it creates keda-hpa-{scaledObject} unless the ScaledObject overrides it.
            metadata.HpaName ?? $"keda-hpa-{key.ScaledObject}",
            metadata.TargetValue, metadata.ActivationValue, _timeProvider.GetUtcNow(), metricValue);

    private IEnumerable<Measurement<long>> ObserveBindings() =>
        Active().Select(b => new Measurement<long>(1,
            new("queue", b.Queue), new("mode", b.Mode), new("scaled_object_namespace", b.Namespace), new("scaled_object", b.ScaledObject),
            new("hpa", b.Hpa), new("target_value", Number(b.TargetValue)), new("activation_value", Number(b.ActivationValue))));

    private IEnumerable<Measurement<double>> ObserveMetricValues() =>
        Active().Where(b => b.MetricValue.HasValue).Select(b => new Measurement<double>(b.MetricValue!.Value,
            new("queue", b.Queue), new("mode", b.Mode), new("scaled_object_namespace", b.Namespace), new("scaled_object", b.ScaledObject)));

    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    public void Dispose() => Meter.Dispose();
}
