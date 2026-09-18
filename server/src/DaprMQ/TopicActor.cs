using Dapr.Actors;
using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// A single published item's target subscriber set, resolved once per generation rather than
/// copied onto every item. Immutable once written (PUBSUB-PLAN.md §2.3).
/// </summary>
public record SubscriberSetGeneration
{
    public required long GenerationId { get; init; }
    public required List<string> SubscriberIds { get; init; }
}

/// <summary>
/// One entry in the shared item log, written once regardless of subscriber count.
/// </summary>
public record TopicItem
{
    public required long Sequence { get; init; }
    public required EnqueueItem Item { get; init; }
    public required long Generation { get; init; }
}

/// <summary>
/// Lightweight index kept only while a publish is still in flight, so GetPublishStatus doesn't
/// need to scan the item log. Removed by the reaper once its items are fully delivered/reaped.
/// </summary>
public record PublishRecord
{
    public required long FirstSequence { get; init; }
    public required long LastSequence { get; init; }
    public required long Generation { get; init; }
}

/// <summary>
/// Per-subscriber circuit breaker state. Present only for a subscriber currently in or
/// recovering from a failure streak - absent means healthy.
/// </summary>
public record CircuitBreakerState
{
    public required string SubscriberId { get; init; }
    public int ConsecutiveFailures { get; init; }
    public double? FirstFailureAt { get; init; }
    public double? NextRetryAt { get; init; }
    public bool Blacklisted { get; init; }
}

/// <summary>
/// TopicActor metadata: subscriber set, monotonic counters, and per-subscriber cursors into the
/// shared item log.
/// </summary>
public record TopicMetadata
{
    public List<string> SubscriberIds { get; init; } = new();
    public long NextSequence { get; init; } = 0;
    public long OldestSequence { get; init; } = 0;
    public long CurrentGeneration { get; init; } = 0;
    public Dictionary<string, long> DispatchCursors { get; init; } = new();

    /// <summary>
    /// Monotonic counters indexing the publish-status log ("publish-seq_{n}" -> publish id,
    /// "publish_{id}" -> PublishRecord). A publish's reap-eligibility is monotonic in its
    /// sequence number - same as OldestSequence for the item log - so no explicit list of
    /// pending ids is needed: Publish only ever increments a counter and writes two new,
    /// independent keys, regardless of how large the undelivered backlog is.
    /// </summary>
    public long NextPublishSequence { get; init; } = 0;
    public long OldestPendingPublishSequence { get; init; } = 0;
    public int CurrentRelayIntervalSeconds { get; init; } = 1;
}

/// <summary>
/// TopicActor - fan-out pub/sub built by delegating to per-subscriber QueueActor instances.
/// See PUBSUB-PLAN.md for the full design and the reasoning behind rejected alternatives.
/// </summary>
public class TopicActor : Actor, ITopicActor, IRemindable
{
    private readonly IQueueActorInvoker _queueActorInvoker;
    private readonly IHttpSinkActorInvoker _httpSinkActorInvoker;
    private readonly TopicActorConfig _config;

    private const string MetadataKey = "metadata";
    private const string RelayReminderName = "relay";
    private const string ReapItemsReminderName = "reap-items";

