namespace DaprMQ.Interfaces;

/// <summary>
/// Dedicated actor invoker interface for HttpSinkActor operations.
/// This enables separate DI registration from the main QueueActor invoker.
/// </summary>
public interface IHttpSinkActorInvoker : IActorInvoker
{
}
