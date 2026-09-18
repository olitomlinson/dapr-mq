using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>
/// Abstraction for invoking actor methods.
/// This interface wraps Dapr's ActorProxy.InvokeMethodAsync to enable unit testing.
/// </summary>
public interface IActorInvoker
{
    /// <summary>
    /// Invokes an actor method with no request data and returns a response.
    /// </summary>
    Task<TResponse> InvokeMethodAsync<TResponse>(ActorId actorId, string methodName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes an actor method with request data and returns a response.
    /// </summary>
    Task<TResponse> InvokeMethodAsync<TRequest, TResponse>(ActorId actorId, string methodName, TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes an actor method with request data and no response.
    /// </summary>
    Task InvokeMethodAsync<TRequest>(ActorId actorId, string methodName, TRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes an actor method with no request data and no response.
    /// </summary>
    Task InvokeMethodAsync(ActorId actorId, string methodName, CancellationToken cancellationToken = default);
}