    public TopicActor(
        ActorHost host,
        IQueueActorInvoker queueActorInvoker,
        IHttpSinkActorInvoker httpSinkActorInvoker,
        TopicActorConfig config) : base(host)
    {
        _queueActorInvoker = queueActorInvoker ?? throw new ArgumentNullException(nameof(queueActorInvoker));
        _httpSinkActorInvoker = httpSinkActorInvoker ?? throw new ArgumentNullException(nameof(httpSinkActorInvoker));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task OnActivateAsync()
    {
        var existing = await StateManager.TryGetStateAsync<TopicMetadata>(MetadataKey);

        if (!existing.HasValue)
        {
            var initialMetadata = new TopicMetadata();
            await StateManager.SetStateAsync(MetadataKey, initialMetadata);
            await StateManager.SetStateAsync(
                GenerationKey(0),
                new SubscriberSetGeneration { GenerationId = 0, SubscriberIds = new List<string>() });
            await StateManager.SaveStateAsync();

            await TryRegisterReminderAsync(ReapItemsReminderName, TimeSpan.FromSeconds(_config.ReapItemsIntervalSeconds), TimeSpan.FromSeconds(_config.ReapItemsIntervalSeconds));
            await TryRegisterReminderAsync(GenerationReapReminderName(0), TimeSpan.FromDays(_config.GenerationRetentionDays), TimeSpan.FromMilliseconds(-1));

            Logger.LogDebug("TopicActor {ActorId} activated and metadata initialized", Id.GetId());
            return;
        }

        var metadata = existing.Value;

        await TryRegisterReminderAsync(ReapItemsReminderName, TimeSpan.FromSeconds(_config.ReapItemsIntervalSeconds), TimeSpan.FromSeconds(_config.ReapItemsIntervalSeconds));

        bool anyBehind = metadata.DispatchCursors.Values.Any(cursor => cursor < metadata.NextSequence);
        if (anyBehind)
        {
            var interval = TimeSpan.FromSeconds(metadata.CurrentRelayIntervalSeconds);
            await TryRegisterReminderAsync(RelayReminderName, interval, interval);
        }

        await TryRegisterReminderAsync(GenerationReapReminderName(metadata.CurrentGeneration), TimeSpan.FromDays(_config.GenerationRetentionDays), TimeSpan.FromMilliseconds(-1));

        Logger.LogDebug("TopicActor {ActorId} activated with existing metadata", Id.GetId());
    }

    private static string GenerationKey(long generationId) => $"subscriber-set-generation-{generationId}";

    private static string ItemKey(long sequence) => $"item_{sequence}";

    private static string PublishKey(string publishId) => $"publish_{publishId}";

    private static string PublishSeqKey(long publishSequence) => $"publish-seq_{publishSequence}";

    private static string CircuitKey(string subscriberId) => $"circuit_{subscriberId}";

    private static string GenerationReapReminderName(long generationId) => $"reap-generation-{generationId}";

    private string BuildSubscriberQueueActorId(string subscriberId) => $"{Id.GetId()}-sub-{subscriberId}";

    private async Task<TopicMetadata> GetMetadataAsync()
    {
        var result = await StateManager.TryGetStateAsync<TopicMetadata>(MetadataKey);
        return result.Value;
    }

    private Task SetMetadataAsync(TopicMetadata metadata) => StateManager.SetStateAsync(MetadataKey, metadata);

    private async Task TryRegisterReminderAsync(string name, TimeSpan dueTime, TimeSpan period)
    {
        try
        {
            await RegisterReminderAsync(name, null, dueTime, period);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "TopicActor {ActorId} failed to register reminder {ReminderName} (scheduler unavailable)", Id.GetId(), name);
        }
    }

    // ------------------------------------------------------------------
    // Subscribe / Unsubscribe / ListSubscribers
    // ------------------------------------------------------------------

