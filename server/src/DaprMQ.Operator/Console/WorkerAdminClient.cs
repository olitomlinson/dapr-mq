using Dapr.Client;
using DaprMQ.Interfaces;

namespace DaprMQ.Operator.Console;

/// <summary>A worker's reply, passed through to the console unchanged.</summary>
public record WorkerReply(int StatusCode, string Body);

/// <summary>
/// Reads the workers' internal admin endpoints. Like queue depth, the operator can't read actor
/// state itself (Dapr keys it by the caller's app-id), so it asks a worker.
/// </summary>
public interface IWorkerAdminClient
{
    /// <summary>GET internal/admin/... on a worker. Throws on a transport failure.</summary>
    Task<WorkerReply> GetAsync(string path, IReadOnlyCollection<KeyValuePair<string, string>> query, CancellationToken cancellationToken);

    Task<QueueDepthResponse> GetDepthsAsync(QueueDepthRequest request, CancellationToken cancellationToken);
}

/// <summary>Calls the workers via Dapr service invocation, as DaprWorkerDepthClient does.</summary>
public sealed class DaprWorkerAdminClient : IWorkerAdminClient
{
    private readonly DaprClient _daprClient;
    private readonly string _workerAppId;

    public DaprWorkerAdminClient(DaprClient daprClient, string workerAppId)
    {
        _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
        _workerAppId = workerAppId ?? throw new ArgumentNullException(nameof(workerAppId));
    }

    public async Task<WorkerReply> GetAsync(string path, IReadOnlyCollection<KeyValuePair<string, string>> query, CancellationToken cancellationToken)
    {
        using var request = _daprClient.CreateInvokeMethodRequest(HttpMethod.Get, _workerAppId, path, query);
        using var response = await _daprClient.InvokeMethodWithResponseAsync(request, cancellationToken);
        return new WorkerReply((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public Task<QueueDepthResponse> GetDepthsAsync(QueueDepthRequest request, CancellationToken cancellationToken) =>
        _daprClient.InvokeMethodAsync<QueueDepthRequest, QueueDepthResponse>(
            HttpMethod.Post, _workerAppId, "internal/queue-depth", request, cancellationToken);
}

/// <summary>
/// Caps the worker calls the console has in flight, so console load on the workers (and their
/// sidecars' Postgres pool) stays small and predictable however many people are browsing.
/// </summary>
public sealed class ConcurrencyLimitedWorkerAdminClient : IWorkerAdminClient
{
    private readonly IWorkerAdminClient _inner;
    private readonly SemaphoreSlim _slots;

    public ConcurrencyLimitedWorkerAdminClient(IWorkerAdminClient inner, int maxConcurrentCalls)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentCalls, 1);
        _slots = new SemaphoreSlim(maxConcurrentCalls, maxConcurrentCalls);
    }

    public Task<WorkerReply> GetAsync(string path, IReadOnlyCollection<KeyValuePair<string, string>> query, CancellationToken cancellationToken) =>
        RunAsync(() => _inner.GetAsync(path, query, cancellationToken), cancellationToken);

    public Task<QueueDepthResponse> GetDepthsAsync(QueueDepthRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => _inner.GetDepthsAsync(request, cancellationToken), cancellationToken);

    private async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            return await call();
        }
        finally
        {
            _slots.Release();
        }
    }
}
