using Dapr.Actors.Runtime;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// The ScaledObjects scaling on DaprMQ queues, as the operator's KEDA scaler has seen them (one
/// actor, id "keda"). Each operator pod only sees the ScaledObjects KEDA calls it about, so they all
/// report here through a worker endpoint, about once a minute. The admin console reads the
/// registrations key through the actor-state API (ScalingRegistryReadService), never activating it.
/// A single key: there's one entry per KEDA trigger, so it stays small.
/// </summary>
public class ScalingRegistryActor : Actor, IScalingRegistryActor
{
    public const string RegistrationsKey = "registrations";

    private readonly TimeProvider _timeProvider;
    private Dictionary<string, ScaledObjectRegistration> _registrations = new();

    public ScalingRegistryActor(ActorHost host, TimeProvider timeProvider) : base(host)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override async Task OnActivateAsync()
    {
        var registrations = await StateManager.TryGetStateAsync<Dictionary<string, ScaledObjectRegistration>>(RegistrationsKey);
        _registrations = registrations.HasValue ? registrations.Value : new Dictionary<string, ScaledObjectRegistration>();
    }

    /// <inheritdoc />
    public async Task RegisterAsync(RegisterScaledObjectsRequest request)
    {
        var expiredBefore = _timeProvider.GetUtcNow() - ScalingRegistry.RetainFor;
        var next = _registrations
            .Where(entry => entry.Value.LastSeenAt >= expiredBefore)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var registration in request.Registrations)
        {
            if (!next.TryGetValue(registration.Key, out var held) || registration.LastSeenAt >= held.LastSeenAt)
            {
                next[registration.Key] = registration;
            }
        }

        // Swap in only after the save, as CatalogActor does, so memory matches what's stored.
        await StateManager.SetStateAsync(RegistrationsKey, next);
        await StateManager.SaveStateAsync();
        _registrations = next;
    }
}
