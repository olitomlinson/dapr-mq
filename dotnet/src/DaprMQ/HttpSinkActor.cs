using System.Text.Json;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using DaprMQ.Interfaces;
using System.Net.Http.Json;

namespace DaprMQ;

/// <summary>
/// HTTP sink actor state containing HTTP endpoint configuration and polling parameters.
/// </summary>
public record HttpSinkActorState
{
    public required string Url { get; init; }
    public required string QueueActorId { get; init; }
    public required int MaxConcurrency { get; init; }
    public required int LockTtlSeconds { get; init; }
    public int CurrentIntervalSeconds { get; init; } = 1;
}

/// <summary>
/// HttpSinkActor - Delivers messages to external HTTP endpoints using pull-based polling.
/// </summary>
public class HttpSinkActor : Actor, IHttpSinkActor, IRemindable
{
    private readonly IQueueActorInvoker _queueActorInvoker;
    private readonly IHttpClientFactory _httpClientFactory;
    private const string StateKey = "sink-state";
    private const string ReminderName = "sink-poll";

    public HttpSinkActor(ActorHost host, IQueueActorInvoker actorInvoker, IHttpClientFactory httpClientFactory) : base(host)
    {
        _queueActorInvoker = actorInvoker ?? throw new ArgumentNullException(nameof(actorInvoker));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <summary>
    /// Initialize the HTTP sink with configuration and register polling reminder.
    /// </summary>
    public async Task InitializeHttpSink(InitializeHttpSinkRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            throw new ArgumentException("URL cannot be empty", nameof(request));
        }

        var state = new HttpSinkActorState
        {
            Url = request.Url,
            QueueActorId = request.QueueActorId,
            MaxConcurrency = request.MaxConcurrency,
            LockTtlSeconds = request.LockTtlSeconds,
            CurrentIntervalSeconds = 1  // Start with fast polling
        };

        await StateManager.SetStateAsync(StateKey, state);
        await StateManager.SaveStateAsync();

        // Register reminder for periodic polling (starting at 1s for dynamic polling)
        await RegisterReminderAsync(
            ReminderName,
            null,
            TimeSpan.Zero,  // Start immediately
            TimeSpan.FromSeconds(1));

        Logger.LogInformation(
            "HttpSinkActor {ActorId} initialized with URL: {Url}, dynamic polling starting at 1s",
            Id.GetId(), request.Url);
    }

    /// <summary>
    /// Uninitialize the HTTP sink, unregister reminder, and clear state.
    /// </summary>
    public async Task UninitializeHttpSink()
    {
        try
        {
            await UnregisterReminderAsync(ReminderName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "HttpSinkActor {ActorId} failed to unregister reminder (may not exist)", Id.GetId());
        }

        await StateManager.RemoveStateAsync(StateKey);
        await StateManager.SaveStateAsync();

        Logger.LogInformation("HttpSinkActor {ActorId} uninitialized", Id.GetId());
    }

    /// <summary>
    /// Reminder callback - triggers polling and delivery.
    /// </summary>
    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        if (reminderName == ReminderName)
        {
            await PollAndDeliver();
        }
    }

    /// <summary>
    /// Poll the queue and deliver items to the configured HTTP endpoint.
    /// </summary>
    private async Task PollAndDeliver()
    {
        try
        {
            var state = await StateManager.GetStateAsync<HttpSinkActorState>(StateKey);

            // Call PopWithAck on QueueActor
            var popRequest = new PopWithAckRequest
            {
                Count = 100,  // Request up to 100 items per poll
                MaxConcurrency = state.MaxConcurrency,  // But respect total lock limit
                TtlSeconds = state.LockTtlSeconds,
                AllowCompetingConsumers = true
            };

            var popResult = await _queueActorInvoker.InvokeMethodAsync<PopWithAckRequest, PopWithAckResponse>(
                new ActorId(state.QueueActorId),
                "PopWithAck",
                popRequest);

            int nextInterval;

            if (popResult.Items.Count == 0)
            {
                if (popResult.MaxConcurrencyReached)
                {
                    Logger.LogDebug("HttpSinkActor {ActorId} poll: max concurrency reached", Id.GetId());
                    // MaxConcurrency reached: increase interval but cap at 4s
                    nextInterval = Math.Min(state.CurrentIntervalSeconds * 2, 4);
                }
                else
                {
                    Logger.LogDebug("HttpSinkActor {ActorId} poll: no items available", Id.GetId());
                    // Empty queue: increase interval, cap at 60s
                    nextInterval = Math.Min(state.CurrentIntervalSeconds * 2, 60);
                }

                await UpdatePollingInterval(state, nextInterval);
                return;
            }

            Logger.LogInformation("HttpSinkActor {ActorId} polled {Count} items", Id.GetId(), popResult.Items.Count);

            // Items found: reset to fast polling (1s)
            nextInterval = 1;
            await UpdatePollingInterval(state, nextInterval);

            // HTTP POST to configured URL
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            // Deserialize ItemJson strings into actual JSON objects to avoid double-encoding
            // while preserving metadata (priority, lockId, lockExpiresAt)
            //TODO : I don't love this as its not fully type safe, must refactor

            var result = popResult.Items.Select(item =>
                        new
                        {
                            item = JsonDocument.Parse(item.ItemJson).RootElement,
                            item.Priority,
                            item.LockId,
                            item.LockExpiresAt
                        }
                    ).ToList();

            var response = await httpClient.PostAsync(state.Url, JsonContent.Create(result));

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // 200 OK - Acknowledge all locks
                Logger.LogInformation("HttpSinkActor {ActorId} received 200 OK, acknowledging {Count} locks",
                    Id.GetId(), popResult.Items.Count);

                foreach (var item in popResult.Items)
                {
                    try
                    {
                        var ackRequest = new AcknowledgeRequest { LockId = item.LockId };
                        await _queueActorInvoker.InvokeMethodAsync<AcknowledgeRequest, AcknowledgeResponse>(
                            new ActorId(state.QueueActorId),
                            "Acknowledge",
                            ackRequest);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "HttpSinkActor {ActorId} failed to acknowledge lock {LockId}",
                            Id.GetId(), item.LockId);
                    }
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                // 202 Accepted - Endpoint will handle acknowledgement
                Logger.LogInformation("HttpSinkActor {ActorId} received 202 Accepted, endpoint will handle acks",
                    Id.GetId());
            }
            else
            {
                // Other status codes - failure, locks will expire and requeue
                Logger.LogError("HttpSinkActor {ActorId} HTTP delivery failed with status {StatusCode}",
                    Id.GetId(), response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HttpSinkActor {ActorId} error during poll and deliver", Id.GetId());
        }
    }

    /// <summary>
    /// Update polling interval if changed, re-registering the reminder with new period.
    /// </summary>
    private async Task UpdatePollingInterval(HttpSinkActorState currentState, int nextInterval)
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

            Logger.LogDebug("HttpSinkActor {ActorId} updated polling interval: {OldInterval}s → {NewInterval}s",
                Id.GetId(), currentState.CurrentIntervalSeconds, nextInterval);
        }
    }
}
