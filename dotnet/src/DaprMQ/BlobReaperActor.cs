using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using DaprMQ.Interfaces;

namespace DaprMQ;

/// <summary>
/// BlobReaperActor state tracking the blob pending deletion.
/// </summary>
public record BlobReaperActorState
{
    public required string BlobReference { get; init; }

    /// <summary>
    /// Unix timestamp the reminder is currently scheduled to fire at. Tracked so PostponeDeletion
    /// can tell whether a requested delay would push the deletion later or earlier.
    /// </summary>
    public double ScheduledAt { get; init; }
}

/// <summary>
/// BlobReaperActor - deletes offloaded object-store blobs after a delay, using a self-rescheduling
/// reminder so a transient deletion failure gets retried a few times before giving up.
/// </summary>
public class BlobReaperActor : Actor, IBlobReaperActor, IRemindable
{
    private readonly IObjectStore _objectStore;
    private const string StateKey = "reap-state";
    private const string ReminderName = "reap";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromMinutes(1);

    public BlobReaperActor(ActorHost host, IObjectStore objectStore) : base(host)
    {
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
    }

    /// <summary>
    /// Schedules deletion of the given blob reference after the specified delay.
    /// </summary>
    public async Task ScheduleDeletion(ScheduleDeletionRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.BlobReference))
        {
            throw new ArgumentException("BlobReference cannot be empty", nameof(request));
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, request.DelaySeconds));
        var scheduledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + delay.TotalSeconds;

        var state = new BlobReaperActorState { BlobReference = request.BlobReference, ScheduledAt = scheduledAt };
        await StateManager.SetStateAsync(StateKey, state);
        await StateManager.SaveStateAsync();

        await RegisterReminderAsync(
            ReminderName,
            null,
            delay,
            RetryPeriod);

        Logger.LogInformation(
            "BlobReaperActor {ActorId} scheduled deletion of {BlobReference} in {DelaySeconds}s",
            Id.GetId(), request.BlobReference, request.DelaySeconds);
    }

    /// <summary>
    /// Pushes the scheduled deletion out to fire NewDelaySeconds from now, but only if that's
    /// later than the currently scheduled deletion time.
    /// </summary>
    public async Task PostponeDeletion(PostponeDeletionRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var reaperState = await StateManager.TryGetStateAsync<BlobReaperActorState>(StateKey);
        if (!reaperState.HasValue)
        {
            Logger.LogDebug(
                "BlobReaperActor {ActorId} PostponeDeletion no-op - no deletion currently scheduled for {BlobReference}",
                Id.GetId(), request.BlobReference);
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(0, request.NewDelaySeconds));
        var newScheduledAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + delay.TotalSeconds;

        if (newScheduledAt <= reaperState.Value.ScheduledAt)
        {
            Logger.LogDebug(
                "BlobReaperActor {ActorId} PostponeDeletion no-op - new delay would not push deletion of {BlobReference} later",
                Id.GetId(), request.BlobReference);
            return;
        }

        var updatedState = reaperState.Value with { ScheduledAt = newScheduledAt };
        await StateManager.SetStateAsync(StateKey, updatedState);
        await StateManager.SaveStateAsync();

        await RegisterReminderAsync(
            ReminderName,
            null,
            delay,
            RetryPeriod);

        Logger.LogInformation(
            "BlobReaperActor {ActorId} postponed deletion of {BlobReference} to {NewDelaySeconds}s from now",
            Id.GetId(), request.BlobReference, request.NewDelaySeconds);
    }

    /// <summary>
    /// Reminder callback - attempts deletion, self-terminating on success or after MaxAttempts.
    /// </summary>
    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        if (reminderName != ReminderName)
        {
            return;
        }

        var reaperState = await StateManager.TryGetStateAsync<BlobReaperActorState>(StateKey);
        if (!reaperState.HasValue)
        {
            await StopReapingAsync();
            return;
        }

        try
        {
            await _objectStore.DeleteAsync(reaperState.Value.BlobReference, CancellationToken.None);
            Logger.LogInformation(
                "BlobReaperActor {ActorId} deleted blob {BlobReference}",
                Id.GetId(), reaperState.Value.BlobReference);
            await StopReapingAsync();
        }
        catch (Exception ex)
        {
            var attempts = await IncrementAttemptCountAsync();
            Logger.LogWarning(ex,
                "BlobReaperActor {ActorId} failed to delete blob {BlobReference} (attempt {Attempts}/{MaxAttempts})",
                Id.GetId(), reaperState.Value.BlobReference, attempts, MaxAttempts);

            if (attempts >= MaxAttempts)
            {
                Logger.LogError(
                    "BlobReaperActor {ActorId} giving up on blob {BlobReference} after {MaxAttempts} attempts",
                    Id.GetId(), reaperState.Value.BlobReference, MaxAttempts);
                await StopReapingAsync();
            }
        }
    }

    private const string AttemptCountKey = "reap-attempts";

    private async Task<int> IncrementAttemptCountAsync()
    {
        var current = await StateManager.TryGetStateAsync<int>(AttemptCountKey);
        var next = (current.HasValue ? current.Value : 0) + 1;
        await StateManager.SetStateAsync(AttemptCountKey, next);
        await StateManager.SaveStateAsync();
        return next;
    }

    private async Task StopReapingAsync()
    {
        try
        {
            await UnregisterReminderAsync(ReminderName);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "BlobReaperActor {ActorId} failed to unregister reminder (may not exist)", Id.GetId());
        }

        await StateManager.TryRemoveStateAsync(StateKey);
        await StateManager.TryRemoveStateAsync(AttemptCountKey);
        await StateManager.SaveStateAsync();
    }
}
