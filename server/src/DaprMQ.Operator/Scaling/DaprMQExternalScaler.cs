using DaprMQ.Interfaces;
using Externalscaler;
using Grpc.Core;

namespace DaprMQ.Operator.Scaling;

public sealed class ScalerOptions
{
    /// <summary>How often StreamIsActive re-reads depth to detect an activation change.</summary>
    public TimeSpan StreamPollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// KEDA external scaler (type: external / external-push) that scales consumer workloads on a DaprMQ
/// queue's depth. Trigger metadata: see ScalerMetadata.
///
/// The metric is ready + locked items in messages mode, or the number of non-empty sessions in
/// sessions mode. A failed depth read is Unavailable - never 0, which would scale consumers to zero
/// during a DaprMQ outage; KEDA's ScaledObject `fallback` decides what happens instead.
/// </summary>
public sealed class DaprMQExternalScaler : ExternalScaler.ExternalScalerBase
{
    private readonly IWorkerDepthClient _depthClient;
    private readonly ScalerOptions _options;
    private readonly ILogger<DaprMQExternalScaler> _logger;

    public DaprMQExternalScaler(IWorkerDepthClient depthClient, ScalerOptions options, ILogger<DaprMQExternalScaler> logger)
    {
        _depthClient = depthClient ?? throw new ArgumentNullException(nameof(depthClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<IsActiveResponse> IsActive(ScaledObjectRef request, ServerCallContext context)
    {
        var metadata = ScalerMetadata.Parse(request.ScalerMetadata);
        return new IsActiveResponse { Result = await GetMetricValueAsync(metadata, context.CancellationToken) > metadata.ActivationValue };
    }

    public override async Task StreamIsActive(ScaledObjectRef request, IServerStreamWriter<IsActiveResponse> responseStream, ServerCallContext context)
    {
        var metadata = ScalerMetadata.Parse(request.ScalerMetadata);
        var cancellationToken = context.CancellationToken;
        bool? lastReported = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var active = await GetMetricValueAsync(metadata, cancellationToken) > metadata.ActivationValue;
                    if (active != lastReported)
                    {
                        await responseStream.WriteAsync(new IsActiveResponse { Result = active });
                        lastReported = active;
                    }
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
                {
                    // Keep the stream: KEDA's regular polling still sees the failure via GetMetrics.
                    _logger.LogWarning("Depth read for queue {QueueId} failed: {Error}", metadata.QueueId, ex.Status.Detail);
                }

                await Task.Delay(_options.StreamPollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // KEDA closed the stream (ScaledObject deleted/updated, or KEDA restarting).
        }
    }

    public override Task<GetMetricSpecResponse> GetMetricSpec(ScaledObjectRef request, ServerCallContext context)
    {
        var metadata = ScalerMetadata.Parse(request.ScalerMetadata);
        return Task.FromResult(new GetMetricSpecResponse
        {
            MetricSpecs =
            {
                new MetricSpec
                {
                    MetricName = metadata.MetricName,
                    TargetSize = (long)Math.Ceiling(metadata.TargetValue),
                    TargetSizeFloat = metadata.TargetValue
                }
            }
        });
    }

    public override async Task<GetMetricsResponse> GetMetrics(GetMetricsRequest request, ServerCallContext context)
    {
        var metadata = ScalerMetadata.Parse(request.ScaledObjectRef.ScalerMetadata);
        var value = await GetMetricValueAsync(metadata, context.CancellationToken);
        return new GetMetricsResponse
        {
            MetricValues =
            {
                new MetricValue
                {
                    // KEDA asks for the name it was given (with its own s{n}- prefix); echo it back.
                    MetricName = string.IsNullOrEmpty(request.MetricName) ? metadata.MetricName : request.MetricName,
                    MetricValue_ = (long)value,
                    MetricValueFloat = value
                }
            }
        };
    }

    private async Task<double> GetMetricValueAsync(ScalerMetadata metadata, CancellationToken cancellationToken)
    {
        QueueDepthResult result;
        try
        {
            result = await _depthClient.GetDepthAsync(metadata.ToQuery(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RpcException)
        {
            throw Unavailable(metadata, ex.Message);
        }

        if (result.Error != null)
        {
            throw Unavailable(metadata, result.Error);
        }

        // Locked items always count: they're out of the queue's Count, and lock expiry is only swept by
        // the next dequeue - scaling consumers to zero over a fully locked queue would strand them.
        return metadata.Mode == QueueDepthMode.Sessions
            ? result.NonEmptySessions
            : result.Ready + result.Locked;
    }

    private static RpcException Unavailable(ScalerMetadata metadata, string detail) =>
        new(new Status(StatusCode.Unavailable, $"Could not read depth of queue '{metadata.QueueId}': {detail}"));
}
