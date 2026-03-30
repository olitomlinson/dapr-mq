using Dapr.Actors;
using Dapr.Actors.Client;

namespace DaprMQ.Interfaces;

public class DaprPubSubSinkActorInvoker : IDaprPubSubSinkActorInvoker
{
    private readonly IActorProxyFactory _actorProxyFactory;
    private readonly string _actorType;

    public DaprPubSubSinkActorInvoker(IActorProxyFactory actorProxyFactory, string actorType)
    {
        _actorProxyFactory = actorProxyFactory;
        _actorType = actorType;
    }

    /// <inheritdoc />
    public async Task<TResponse> InvokeMethodAsync<TResponse>(
        ActorId actorId,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        var proxy = _actorProxyFactory.Create(actorId, _actorType);
        return await proxy.InvokeMethodAsync<TResponse>(methodName, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TResponse> InvokeMethodAsync<TRequest, TResponse>(
        ActorId actorId,
        string methodName,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var proxy = _actorProxyFactory.Create(actorId, _actorType);
        return await proxy.InvokeMethodAsync<TRequest, TResponse>(methodName, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task InvokeMethodAsync<TRequest>(
        ActorId actorId,
        string methodName,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var proxy = _actorProxyFactory.Create(actorId, _actorType);
        await proxy.InvokeMethodAsync(methodName, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task InvokeMethodAsync(
        ActorId actorId,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        var proxy = _actorProxyFactory.Create(actorId, _actorType);
        await proxy.InvokeMethodAsync(methodName, cancellationToken);
    }
}
