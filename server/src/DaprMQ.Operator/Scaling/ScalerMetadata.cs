using System.Globalization;
using System.Text.RegularExpressions;
using DaprMQ.Interfaces;
using Grpc.Core;

namespace DaprMQ.Operator.Scaling;

/// <summary>
/// A ScaledObject trigger's `metadata` block, validated. Invalid metadata is an InvalidArgument RPC
/// error, which KEDA surfaces on the ScaledObject's status.
/// </summary>
public sealed partial record ScalerMetadata
{
    public required string QueueId { get; init; }
    public QueueDepthMode Mode { get; init; } = QueueDepthMode.Messages;

    /// <summary>Metric value per replica the HPA aims for.</summary>
    public double TargetValue { get; init; } = 10;

    /// <summary>The scale-from/to-zero threshold: active while the metric is above this.</summary>
    public double ActivationValue { get; init; }

    /// <summary>Messages mode: also count {queueId}-deadletter.</summary>
    public bool IncludeDeadLetter { get; init; }

    /// <summary>
    /// Stable per (mode, queue) so GetMetricSpec and GetMetrics agree; lowercased with anything outside
    /// [a-z0-9-] replaced, since it becomes part of an external-metric name.
    /// </summary>
    public string MetricName =>
        $"daprmq-{Mode.ToString().ToLowerInvariant()}-{UnsafeMetricChars().Replace(QueueId.ToLowerInvariant(), "-")}";

    public QueueDepthQuery ToQuery() => new() { QueueId = QueueId, Mode = Mode, IncludeDeadLetter = IncludeDeadLetter };

    public static ScalerMetadata Parse(IDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue("queueId", out var queueId) || string.IsNullOrWhiteSpace(queueId))
        {
            throw Invalid("queueId is required");
        }

        var result = new ScalerMetadata
        {
            QueueId = queueId,
            Mode = metadata.TryGetValue("mode", out var mode) ? ParseMode(mode) : QueueDepthMode.Messages,
            TargetValue = ParseDouble(metadata, "targetValue", 10),
            ActivationValue = ParseDouble(metadata, "activationValue", 0),
            IncludeDeadLetter = ParseBool(metadata, "includeDeadLetter", false)
        };

        if (result.TargetValue <= 0)
        {
            throw Invalid("targetValue must be greater than 0");
        }

        if (result.ActivationValue < 0)
        {
            throw Invalid("activationValue must not be negative");
        }

        if (result.Mode == QueueDepthMode.Sessions && result.IncludeDeadLetter)
        {
            throw Invalid("includeDeadLetter is only supported in messages mode");
        }

        return result;
    }

    private static QueueDepthMode ParseMode(string value) =>
        Enum.TryParse<QueueDepthMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode) && !char.IsDigit(value.Trim()[0])
            ? mode
            : throw Invalid($"mode must be 'messages' or 'sessions', got '{value}'");

    private static double ParseDouble(IDictionary<string, string> metadata, string key, double fallback)
    {
        if (!metadata.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed
            : throw Invalid($"{key} must be a number, got '{value}'");
    }

    private static bool ParseBool(IDictionary<string, string> metadata, string key, bool fallback)
    {
        if (!metadata.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return bool.TryParse(value, out var parsed) ? parsed : throw Invalid($"{key} must be true or false, got '{value}'");
    }

    private static RpcException Invalid(string message) => new(new Status(StatusCode.InvalidArgument, message));

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex UnsafeMetricChars();
}
