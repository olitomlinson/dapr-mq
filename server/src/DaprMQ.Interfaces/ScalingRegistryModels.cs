using System.Text.Json.Serialization;
using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// A KEDA ScaledObject (or ScaledJob) trigger that uses DaprMQ's external scaler, as the scaler last
/// saw it in one of KEDA's calls. KEDA only tells a scaler the object's name, namespace and trigger
/// metadata - never its replica counts.
/// </summary>
public record ScaledObjectRegistration
{
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required string QueueId { get; init; }
    public QueueDepthMode Mode { get; init; }
    public double TargetValue { get; init; }
    public double ActivationValue { get; init; }
    public bool IncludeDeadLetter { get; init; }

    /// <summary>When KEDA last called the scaler for this trigger.</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>One entry per trigger: a ScaledObject can scale on several queues.</summary>
    [JsonIgnore]
    public string Key => $"{Namespace}/{Name}/{Mode}/{QueueId}";
}

/// <summary>Request for ScalingRegistryActor.RegisterAsync.</summary>
public record RegisterScaledObjectsRequest
{
    public List<ScaledObjectRegistration> Registrations { get; init; } = [];
}

/// <summary>The worker's read of the registry (GET /internal/admin/scaling), sorted by key.</summary>
public record ScaledObjectRegistrations
{
    public List<ScaledObjectRegistration> Registrations { get; init; } = [];
}

/// <summary>
/// The registry of ScaledObjects scaling on DaprMQ queues: one actor, written (through a worker
/// endpoint) by every operator pod's scaler, since each sees only the ScaledObjects KEDA asks it
/// about. Reads go through the actor-state API and never call or activate it.
/// </summary>
public interface IScalingRegistryActor : IActor
{
    /// <summary>Upserts each registration unless a newer one is held, then drops expired ones; one save.</summary>
    Task RegisterAsync(RegisterScaledObjectsRequest request);
}

/// <summary>Dedicated invoker for ScalingRegistryActor, used by the worker's registration endpoint.</summary>
public interface IScalingRegistryActorInvoker : IActorInvoker
{
}

public static class ScalingRegistry
{
    public const string DefaultActorType = "ScalingRegistryActor";
    public const string ActorId = "keda";

    /// <summary>A registration KEDA hasn't asked about for this long (its ScaledObject was deleted) is dropped.</summary>
    public static readonly TimeSpan RetainFor = TimeSpan.FromHours(1);
}