    public async Task<SubscribeResponse> Subscribe(SubscribeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SubscriberId))
        {
            return new SubscribeResponse { Success = false, QueueActorId = string.Empty, ErrorCode = "VALIDATION_ERROR", ErrorMessage = "SubscriberId cannot be empty" };
        }

        var metadata = await GetMetadataAsync();

        if (metadata.SubscriberIds.Contains(request.SubscriberId))
        {
            return new SubscribeResponse { Success = false, QueueActorId = string.Empty, ErrorCode = "SUBSCRIBER_EXISTS", ErrorMessage = "Subscriber already exists" };
        }

        long newGeneration = metadata.CurrentGeneration + 1;
        var newSubscriberIds = new List<string>(metadata.SubscriberIds) { request.SubscriberId };

        await StateManager.SetStateAsync(GenerationKey(newGeneration), new SubscriberSetGeneration
        {
            GenerationId = newGeneration,
            SubscriberIds = newSubscriberIds
        });

        var newCursors = new Dictionary<string, long>(metadata.DispatchCursors)
        {
            [request.SubscriberId] = metadata.NextSequence
        };

        metadata = metadata with
        {
            SubscriberIds = newSubscriberIds,
            CurrentGeneration = newGeneration,
            DispatchCursors = newCursors
        };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();

        await TryRegisterReminderAsync(GenerationReapReminderName(newGeneration), TimeSpan.FromDays(_config.GenerationRetentionDays), TimeSpan.FromMilliseconds(-1));

        Logger.LogInformation("TopicActor {ActorId} subscribed {SubscriberId} at generation {Generation}", Id.GetId(), request.SubscriberId, newGeneration);

        string queueActorId = BuildSubscriberQueueActorId(request.SubscriberId);

        if (request.HttpSink != null)
        {
            await InitializeHttpSinkBestEffortAsync(queueActorId, request.SubscriberId, request.HttpSink);
        }

        if (request.DedupEnabled.HasValue)
        {
            await ConfigureDedupBestEffortAsync(queueActorId, request.SubscriberId, request.DedupEnabled.Value);
        }

        return new SubscribeResponse { Success = true, QueueActorId = queueActorId };
    }

    /// <summary>
    /// Configures dedup on a subscriber's provisioned queue at subscribe time. Best-effort, same
    /// reasoning as InitializeHttpSinkBestEffortAsync above: the subscription itself is already
    /// committed by the time this runs, so a failure here logs a warning rather than failing
    /// Subscribe - the caller can always reconfigure the queue afterwards directly.
    /// </summary>
    private async Task ConfigureDedupBestEffortAsync(string queueActorId, string subscriberId, bool enabled)
    {
        try
        {
            await _queueActorInvoker.InvokeMethodAsync<ConfigureDedupRequest, ConfigureDedupResponse>(
                new ActorId(queueActorId),
                "ConfigureDedup",
                new ConfigureDedupRequest { Enabled = enabled });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "TopicActor {ActorId} failed to configure dedup for subscriber {SubscriberId}", Id.GetId(), subscriberId);
        }
    }

    /// <summary>
    /// Registers a push (HTTP sink) delivery on a subscriber's provisioned queue. Best-effort: the
    /// pull-based subscription above is already committed by the time this runs, so a failure here
    /// (sink actor unreachable, transient error) logs a warning rather than failing Subscribe -
    /// the caller can always register a sink afterwards via the existing queue sink endpoints.
    /// </summary>
    private async Task InitializeHttpSinkBestEffortAsync(string queueActorId, string subscriberId, TopicHttpSinkConfig sinkConfig)
    {
        try
        {
            await _httpSinkActorInvoker.InvokeMethodAsync(
                new ActorId($"{queueActorId}-sink"),
                "InitializeHttpSink",
                new InitializeHttpSinkRequest
                {
                    Url = sinkConfig.Url,
                    QueueActorId = queueActorId,
                    MaxConcurrency = sinkConfig.MaxConcurrency,
                    LockTtlSeconds = sinkConfig.LockTtlSeconds
                });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "TopicActor {ActorId} failed to initialize HTTP sink for subscriber {SubscriberId}", Id.GetId(), subscriberId);
        }
    }

    public async Task<UnsubscribeResponse> Unsubscribe(UnsubscribeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SubscriberId))
        {
            return new UnsubscribeResponse { Success = false, ErrorCode = "VALIDATION_ERROR", ErrorMessage = "SubscriberId cannot be empty" };
        }

        var metadata = await GetMetadataAsync();

        if (!metadata.SubscriberIds.Contains(request.SubscriberId))
        {
            return new UnsubscribeResponse { Success = false, ErrorCode = "SUBSCRIBER_NOT_FOUND", ErrorMessage = "Subscriber not found" };
        }

        long newGeneration = metadata.CurrentGeneration + 1;
        var newSubscriberIds = metadata.SubscriberIds.Where(s => s != request.SubscriberId).ToList();

        await StateManager.SetStateAsync(GenerationKey(newGeneration), new SubscriberSetGeneration
        {
            GenerationId = newGeneration,
            SubscriberIds = newSubscriberIds
        });

        var newCursors = new Dictionary<string, long>(metadata.DispatchCursors);
        newCursors.Remove(request.SubscriberId);

        metadata = metadata with
        {
            SubscriberIds = newSubscriberIds,
            CurrentGeneration = newGeneration,
            DispatchCursors = newCursors
        };
        await SetMetadataAsync(metadata);
        await StateManager.TryRemoveStateAsync(CircuitKey(request.SubscriberId));
        await StateManager.SaveStateAsync();

        await TryRegisterReminderAsync(GenerationReapReminderName(newGeneration), TimeSpan.FromDays(_config.GenerationRetentionDays), TimeSpan.FromMilliseconds(-1));

        Logger.LogInformation("TopicActor {ActorId} unsubscribed {SubscriberId} at generation {Generation}", Id.GetId(), request.SubscriberId, newGeneration);

        return new UnsubscribeResponse { Success = true };
    }

    public async Task<ListSubscribersResponse> ListSubscribers()
    {
        var metadata = await GetMetadataAsync();
        return new ListSubscribersResponse { SubscriberIds = new List<string>(metadata.SubscriberIds) };
    }

    // ------------------------------------------------------------------
    // Publish
    // ------------------------------------------------------------------

    public async Task<PublishResponse> Publish(PublishRequest request)
    {
        if (request.Items == null || request.Items.Count == 0)
        {
            return new PublishResponse { Accepted = false, PublishId = string.Empty, Sequence = -1, ErrorMessage = "Items cannot be empty" };
        }

        var metadata = await GetMetadataAsync();
        long firstSequence = metadata.NextSequence;
        long generation = metadata.CurrentGeneration;
        long sequence = firstSequence;

        foreach (var item in request.Items)
        {
            await StateManager.SetStateAsync(ItemKey(sequence), new TopicItem
            {
                Sequence = sequence,
                Item = item,
                Generation = generation
            });
            sequence++;
        }

        long lastSequence = sequence - 1;
        string publishId = Guid.NewGuid().ToString("N");
        long publishSequence = metadata.NextPublishSequence;

        await StateManager.SetStateAsync(PublishKey(publishId), new PublishRecord
        {
            FirstSequence = firstSequence,
            LastSequence = lastSequence,
            Generation = generation
        });
        await StateManager.SetStateAsync(PublishSeqKey(publishSequence), publishId);

        metadata = metadata with
        {
            NextSequence = sequence,
            NextPublishSequence = publishSequence + 1,
            CurrentRelayIntervalSeconds = 1
        };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();

        await TryRegisterReminderAsync(RelayReminderName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Logger.LogDebug("TopicActor {ActorId} published {Count} item(s) as PublishId {PublishId}, sequence {First}-{Last}", Id.GetId(), request.Items.Count, publishId, firstSequence, lastSequence);

        return new PublishResponse { Accepted = true, PublishId = publishId, Sequence = firstSequence };
    }

    public async Task<PublishStatusResponse> GetPublishStatus(GetPublishStatusRequest request)
    {
        var recordResult = await StateManager.TryGetStateAsync<PublishRecord>(PublishKey(request.PublishId));
        if (!recordResult.HasValue)
        {
            return new PublishStatusResponse { Found = false };
        }

        var record = recordResult.Value;
        var metadata = await GetMetadataAsync();

        var genSnapshot = await StateManager.TryGetStateAsync<SubscriberSetGeneration>(GenerationKey(record.Generation));
        var targetSubscriberIds = genSnapshot.HasValue ? genSnapshot.Value.SubscriberIds : new List<string>();

        var deliveredSubscriberIds = targetSubscriberIds
            .Where(s => metadata.DispatchCursors.TryGetValue(s, out var cursor) && cursor > record.LastSequence)
            .ToList();

        bool complete = targetSubscriberIds.All(s =>
            !metadata.DispatchCursors.TryGetValue(s, out var cursor) || cursor > record.LastSequence);

        return new PublishStatusResponse
        {
            Found = true,
            Complete = complete,
            TargetSubscriberIds = targetSubscriberIds,
            DeliveredSubscriberIds = deliveredSubscriberIds
        };
    }

    // ------------------------------------------------------------------
    // Circuit breaker
    // ------------------------------------------------------------------

    public async Task<ResetCircuitBreakerResponse> ResetCircuitBreaker(ResetCircuitBreakerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SubscriberId))
        {
            return new ResetCircuitBreakerResponse { Success = false, ErrorCode = "VALIDATION_ERROR", ErrorMessage = "SubscriberId cannot be empty" };
        }

        var metadata = await GetMetadataAsync();
        if (!metadata.SubscriberIds.Contains(request.SubscriberId))
        {
            return new ResetCircuitBreakerResponse { Success = false, ErrorCode = "SUBSCRIBER_NOT_FOUND", ErrorMessage = "Subscriber not found" };
        }

        await StateManager.TryRemoveStateAsync(CircuitKey(request.SubscriberId));
        await StateManager.SaveStateAsync();

        Logger.LogInformation("TopicActor {ActorId} reset circuit breaker for {SubscriberId}", Id.GetId(), request.SubscriberId);

        return new ResetCircuitBreakerResponse { Success = true };
    }

    public async Task<CircuitBreakerStatusResponse> GetCircuitBreakerStatus(GetCircuitBreakerStatusRequest request)
    {
        var circuit = await StateManager.TryGetStateAsync<CircuitBreakerState>(CircuitKey(request.SubscriberId));
        if (!circuit.HasValue)
        {
            return new CircuitBreakerStatusResponse { Found = false };
        }

        var s = circuit.Value;
        return new CircuitBreakerStatusResponse
        {
            Found = true,
            ConsecutiveFailures = s.ConsecutiveFailures,
            FirstFailureAt = s.FirstFailureAt,
            NextRetryAt = s.NextRetryAt,
            Blacklisted = s.Blacklisted
        };
    }

    private async Task RecordFailureAsync(string subscriberId, double now)
    {
        var existing = await StateManager.TryGetStateAsync<CircuitBreakerState>(CircuitKey(subscriberId));
        int consecutiveFailures = (existing.HasValue ? existing.Value.ConsecutiveFailures : 0) + 1;
        double firstFailureAt = existing.HasValue && existing.Value.FirstFailureAt.HasValue ? existing.Value.FirstFailureAt.Value : now;
        bool blacklisted = (now - firstFailureAt) >= _config.CircuitBreakerBlacklistAfterSeconds;
        double? nextRetryAt = blacklisted ? null : now + Math.Min(Math.Pow(2, consecutiveFailures), _config.CircuitBreakerMaxBackoffSeconds);

        await StateManager.SetStateAsync(CircuitKey(subscriberId), new CircuitBreakerState
        {
            SubscriberId = subscriberId,
            ConsecutiveFailures = consecutiveFailures,
            FirstFailureAt = firstFailureAt,
            NextRetryAt = nextRetryAt,
            Blacklisted = blacklisted
        });

        if (blacklisted)
        {
            Logger.LogWarning("TopicActor {ActorId} blacklisted subscriber {SubscriberId} after sustained failures", Id.GetId(), subscriberId);
        }
    }

    private Task ClearCircuitBreakerAsync(string subscriberId) => StateManager.TryRemoveStateAsync(CircuitKey(subscriberId));

    // ------------------------------------------------------------------
    // Relay
    // ------------------------------------------------------------------

    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        try
        {
            if (reminderName == RelayReminderName)
            {
                await RelayTickAsync();
            }
            else if (reminderName == ReapItemsReminderName)
            {
                await ReapItemsTickAsync();
            }
            else if (reminderName.StartsWith("reap-generation-"))
            {
                long generationId = long.Parse(reminderName["reap-generation-".Length..]);
                await ReapGenerationTickAsync(generationId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "TopicActor {ActorId} error in ReceiveReminderAsync for reminder {ReminderName}", Id.GetId(), reminderName);
        }
    }

    /// <summary>
    /// Whether a subscriber is currently eligible for an Enqueue attempt this tick (not blacklisted,
    /// not still in its backoff window).
    /// </summary>
    private async Task<bool> IsEligibleAsync(string subscriberId, double now)
    {
        var circuit = await StateManager.TryGetStateAsync<CircuitBreakerState>(CircuitKey(subscriberId));
        if (!circuit.HasValue)
        {
            return true;
        }

        if (circuit.Value.Blacklisted)
        {
            return false;
        }

        return !circuit.Value.NextRetryAt.HasValue || circuit.Value.NextRetryAt.Value <= now;
    }

    private async Task<EnqueueResponse> EnqueueWithTimeoutAsync(string subscriberId, EnqueueRequest enqueueRequest)
    {
        var actorId = new ActorId(BuildSubscriberQueueActorId(subscriberId));
        var timeout = TimeSpan.FromSeconds(_config.RelayTickTimeoutSeconds);

        try
        {
            var enqueueTask = _queueActorInvoker.InvokeMethodAsync<EnqueueRequest, EnqueueResponse>(actorId, "Enqueue", enqueueRequest);
            var completed = await Task.WhenAny(enqueueTask, Task.Delay(timeout));

            if (completed != enqueueTask)
            {
                return new EnqueueResponse { Success = false, ItemsEnqueued = 0, ErrorMessage = "Relay enqueue timed out" };
            }

            return await enqueueTask;
        }
        catch (Exception ex)
        {
            return new EnqueueResponse { Success = false, ItemsEnqueued = 0, ErrorMessage = ex.Message };
        }
    }

    private async Task RelayTickAsync()
    {
        var metadata = await GetMetadataAsync();

        if (metadata.SubscriberIds.Count == 0)
        {
            return;
        }

        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var eligibleSubscribers = new List<string>();
        foreach (var subscriberId in metadata.SubscriberIds)
        {
            if (await IsEligibleAsync(subscriberId, now))
            {
                eligibleSubscribers.Add(subscriberId);
            }
        }

        var generationCache = new Dictionary<long, SubscriberSetGeneration?>();
        async Task<SubscriberSetGeneration?> ResolveGenerationAsync(long generationId)
        {
            if (generationCache.TryGetValue(generationId, out var cached))
            {
                return cached;
            }

            var result = await StateManager.TryGetStateAsync<SubscriberSetGeneration>(GenerationKey(generationId));
            var value = result.HasValue ? result.Value : null;
            generationCache[generationId] = value;
            return value;
        }

        var batches = new Dictionary<string, List<EnqueueItem>>();
        var advancedCursors = new Dictionary<string, long>();

        foreach (var subscriberId in eligibleSubscribers)
        {
            long cursor = metadata.DispatchCursors.TryGetValue(subscriberId, out var c) ? c : metadata.NextSequence;
            long advanced = cursor;
            var batch = new List<EnqueueItem>();

            for (int i = 0; i < _config.RelayBatchSize && advanced < metadata.NextSequence; i++)
            {
                var itemResult = await StateManager.TryGetStateAsync<TopicItem>(ItemKey(advanced));
                if (!itemResult.HasValue)
                {
                    advanced++;
                    continue;
                }

                var topicItem = itemResult.Value;
                var genSet = await ResolveGenerationAsync(topicItem.Generation);
                bool isMember = genSet != null && genSet.SubscriberIds.Contains(subscriberId);
                advanced++;

                if (isMember)
                {
                    batch.Add(topicItem.Item);
                }
            }

            advancedCursors[subscriberId] = advanced;
            if (batch.Count > 0)
            {
                batches[subscriberId] = batch;
            }
        }

        var enqueueTasks = batches.ToDictionary(
            kvp => kvp.Key,
            kvp => EnqueueWithTimeoutAsync(kvp.Key, new EnqueueRequest { Items = kvp.Value }));

        if (enqueueTasks.Count > 0)
        {
            await Task.WhenAll(enqueueTasks.Values);
        }

        var updatedCursors = new Dictionary<string, long>(metadata.DispatchCursors);

        foreach (var subscriberId in eligibleSubscribers)
        {
            if (!batches.ContainsKey(subscriberId))
            {
                // Nothing to enqueue (empty or fully non-member batch) - cursor still advances past
                // whatever was skipped, no Enqueue call needed.
                updatedCursors[subscriberId] = advancedCursors[subscriberId];
                continue;
            }

            bool success = enqueueTasks[subscriberId].Result.Success;

            if (success)
            {
                updatedCursors[subscriberId] = advancedCursors[subscriberId];
                await ClearCircuitBreakerAsync(subscriberId);
            }
            else
            {
                // Leave the cursor untouched for retry on a later tick.
                await RecordFailureAsync(subscriberId, now);
            }
        }

        int nextInterval = enqueueTasks.Count > 0
            ? 1
            : Math.Min(metadata.CurrentRelayIntervalSeconds * 2, _config.RelayIntervalCeilingSeconds);

        metadata = metadata with { DispatchCursors = updatedCursors, CurrentRelayIntervalSeconds = nextInterval };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();

        var nextIntervalSpan = TimeSpan.FromSeconds(nextInterval);
        await TryRegisterReminderAsync(RelayReminderName, nextIntervalSpan, nextIntervalSpan);
    }

    private async Task ReapItemsTickAsync()
    {
        var metadata = await GetMetadataAsync();

        long minCursor = metadata.DispatchCursors.Count > 0
            ? metadata.DispatchCursors.Values.Min()
            : metadata.NextSequence;

        for (long seq = metadata.OldestSequence; seq < minCursor; seq++)
        {
            await StateManager.RemoveStateAsync(ItemKey(seq));
        }

        long publishSeq = metadata.OldestPendingPublishSequence;
        while (publishSeq < metadata.NextPublishSequence)
        {
            var idResult = await StateManager.TryGetStateAsync<string>(PublishSeqKey(publishSeq));
            if (!idResult.HasValue)
            {
                // Already cleaned up somehow (shouldn't normally happen) - treat as reaped.
                publishSeq++;
                continue;
            }

            var recordResult = await StateManager.TryGetStateAsync<PublishRecord>(PublishKey(idResult.Value));
            if (recordResult.HasValue && recordResult.Value.LastSequence >= minCursor)
            {
                // Not yet reapable. Publish records are indexed in creation order, which is the
                // same order their LastSequence values increase in, so nothing after this one
                // can be reapable yet either - stop rather than checking the rest.
                break;
            }

            await StateManager.RemoveStateAsync(PublishKey(idResult.Value));
            await StateManager.RemoveStateAsync(PublishSeqKey(publishSeq));
            publishSeq++;
        }

        metadata = metadata with
        {
            OldestSequence = Math.Max(metadata.OldestSequence, minCursor),
            OldestPendingPublishSequence = publishSeq
        };
        await SetMetadataAsync(metadata);
        await StateManager.SaveStateAsync();
    }

    private async Task ReapGenerationTickAsync(long generationId)
    {
        var metadata = await GetMetadataAsync();

        if (generationId == metadata.CurrentGeneration)
        {
            await TryRegisterReminderAsync(GenerationReapReminderName(generationId), TimeSpan.FromDays(_config.GenerationRetentionDays), TimeSpan.FromMilliseconds(-1));
            return;
        }

        await StateManager.RemoveStateAsync(GenerationKey(generationId));
        await StateManager.SaveStateAsync();
    }
}
