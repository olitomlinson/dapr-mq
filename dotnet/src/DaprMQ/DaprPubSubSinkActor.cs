using System.Net.Http.Json;
using System.Text.Json;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using DaprMQ.Interfaces;
using Microsoft.Extensions.Logging;

namespace DaprMQ;

/// <summary>
/// Helper for resolving Dapr configuration.
/// </summary>
internal static class DaprConfigHelper
{
    /// <summary>
    /// Resolves the Dapr HTTP endpoint URL from environment variables or defaults.
    /// Priority: DAPR_HTTP_ENDPOINT > http://127.0.0.1:{DAPR_HTTP_PORT} > http://localhost:3500
    /// </summary>
    public static string ResolveDaprHttpEndpoint()
    {
        var endpoint = Environment.GetEnvironmentVariable("DAPR_HTTP_ENDPOINT");
        if (!string.IsNullOrEmpty(endpoint))
        {
            return endpoint;
        }

        var port = Environment.GetEnvironmentVariable("DAPR_HTTP_PORT");
        if (!string.IsNullOrEmpty(port))
        {
            return $"http://127.0.0.1:{port}";
        }

        return "http://localhost:3500";
    }
}

/// <summary>
/// Actor state for Dapr PubSub sink.
/// </summary>
public record DaprPubSubSinkActorState(
    string PubSubName,
    string Topic,
    bool RawPayload,
    string QueueActorId,
    int MaxConcurrency,
    int LockTtlSeconds
)
{
    public int CurrentIntervalSeconds { get; init; } = 1;
};

/// <summary>
/// DaprPubSubSinkActor implements pull-based message delivery to Dapr PubSub components.
/// Polls a queue at regular intervals and publishes messages to a configured PubSub topic.
/// </summary>
public class DaprPubSubSinkActor : Actor, IDaprPubSubSinkActor, IRemindable
{
    private readonly IQueueActorInvoker _queueActorInvoker;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string StateKey = "sink-state";
    private const string ReminderName = "sink-poll";

    public DaprPubSubSinkActor(
        ActorHost host,
        IQueueActorInvoker queueActorInvoker,
        IHttpClientFactory httpClientFactory) : base(host)
    {
        _queueActorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>
    /// Initializes the Dapr PubSub sink with configuration and registers a polling reminder.
    /// </summary>
    public async Task InitializeDaprPubSubSink(InitializeDaprPubSubSinkRequest request)
    {
        // Validate request
        if (string.IsNullOrEmpty(request.PubSubName))
        {
            throw new ArgumentException("PubSubName cannot be empty", nameof(request));
        }

        if (string.IsNullOrEmpty(request.Topic))
        {
            throw new ArgumentException("Topic cannot be empty", nameof(request));
        }

        if (string.IsNullOrEmpty(request.QueueActorId))
        {
            throw new ArgumentException("QueueActorId cannot be empty", nameof(request));
        }

        if (request.MaxConcurrency < 1 || request.MaxConcurrency > 100)
        {
            throw new ArgumentException("MaxConcurrency must be between 1 and 100", nameof(request));
        }

        if (request.LockTtlSeconds < 1 || request.LockTtlSeconds > 300)
        {
            throw new ArgumentException("LockTtlSeconds must be between 1 and 300", nameof(request));
        }

        var state = new DaprPubSubSinkActorState(
            request.PubSubName,
            request.Topic,
            request.RawPayload,
            request.QueueActorId,
            request.MaxConcurrency,
            request.LockTtlSeconds
        )
        { CurrentIntervalSeconds = 1 };

        await StateManager.SetStateAsync(StateKey, state);
        await StateManager.SaveStateAsync();

        // Register reminder for periodic polling (starting at 1s for dynamic polling)
        await RegisterReminderAsync(
            ReminderName,
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));

        Logger.LogInformation(
            "DaprPubSubSinkActor {ActorId} initialized: PubSubName={PubSubName}, Topic={Topic}, RawPayload={RawPayload}, QueueActorId={QueueActorId}, MaxConcurrency={MaxConcurrency}, LockTtlSeconds={LockTtlSeconds}, dynamic polling starting at 1s",
            Id.GetId(), request.PubSubName, request.Topic, request.RawPayload, request.QueueActorId, request.MaxConcurrency, request.LockTtlSeconds);
    }

    /// <summary>
    /// Uninitializes the Dapr PubSub sink, unregistering the polling reminder and clearing state.
    /// </summary>
    public async Task UninitializeDaprPubSubSink()
    {
        try
        {
            await UnregisterReminderAsync(ReminderName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to unregister reminder {ReminderName}", ReminderName);
        }

        await StateManager.RemoveStateAsync(StateKey);
        await StateManager.SaveStateAsync();

        Logger.LogInformation("DaprPubSubSinkActor {ActorId} uninitialized", Id.GetId());
    }

    /// <summary>
    /// Reminder callback that triggers polling and delivery.
    /// </summary>
    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        if (reminderName == ReminderName)
        {
            await PollAndDeliver();
        }
    }

