namespace DaprMQ.Interfaces;

/// <summary>
/// Dedicated actor invoker interface for SessionCoordinatorActor operations.
/// This enables separate DI registration from the QueueActor invoker.
/// </summary>
public interface ISessionCoordinatorActorInvoker : IActorInvoker
{
}
