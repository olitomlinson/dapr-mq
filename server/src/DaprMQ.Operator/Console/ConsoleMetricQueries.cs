using System.Text.RegularExpressions;

namespace DaprMQ.Operator.Console;

/// <summary>
/// The fixed set of PromQL the console may run. Arbitrary PromQL is never accepted: a request names
/// one of these and supplies, at most, entity ids, which are escaped into label matchers.
/// </summary>
public static class ConsoleMetricQueries
{
    public static readonly IReadOnlyDictionary<string, TimeSpan> Ranges = new Dictionary<string, TimeSpan>
    {
        ["15m"] = TimeSpan.FromMinutes(15),
        ["1h"] = TimeSpan.FromHours(1),
        ["6h"] = TimeSpan.FromHours(6),
        ["24h"] = TimeSpan.FromHours(24)
    };

    /// <summary>Points per range series.</summary>
    public const int PointsPerRange = 120;

    public const int MaxIds = 50;

    /// <summary>One query of a named set: range queries chart over time, instant ones are "now".</summary>
    public record NamedQuery(string Series, string PromQl, bool Instant = false);

    /// <summary>
    /// The queries behind a name, or null for an unknown name or missing id. <paramref name="window"/>
    /// is the rate window ("[2m]"), at least one scrape interval and no less than the step.
    /// </summary>
    public static IReadOnlyList<NamedQuery>? Build(string name, string? id, IReadOnlyList<string> ids, string window) => name switch
    {
        // Lets the UI tell "Prometheus down" from "no data yet".
        "status" => [new("up", "vector(1)", Instant: true)],
        "throughput" =>
        [
            new("enqueued", $"sum(rate(daprmq_queue_enqueued_total{{kind=\"queue\"}}[{window}]))"),
            new("dequeued", $"sum(rate(daprmq_queue_dequeued_total{{kind=\"queue\"}}[{window}]))"),
            new("deadlettered", $"sum(rate(daprmq_queue_deadlettered_total[{window}]))")
        ],
        "depth-summary" =>
        [
            new("total", $"sum by (state) ({FreshDepth("kind=\"queue\"")})", Instant: true),
            new("top", $"topk(10, sum by (queue) ({FreshDepth("kind=\"queue\"")}))", Instant: true),
            new("dlq", $"topk(20, sum by (queue) ({FreshDepth("kind=\"deadletter\"")}) > 0)", Instant: true)
        ],
        "queue" when id != null =>
        [
            .. new[] { "enqueued", "dequeued", "acknowledged", "nacked", "lock_expired", "deadlettered" }.Select(counter =>
                new NamedQuery(counter, $"sum(rate(daprmq_queue_{counter}_total{{{Queue(id)},kind=\"queue\"}}[{window}]))")),
            new("ready", $"sum({FreshDepth($"{Queue(id)},kind=\"queue\"", "ready")})"),
            new("locked", $"sum({FreshDepth($"{Queue(id)},kind=\"queue\"", "locked")})"),
            new("dlq", $"sum({FreshDepth($"{Queue(id)},kind=\"deadletter\"")})")
        ],
        "queue-list" when ids.Count > 0 =>
        [
            new("in", $"sum by (queue) (rate(daprmq_queue_enqueued_total{{{Queues(ids)},kind=\"queue\"}}[{window}]))", Instant: true),
            new("out", $"sum by (queue) (rate(daprmq_queue_dequeued_total{{{Queues(ids)},kind=\"queue\"}}[{window}]))", Instant: true),
            new("depth", $"sum by (queue) ({FreshDepth($"{Queues(ids)},kind=\"queue\"")})")
        ],
        "scaling" when id != null => Scaling(id),
        "topic" when id != null =>
        [
            new("published", $"sum(rate(daprmq_topic_published_total{{{Label("topic", id)}}}[{window}]))"),
            new("dispatched", $"sum by (subscriber) (rate(daprmq_topic_dispatched_total{{{Label("topic", id)}}}[{window}]))")
        ],
        _ => null
    };

    /// <summary>
    /// The KEDA overlay for a queue, per ScaledObject (label scaled_object). The operator exports which
    /// ScaledObjects scale on the queue (daprmq_scaler_binding); the replica counts come from
    /// kube-state-metrics, joined through the HPA KEDA creates:
    ///   desired / min / max   kube_horizontalpodautoscaler_* (desired replicas, spec bounds)
    ///   actual                the scale target's own replica count, via kube_horizontalpodautoscaler_info
    ///                         (an HPA's current_replicas never shows 0, though KEDA holds the target there)
    ///   jobs                  active Jobs owned by a ScaledJob of that name (ScaledJobs have no HPA)
    /// Without kube-state-metrics only binding and metric come back, and the UI estimates the target.
    /// </summary>
    private static IReadOnlyList<NamedQuery> Scaling(string queueId)
    {
        var binding = $"daprmq_scaler_binding{{{Queue(queueId)}}}";
        var bound = $"max by (scaled_object_namespace, scaled_object, hpa) ({binding})";
        var withNamespace = $"label_replace({bound}, \"namespace\", \"$1\", \"scaled_object_namespace\", \"(.*)\")";
        var hpas = $"label_replace({withNamespace}, \"horizontalpodautoscaler\", \"$1\", \"hpa\", \"(.*)\")";
        string Hpa(string metric) =>
            $"max by (scaled_object) ({metric} * on (namespace, horizontalpodautoscaler) group_left (scaled_object) {hpas})";
        string Target(string kind, string replicas, string label)
        {
            var targets = $"kube_horizontalpodautoscaler_info{{scaletargetref_kind=\"{kind}\"}} * on (namespace, horizontalpodautoscaler) group_left (scaled_object) {hpas}";
            var byName = $"max by (namespace, {label}, scaled_object) (label_replace({targets}, \"{label}\", \"$1\", \"scaletargetref_name\", \"(.*)\"))";
            return $"max by (scaled_object) ({replicas} * on (namespace, {label}) group_left (scaled_object) {byName})";
        }

        var jobOwners = $"max by (namespace, owner_name, scaled_object) (label_replace({withNamespace}, \"owner_name\", \"$1\", \"scaled_object\", \"(.*)\"))";
        var activeJobs = "sum by (namespace, owner_name) (kube_job_status_active * on (namespace, job_name) group_left (owner_name) kube_job_owner{owner_kind=\"ScaledJob\"})";

        return
        [
            new("binding", $"max by (scaled_object_namespace, scaled_object, mode, hpa, target_value, activation_value) ({binding})", Instant: true),
            new("metric", $"max by (scaled_object) (daprmq_scaler_metric_value{{{Queue(queueId)}}})"),
            new("desired", Hpa("kube_horizontalpodautoscaler_status_desired_replicas")),
            new("min", Hpa("kube_horizontalpodautoscaler_spec_min_replicas")),
            new("max", Hpa("kube_horizontalpodautoscaler_spec_max_replicas")),
            new("actual", $"{Target("Deployment", "kube_deployment_status_replicas", "deployment")} or {Target("StatefulSet", "kube_statefulset_status_replicas", "statefulset")}"),
            new("jobs", $"max by (scaled_object) ({activeJobs} * on (namespace, owner_name) group_left (scaled_object) {jobOwners})")
        ];
    }

    /// <summary>
    /// daprmq_queue_depth restricted to the freshest reporter of each queue. After an actor moves
    /// between workers the old worker keeps reporting its last value, so for each (queue, kind) only
    /// the instance with the newest depth_updated is kept.
    /// </summary>
    public static string FreshDepth(string matchers, string? state = null)
    {
        var updated = $"daprmq_queue_depth_updated{{{matchers}}}";
        var depth = state == null ? $"daprmq_queue_depth{{{matchers}}}" : $"daprmq_queue_depth{{{matchers},state=\"{state}\"}}";
        return $"({depth} and on (queue, kind, instance) ({updated} == on (queue, kind) group_left max by (queue, kind) ({updated})))";
    }

    private static string Queue(string id) => Label("queue", id);

    private static string Queues(IReadOnlyList<string> ids) =>
        $"queue=~\"{Escape(string.Join("|", ids.Select(Regex.Escape)))}\"";

    private static string Label(string name, string value) => $"{name}=\"{Escape(value)}\"";

    // PromQL string literal escaping.
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
