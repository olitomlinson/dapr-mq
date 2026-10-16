using DaprMQ.Interfaces;
using DaprMQ.Operator.Scaling;

namespace DaprMQ.Operator.Console;

public sealed class ScalingSamplerOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>History kept per KEDA trigger, in memory.</summary>
    public TimeSpan History { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// A registration KEDA hasn't asked about for this long isn't sampled. The scaler re-reports live
    /// ones every minute, so this is a few missed heartbeats.
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Kubernetes reads in flight at once.</summary>
    public int MaxKubernetesCalls { get; init; } = 4;
}

/// <summary>
/// The console's KEDA history. Every Interval it lists the ScaledObjects the scaler has reported,
/// reads their queues' depth the same way the scaler does (so it works on a console-only pod, which
/// never sees KEDA's calls), and - with Kubernetes read access - reads each ScaledObject and its HPA
/// for replica counts. History is in memory: per pod, and lost on restart. Nothing here is a metric
/// pipeline; it's a ring buffer for the console's charts.
/// </summary>
public sealed class ScalingSampler : BackgroundService
{
    private const int MaxDepthQueriesPerCall = 100;

    private sealed record Series(ScaledObjectRegistration Registration, KubernetesScalingStatus? Kubernetes, Queue<ScalingSample> Samples);

    private readonly IScalingRegistryClient _registry;
    private readonly IWorkerAdminClient _workers;
    private readonly IScalingStatusReader? _kubernetes;
    private readonly ScalingSamplerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScalingSampler> _logger;
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private Dictionary<string, Series> _series = new();

    public ScalingSampler(
        IScalingRegistryClient registry, IWorkerAdminClient workers, IScalingStatusReader? kubernetes,
        ScalingSamplerOptions options, TimeProvider timeProvider, ILogger<ScalingSampler> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _kubernetes = kubernetes;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = Math.Max(1, (int)(options.History / options.Interval));
    }

    public ConsoleScaling Snapshot(string? queueId)
    {
        lock (_lock)
        {
            return new ConsoleScaling
            {
                KubernetesRead = _kubernetes != null,
                SampleIntervalSeconds = (int)_options.Interval.TotalSeconds,
                HistorySeconds = (int)_options.History.TotalSeconds,
                Targets = _series.Values
                    .Where(s => queueId == null || s.Registration.QueueId == queueId)
                    .OrderBy(s => s.Registration.Key, StringComparer.Ordinal)
                    .Select(s => new ConsoleScalingTarget
                    {
                        Namespace = s.Registration.Namespace,
                        Name = s.Registration.Name,
                        QueueId = s.Registration.QueueId,
                        Mode = s.Registration.Mode,
                        TargetValue = s.Registration.TargetValue,
                        ActivationValue = s.Registration.ActivationValue,
                        IncludeDeadLetter = s.Registration.IncludeDeadLetter,
                        LastSeenAt = s.Registration.LastSeenAt.ToUnixTimeSeconds(),
                        Kubernetes = s.Kubernetes,
                        Samples = [.. s.Samples]
                    })
                    .ToList()
            };
        }
    }

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        List<ScaledObjectRegistration> registrations;
        try
        {
            registrations = (await _registry.ListAsync(cancellationToken))
                .Where(r => now - r.LastSeenAt <= _options.StaleAfter)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not list ScaledObjects from the scaling registry: {Error}", ex.Message);
            return;
        }

        var depthsTask = ReadDepthsAsync(registrations, cancellationToken);
        var kubernetesTask = ReadKubernetesAsync(registrations, cancellationToken);
        var depths = await depthsTask;
        var kubernetes = await kubernetesTask;

        var next = new Dictionary<string, Series>();
        lock (_lock)
        {
            for (var i = 0; i < registrations.Count; i++)
            {
                var registration = registrations[i];
                var status = kubernetes.GetValueOrDefault((registration.Namespace, registration.Name));
                var samples = _series.TryGetValue(registration.Key, out var existing) ? existing.Samples : new Queue<ScalingSample>();
                var depth = depths[i];
                double? metric = depth is { Error: null } ? ScalerMetadata.MetricValue(registration.Mode, depth) : null;

                samples.Enqueue(new ScalingSample
                {
                    T = now.ToUnixTimeSeconds(),
                    Metric = metric,
                    DesiredByMetric = metric is { } m ? DesiredByMetric(m, registration) : null,
                    CurrentReplicas = status?.CurrentReplicas,
                    DesiredReplicas = status?.DesiredReplicas,
                    ActiveJobs = status?.ActiveJobs
                });
                while (samples.Count > _capacity)
                {
                    samples.Dequeue();
                }

                next[registration.Key] = new Series(registration, status, samples);
            }

            _series = next;
        }
    }

    /// <summary>
    /// What the scaler's metric asks for: 0 while it's not active (KEDA scales to zero), otherwise the
    /// HPA's AverageValue formula. Min/max, HPA tolerance and stabilization aren't applied.
    /// </summary>
    private static int DesiredByMetric(double metric, ScaledObjectRegistration registration) =>
        metric <= registration.ActivationValue ? 0 : (int)Math.Ceiling(metric / registration.TargetValue);

    /// <summary>One result per registration, in order; null where the read failed.</summary>
    private async Task<QueueDepthResult?[]> ReadDepthsAsync(List<ScaledObjectRegistration> registrations, CancellationToken cancellationToken)
    {
        // The worker answers queries in order, so results line up with the registrations.
        var chunks = await Task.WhenAll(registrations.Chunk(MaxDepthQueriesPerCall).Select(async chunk =>
        {
            try
            {
                var response = await _workers.GetDepthsAsync(new QueueDepthRequest
                {
                    Queries = chunk.Select(r => new QueueDepthQuery { QueueId = r.QueueId, Mode = r.Mode, IncludeDeadLetter = r.IncludeDeadLetter }).ToList()
                }, cancellationToken);
                return response.Results.Count == chunk.Length ? response.Results.ToArray<QueueDepthResult?>() : new QueueDepthResult?[chunk.Length];
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Could not read queue depth for scaling samples: {Error}", ex.Message);
                return new QueueDepthResult?[chunk.Length];
            }
        }));
        return chunks.SelectMany(c => c).ToArray();
    }

    private async Task<Dictionary<(string Namespace, string Name), KubernetesScalingStatus>> ReadKubernetesAsync(
        List<ScaledObjectRegistration> registrations, CancellationToken cancellationToken)
    {
        if (_kubernetes == null)
        {
            return [];
        }

        // A ScaledObject with several DaprMQ triggers is one read.
        using var slots = new SemaphoreSlim(_options.MaxKubernetesCalls);
        var reads = await Task.WhenAll(registrations.Select(r => (r.Namespace, r.Name)).Distinct().Select(async key =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                return (key, await _kubernetes.ReadAsync(key.Namespace, key.Name, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return (key, new KubernetesScalingStatus { Error = ex.Message });
            }
            finally
            {
                slots.Release();
            }
        }));
        return reads.ToDictionary(r => r.key, r => r.Item2);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, _timeProvider);
        try
        {
            do
            {
                await SampleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
