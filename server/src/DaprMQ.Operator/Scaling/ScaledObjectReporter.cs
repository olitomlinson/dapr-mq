using DaprMQ.Interfaces;
using Externalscaler;

namespace DaprMQ.Operator.Scaling;

/// <summary>Told about every KEDA call the scaler serves.</summary>
public interface IScaledObjectObserver
{
    void Observe(ScaledObjectRef scaledObject, ScalerMetadata metadata);
}

public sealed class ScaledObjectReporterOptions
{
    /// <summary>How often new and changed ScaledObjects are sent to the registry.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often a ScaledObject KEDA is still asking about is re-sent, keeping it registered.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Reports the ScaledObjects KEDA asks this pod's scaler about to the ScalingRegistryActor, off the
/// scaler's path: Observe only records the call, and a background flush sends the new or changed
/// ones, plus a heartbeat for the rest, in one actor call. One KEDA ScaledObject polled every second
/// costs the registry a write a minute. One that KEDA stops asking about (deleted) stops being sent,
/// and the registry lets it expire.
/// </summary>
public sealed class ScaledObjectReporter : BackgroundService, IScaledObjectObserver
{
    private sealed class Entry
    {
        public required ScaledObjectRegistration Trigger { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public ScaledObjectRegistration? SentTrigger { get; set; }
        public DateTimeOffset SentAt { get; set; }
    }

    private readonly IScalingRegistryClient _registry;
    private readonly ScaledObjectReporterOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScaledObjectReporter> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    public ScaledObjectReporter(IScalingRegistryClient registry, ScaledObjectReporterOptions options, TimeProvider timeProvider, ILogger<ScaledObjectReporter> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Observe(ScaledObjectRef scaledObject, ScalerMetadata metadata)
    {
        // LastSeenAt stays default here so triggers compare by configuration; it's stamped on send.
        var trigger = new ScaledObjectRegistration
        {
            Namespace = scaledObject.Namespace,
            Name = scaledObject.Name,
            QueueId = metadata.QueueId,
            Mode = metadata.Mode,
            TargetValue = metadata.TargetValue,
            ActivationValue = metadata.ActivationValue,
            IncludeDeadLetter = metadata.IncludeDeadLetter
        };
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            if (_entries.TryGetValue(trigger.Key, out var entry))
            {
                entry.Trigger = trigger;
                entry.ObservedAt = now;
            }
            else
            {
                _entries[trigger.Key] = new Entry { Trigger = trigger, ObservedAt = now };
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        List<(Entry Entry, ScaledObjectRegistration Trigger, ScaledObjectRegistration Sent)> due;
        lock (_lock)
        {
            foreach (var key in _entries.Where(e => now - e.Value.ObservedAt > ScalingRegistry.RetainFor).Select(e => e.Key).ToList())
            {
                _entries.Remove(key);
            }

            due = _entries.Values
                .Where(e => e.SentTrigger != e.Trigger || (e.ObservedAt > e.SentAt && now - e.SentAt >= _options.Heartbeat))
                .Select(e => (e, e.Trigger, e.Trigger with { LastSeenAt = e.ObservedAt }))
                .ToList();
        }

        if (due.Count == 0)
        {
            return;
        }

        try
        {
            await _registry.RegisterAsync(due.Select(d => d.Sent).ToList(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Left due, so the next flush retries.
            _logger.LogWarning("Could not report {Count} ScaledObjects to the scaling registry: {Error}", due.Count, ex.Message);
            return;
        }

        lock (_lock)
        {
            foreach (var (entry, trigger, _) in due)
            {
                entry.SentTrigger = trigger;
                entry.SentAt = now;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.FlushInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
