using System.Collections.Concurrent;
using Dapr.Client;
using DaprMQ.Interfaces;

namespace DaprMQ.Operator.Scaling;

/// <summary>
/// Reads queue depth from the DaprMQ workers. The operator can't read actor state itself: Dapr keys
/// it by the calling sidecar's app-id, so from the operator's own app-id every read comes back empty.
/// </summary>
public interface IWorkerDepthClient
{
    Task<QueueDepthResult> GetDepthAsync(QueueDepthQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Calls the workers' internal POST /internal/queue-depth via Dapr service invocation (mTLS and
/// access-control policy come with it). Any worker can answer for any queue.
/// </summary>
public sealed class DaprWorkerDepthClient : IWorkerDepthClient
{
    private const string MethodName = "internal/queue-depth";

    private readonly DaprClient _daprClient;
    private readonly string _workerAppId;

    public DaprWorkerDepthClient(DaprClient daprClient, string workerAppId)
    {
        _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
        _workerAppId = workerAppId ?? throw new ArgumentNullException(nameof(workerAppId));
    }

    public async Task<QueueDepthResult> GetDepthAsync(QueueDepthQuery query, CancellationToken cancellationToken)
    {
        var response = await _daprClient.InvokeMethodAsync<QueueDepthRequest, QueueDepthResponse>(
            HttpMethod.Post, _workerAppId, MethodName, new QueueDepthRequest { Queries = [query] }, cancellationToken);

        return response.Results.SingleOrDefault()
            ?? throw new InvalidOperationException($"Worker returned no result for queue '{query.QueueId}'");
    }
}

/// <summary>
/// Collapses the several calls KEDA makes per polling cycle (IsActive, GetMetrics, and a
/// StreamIsActive loop per ScaledObject) into one worker round-trip per query per TTL. Concurrent
/// callers share the in-flight read. Failures - thrown or reported - are never cached.
/// </summary>
public sealed class CachingWorkerDepthClient : IWorkerDepthClient
{
    private readonly IWorkerDepthClient _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<QueueDepthQuery, (DateTimeOffset ExpiresAt, Task<QueueDepthResult> Read)> _cache = new();

    public CachingWorkerDepthClient(IWorkerDepthClient inner, TimeSpan ttl, TimeProvider timeProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<QueueDepthResult> GetDepthAsync(QueueDepthQuery query, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!_cache.TryGetValue(query, out var entry) || entry.ExpiresAt <= now || entry.Read.IsFaulted || entry.Read.IsCanceled)
        {
            // The shared read must not be cancelled by whichever caller happened to start it.
            entry = (now + _ttl, _inner.GetDepthAsync(query, CancellationToken.None));
            _cache[query] = entry;
        }

        try
        {
            var result = await entry.Read.WaitAsync(cancellationToken);
            if (result.Error != null)
            {
                _cache.TryRemove(KeyValuePair.Create(query, entry));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _cache.TryRemove(KeyValuePair.Create(query, entry));
            throw;
        }
    }
}
