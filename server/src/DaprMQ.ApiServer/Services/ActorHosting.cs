namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Which actor types an actor-hosting instance registers (ACTOR_SET). All is a single deployment.
/// Data and Control split the session coordinators, catalog and scaling registry onto their own
/// deployment and Dapr app-id, so worker scaling never moves them and a busy worker never delays them.
/// </summary>
public enum ActorSet
{
    All,
    Data,
    Control
}

/// <summary>
/// The actor types this instance hosts, and which other app-id owns the rest. Dapr keys actor state
/// by app-id, so state of a type hosted elsewhere is read through its owner (see QueueActorStateReader).
/// </summary>
public sealed class ActorHosting
{
    private readonly IReadOnlyCollection<string> _dataTypes;
    private readonly IReadOnlyCollection<string> _controlTypes;

    /// <param name="dataTypes">The data-plane types, the first being the one readiness requires.</param>
    /// <param name="controlTypes">The control-plane types, the first being the one readiness requires.</param>
    public ActorHosting(ActorSet set, IReadOnlyCollection<string> dataTypes, IReadOnlyCollection<string> controlTypes)
    {
        Set = set;
        _dataTypes = dataTypes ?? throw new ArgumentNullException(nameof(dataTypes));
        _controlTypes = controlTypes ?? throw new ArgumentNullException(nameof(controlTypes));
    }

    public ActorSet Set { get; }

    /// <summary>The type an instance must host to be ready.</summary>
    public string ReadinessActorType => Set == ActorSet.Control ? _controlTypes.First() : _dataTypes.First();

    public static ActorSet ParseSet(string? value) =>
        string.IsNullOrWhiteSpace(value) ? ActorSet.All
        : Enum.TryParse<ActorSet>(value, ignoreCase: true, out var set) && Enum.IsDefined(set) ? set
        : throw new InvalidOperationException($"ACTOR_SET must be all, data or control, not '{value}'");

    public bool Hosts(string actorType) => Set switch
    {
        ActorSet.Data => _dataTypes.Contains(actorType),
        ActorSet.Control => _controlTypes.Contains(actorType),
        _ => true
    };

    /// <summary>The app-id to read each type not hosted here from.</summary>
    public IReadOnlyDictionary<string, string> RemoteAppIds(string? dataAppId, string? controlAppId) => Set switch
    {
        ActorSet.Data => _controlTypes.ToDictionary(t => t, _ => Required(controlAppId, "CONTROL_PLANE_APP_ID", "data")),
        ActorSet.Control => _dataTypes.ToDictionary(t => t, _ => Required(dataAppId, "DATA_PLANE_APP_ID", "control")),
        _ => new Dictionary<string, string>()
    };

    private static string Required(string? appId, string name, string set) =>
        string.IsNullOrWhiteSpace(appId)
            ? throw new InvalidOperationException($"{name} is required when ACTOR_SET={set}: the Dapr app-id hosting the other actor types")
            : appId;
}
