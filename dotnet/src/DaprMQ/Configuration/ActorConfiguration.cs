namespace DaprMQ.Configuration;

/// <summary>
/// Configuration for Dapr actor settings
/// </summary>
public class ActorConfiguration
{
    /// <summary>
    /// The actor type name used for registration and proxy creation
    /// Default: "QueueActor"
    /// Environment variable: ACTOR_TYPE_NAME
    /// </summary>
    public string ActorTypeName { get; set; } = "QueueActor";
}
