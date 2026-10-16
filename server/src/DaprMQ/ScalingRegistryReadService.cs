using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>Lists the scaling registry behind the worker's GET /internal/admin/scaling.</summary>
public interface IScalingRegistryReadService
{
    Task<ScaledObjectRegistrations> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads ScalingRegistryActor's registrations key through the actor-state API - never calling or
/// activating the actor, as the console's sampler reads it every few seconds - and drops the
/// registrations past retention that the actor only prunes on its next write.
/// </summary>
public class ScalingRegistryReadService : IScalingRegistryReadService
{
    private readonly IQueueActorStateReader _reader;
    private readonly string _actorType;
    private readonly TimeProvider _timeProvider;

    public ScalingRegistryReadService(IQueueActorStateReader reader, string actorType, TimeProvider timeProvider)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _actorType = actorType ?? throw new ArgumentNullException(nameof(actorType));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<ScaledObjectRegistrations> ListAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _reader.ReadStateAsync<Dictionary<string, ScaledObjectRegistration>>(
            _actorType, ScalingRegistry.ActorId, ScalingRegistryActor.RegistrationsKey, cancellationToken);
        var expiredBefore = _timeProvider.GetUtcNow() - ScalingRegistry.RetainFor;
        return new ScaledObjectRegistrations
        {
            Registrations = (stored?.Values ?? Enumerable.Empty<ScaledObjectRegistration>())
                .Where(r => r.LastSeenAt >= expiredBefore)
                .OrderBy(r => r.Key, StringComparer.Ordinal)
                .ToList()
        };
    }
}
