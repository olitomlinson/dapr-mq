using System.Text.Json;
using Dapr.Actors;

namespace DaprMQ;

/// <summary>
/// Reads a QueueActor's persisted state directly from the state store via Dapr's actor-state
/// data-plane API, bypassing actor placement/activation entirely - used by
/// SessionCoordinatorActor's directory sweep so checking a candidate for liveness never risks
/// activating it if it's currently cold.
/// </summary>
public interface IQueueActorStateReader
{
    /// <summary>
    /// True if the session actor at <paramref name="actorId"/> currently has zero items across
    /// all its priority queues. Throws on a failed or inconclusive read (network/HTTP error, or a
    /// missing metadata key) rather than guessing - callers must treat that as "unknown", never as
    /// "empty".
    /// </summary>
    Task<bool> IsSessionEmptyAsync(ActorId actorId, CancellationToken cancellationToken = default);
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

    public QueueActorStateReader(IHttpClientFactory httpClientFactory, string actorType, string daprHttpEndpoint)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _actorType = actorType ?? throw new ArgumentNullException(nameof(actorType));
        _daprHttpEndpoint = (daprHttpEndpoint ?? throw new ArgumentNullException(nameof(daprHttpEndpoint))).TrimEnd('/');
    }

    /// <inheritdoc />
    public async Task<bool> IsSessionEmptyAsync(ActorId actorId, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"{_daprHttpEndpoint}/v1.0/actors/{_actorType}/{actorId.GetId()}/state/metadata";

        var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to read metadata state for actor '{actorId.GetId()}': HTTP {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(
                $"No metadata state found for actor '{actorId.GetId()}' - inconclusive read, cannot determine emptiness");
        }

        var metadata = JsonSerializer.Deserialize<ActorMetadata>(body, DeserializeOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize metadata state for actor '{actorId.GetId()}'");

        return metadata.Queues.Values.Sum(q => q.Count) == 0;
    }
}
