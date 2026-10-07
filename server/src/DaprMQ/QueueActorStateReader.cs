using System.Text.Json;
using Dapr.Actors;

namespace DaprMQ;

/// <summary>
/// A session actor's liveness as read straight from its persisted state. Reported as the underlying
/// facts rather than a single bool so the coordinator's decision is auditable, and so a future
/// policy change (say, acting on a lapsed lease) needs no second shape change here.
/// </summary>
public readonly record struct SessionActorState(int ItemCount, int LockCount, double? LeaseExpiresAt)
{
    /// <summary>
    /// A session is only finished once it holds neither queued nor locked items. Locked items count:
    /// under session-scoped locking they outlive individual messages, so ignoring them would let the
    /// directory sweep evict a session that still owns work.
    /// </summary>
    public bool IsEmpty => ItemCount == 0 && LockCount == 0;
}

/// <summary>
/// A queue's depth as persisted: <paramref name="Ready"/> items waiting in its segments (locked items
/// are removed from QueueMetadata.Count while locked) and <paramref name="Locked"/> items in flight.
/// </summary>
public readonly record struct QueueDepth(long Ready, long Locked);

/// <summary>
/// Reads a QueueActor's persisted state directly from the state store via Dapr's actor-state
/// data-plane API, bypassing actor placement/activation entirely - used by
/// SessionCoordinatorActor's directory sweep so checking a candidate for liveness never risks
/// activating it if it's currently cold, and by the queue-depth read the KEDA scaler relies on.
///
/// Dapr keys actor state by the *calling* sidecar's app-id ({appId}||{type}||{id}||{key}), so this
/// only sees real data when run on an actor-hosting (worker) instance. From any other app-id every
/// read silently comes back empty. Any worker can read any actor - there is no "hosted here" check.
/// </summary>
public interface IQueueActorStateReader
{
    /// <summary>
    /// Reads the session actor at <paramref name="actorId"/>. Throws on a failed or inconclusive read
    /// (network/HTTP error, or a missing metadata key) rather than guessing - callers must treat that
    /// as "unknown", never as "empty".
    /// </summary>
    Task<SessionActorState> ReadSessionStateAsync(ActorId actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the queue actor at <paramref name="actorId"/>. A missing metadata key is a queue nothing
    /// has been enqueued to, so it reads as zero; a failed request throws.
    /// </summary>
    Task<QueueDepth> ReadQueueDepthAsync(ActorId actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the session ids known to <paramref name="queueId"/>'s SessionCoordinatorActor. A missing
    /// metadata key means no sessions; a failed request throws.
    /// </summary>
    Task<IReadOnlyCollection<string>> ReadSessionDirectoryAsync(string queueId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IQueueActorStateReader" />
public class QueueActorStateReader : IQueueActorStateReader
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _actorType;
    private readonly string _daprHttpEndpoint;
    private readonly string _sessionCoordinatorActorType;

    public QueueActorStateReader(IHttpClientFactory httpClientFactory, string actorType, string daprHttpEndpoint,
        string sessionCoordinatorActorType = "SessionCoordinatorActor")
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _actorType = actorType ?? throw new ArgumentNullException(nameof(actorType));
        _daprHttpEndpoint = (daprHttpEndpoint ?? throw new ArgumentNullException(nameof(daprHttpEndpoint))).TrimEnd('/');
        _sessionCoordinatorActorType = sessionCoordinatorActorType ?? throw new ArgumentNullException(nameof(sessionCoordinatorActorType));
    }

    /// <inheritdoc />
    public async Task<SessionActorState> ReadSessionStateAsync(ActorId actorId, CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync<ActorMetadata>(_actorType, actorId.GetId(), cancellationToken)
            ?? throw new InvalidOperationException(
                $"No metadata state found for actor '{actorId.GetId()}' - inconclusive read, cannot determine emptiness");

        return new SessionActorState(
            metadata.Queues.Values.Sum(q => q.Count),
            metadata.LockCount,
            metadata.ActiveSessionLeaseExpiresAt);
    }

    /// <inheritdoc />
    public async Task<QueueDepth> ReadQueueDepthAsync(ActorId actorId, CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync<ActorMetadata>(_actorType, actorId.GetId(), cancellationToken);
        return metadata == null
            ? new QueueDepth(0, 0)
            : new QueueDepth(metadata.Queues.Values.Sum(q => (long)q.Count), metadata.LockCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> ReadSessionDirectoryAsync(string queueId, CancellationToken cancellationToken = default)
    {
        var metadata = await ReadMetadataAsync<SessionCoordinatorMetadata>(_sessionCoordinatorActorType, queueId, cancellationToken);
        return metadata?.SessionDirectory.Keys.ToArray() ?? [];
    }

    /// <summary>
    /// GETs an actor's "metadata" key. Returns null when the key doesn't exist (empty body); throws on
    /// a non-success status or an undeserializable body.
    /// </summary>
    private async Task<T?> ReadMetadataAsync<T>(string actorType, string actorId, CancellationToken cancellationToken) where T : class
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{_daprHttpEndpoint}/v1.0/actors/{actorType}/{actorId}/state/metadata";

        var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to read metadata state for actor '{actorId}': HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(body, DeserializeOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize metadata state for actor '{actorId}'");
    }
}
