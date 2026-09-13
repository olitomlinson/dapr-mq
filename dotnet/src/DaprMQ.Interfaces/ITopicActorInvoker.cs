namespace DaprMQ.Interfaces;

/// <summary>
/// Dedicated actor invoker interface for TopicActor operations.
/// This enables separate DI registration from the QueueActor invoker.
/// </summary>
public interface ITopicActorInvoker : IActorInvoker
{
}