    /// <summary>
    /// Polls the queue and publishes items to Dapr PubSub.
    /// </summary>
    private async Task PollAndDeliver()
    {
        try
        {
            var stateResult = await StateManager.TryGetStateAsync<DaprPubSubSinkActorState>(StateKey);
            if (!stateResult.HasValue)
            {
                Logger.LogWarning("DaprPubSubSinkActor {ActorId} state not found, skipping poll", Id.GetId());
                return;
            }

            var state = stateResult.Value;

            // PopWithAck from queue (competing consumers enabled, up to 100 items)
            var popRequest = new PopWithAckRequest
            {
                TtlSeconds = state.LockTtlSeconds,
                Count = 100,
                AllowCompetingConsumers = true,
                MaxConcurrency = state.MaxConcurrency
            };

            var popResult = await _queueActorInvoker.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                new ActorId(state.QueueActorId),
                "PopWithAck",
                popRequest);

            int nextInterval;

            if (popResult.IsEmpty || popResult.Items.Count == 0)
            {
                if (popResult.MaxConcurrencyReached)
                {
                    Logger.LogDebug("DaprPubSubSinkActor {ActorId} MaxConcurrency reached, will retry next poll", Id.GetId());
                    // MaxConcurrency reached: increase interval but cap at 4s
                    nextInterval = Math.Min(state.CurrentIntervalSeconds * 2, 4);
                }
                else
                {
                    Logger.LogDebug("DaprPubSubSinkActor {ActorId} queue is empty, nothing to publish", Id.GetId());
                    // Empty queue: increase interval, cap at 60s
                    nextInterval = Math.Min(state.CurrentIntervalSeconds * 2, 60);
                }

                await UpdatePollingInterval(state, nextInterval);
                return;
            }

            Logger.LogInformation("DaprPubSubSinkActor {ActorId} polling {Count} items from queue", Id.GetId(), popResult.Items.Count);

            // Items found: reset to fast polling (1s)
            nextInterval = 1;
            await UpdatePollingInterval(state, nextInterval);

            // Resolve Dapr URL dynamically from environment
            var daprUrl = DaprConfigHelper.ResolveDaprHttpEndpoint();

            // Publish each item to Dapr PubSub
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            foreach (var item in popResult.Items)
            {
                try
                {
                    // Build message body with item, priority, lockId, lockExpiresAt
                    var messageBody = new
                    {
                        item = JsonDocument.Parse(item.ItemJson).RootElement,
                        item.Priority,
                        item.LockId,
                        item.LockExpiresAt
                    };

                    // Extract metadata from sink config
                    var itemMetadata = item.Sink?.DaprPubSub?.Metadata ?? new Dictionary<string, string>();

                    // Build query string: rawPayload first, then item metadata
                    var queryParams = new List<string> { $"metadata.rawPayload={state.RawPayload.ToString().ToLower()}" };
                    queryParams.AddRange(itemMetadata.Select(kv => $"metadata.{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
                    var queryString = string.Join("&", queryParams);

                    var url = $"{daprUrl}/v1.0/publish/{state.PubSubName}/{state.Topic}?{queryString}";
                    Logger.LogDebug("DaprPubSubSinkActor {ActorId} publishing on {url}", Id.GetId(), url);

                    var response = await httpClient.PostAsync(url, JsonContent.Create(messageBody));

                    if (response.IsSuccessStatusCode)
                    {
                        Logger.LogDebug(
                            "DaprPubSubSinkActor {ActorId} published item with LockId={LockId} to topic={Topic}",
                            Id.GetId(), item.LockId, state.Topic);
                    }
                    else
                    {
                        Logger.LogError(
                            "DaprPubSubSinkActor {ActorId} failed to publish item with LockId={LockId}, status={StatusCode}",
                            Id.GetId(), item.LockId, response.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex,
                        "DaprPubSubSinkActor {ActorId} exception publishing item with LockId={LockId}",
                        Id.GetId(), item.LockId);
                }
            }

            Logger.LogInformation(
                "DaprPubSubSinkActor {ActorId} published {Count} items to topic={Topic}",
                Id.GetId(), popResult.Items.Count, state.Topic);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "DaprPubSubSinkActor {ActorId} error during poll and deliver", Id.GetId());
        }
    }

    /// <summary>
    /// Update polling interval if changed, re-registering the reminder with new period.
    /// </summary>
    private async Task UpdatePollingInterval(DaprPubSubSinkActorState currentState, int nextInterval)
    {
        if (nextInterval != currentState.CurrentIntervalSeconds)
        {
            var updatedState = currentState with { CurrentIntervalSeconds = nextInterval };
            await StateManager.SetStateAsync(StateKey, updatedState);
            await StateManager.SaveStateAsync();

            // Re-register reminder with new interval (overwrites existing)
            await RegisterReminderAsync(
                ReminderName,
                null,
                TimeSpan.FromSeconds(nextInterval),
                TimeSpan.FromSeconds(nextInterval));

            Logger.LogDebug("DaprPubSubSinkActor {ActorId} updated polling interval: {OldInterval}s → {NewInterval}s",
                Id.GetId(), currentState.CurrentIntervalSeconds, nextInterval);
        }
    }
}
