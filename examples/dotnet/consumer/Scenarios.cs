using System.Text.Json;
using DaprMQ.Client;

/// <summary>Consumer side of the 4 demo scenarios - see examples/shared/SCENARIOS.md.</summary>
public static class Scenarios
{
    public static readonly IReadOnlyList<ScenarioDescriptor> Descriptors = new List<ScenarioDescriptor>
    {
        new("basic", "consumer", "Dequeues up to 5 items and acknowledges each."),
        new("ack-deadletter", "consumer", "Acknowledges, dead-letters, and lets one item expire, then drains the DLQ."),
        new("priority", "consumer", "Dequeues items to show fast-lane items surfacing before normal ones."),
        new("sessions", "consumer", "Accepts, dequeues, acknowledges, and releases two sessions in turn."),
        new("idempotency", "consumer", "Dequeues and acknowledges the survivors of the producer's dedup demo."),
    };

    public static bool IsKnown(string name) => Descriptors.Any(d => d.Name == name);

    public static Task<(string QueueId, List<StepDto> Steps)> RunAsync(
        string name, IDaprMQClient client, string queuePrefix, Action<string, string> log) => name switch
    {
        "basic" => RunBasicAsync(client, queuePrefix, log),
        "ack-deadletter" => RunAckDeadletterAsync(client, queuePrefix, log),
        "priority" => RunPriorityAsync(client, queuePrefix, log),
        "sessions" => RunSessionsAsync(client, queuePrefix, log),
        "idempotency" => RunIdempotencyAsync(client, queuePrefix, log),
        _ => throw new ArgumentOutOfRangeException(nameof(name), $"unknown scenario '{name}'")
    };

    private static void Step(List<StepDto> steps, Action<string, string> log, string action, string detail, string level = "INFO")
    {
        log(level, detail);
        steps.Add(new StepDto(action, detail));
    }

    private static string GetOutcome(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("outcome", out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? "ack";
        }

        return "ack";
    }

    private static string GetField(JsonElement item, string name)
    {
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var prop))
        {
            return prop.ToString();
        }

        return "?";
    }

    private static async Task<(string, List<StepDto>)> RunBasicAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-basic";
        var steps = new List<StepDto>();

        var dequeued = await client.DequeueLockedAsync(queueId, count: 5);
        if (dequeued is null || dequeued.Items.Count == 0)
        {
            Step(steps, log, "dequeue", "queue empty — run the producer's scenario first");
            return (queueId, steps);
        }

        Step(steps, log, "dequeue", $"dequeued {dequeued.Items.Count} items from {queueId}");

        foreach (var item in dequeued.Items)
        {
            await client.AcknowledgeAsync(queueId, item.LockId);
            Step(steps, log, "acknowledge", $"acknowledged item n={GetField(item.Item, "n")} (lockId={item.LockId})");
        }

        return (queueId, steps);
    }

    private static async Task<(string, List<StepDto>)> RunAckDeadletterAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-ackdlq";
        var steps = new List<StepDto>();

        var dequeued = await client.DequeueLockedAsync(queueId, count: 3, ttlSeconds: 10);
        if (dequeued is null || dequeued.Items.Count == 0)
        {
            Step(steps, log, "dequeue", "queue empty — run the producer's scenario first");
            return (queueId, steps);
        }

        Step(steps, log, "dequeue", $"dequeued {dequeued.Items.Count} items, lockIds=[{string.Join(",", dequeued.Items.Select(i => i.LockId))}]");

        foreach (var item in dequeued.Items)
        {
            var outcome = GetOutcome(item.Item);
            switch (outcome)
            {
                case "deadletter":
                    await client.DeadLetterAsync(queueId, item.LockId);
                    var dlqId = $"{queueId}-deadletter";
                    Step(steps, log, "deadletter", $"dead-lettered item outcome=deadletter -> dlqId={dlqId}");
                    break;

                case "expire":
                    Step(steps, log, "skip", "leaving item outcome=expire locked to let it expire");
                    break;

                default:
                    await client.AcknowledgeAsync(queueId, item.LockId);
                    Step(steps, log, "acknowledge", $"acked item outcome={outcome}");
                    break;
            }
        }

        Step(steps, log, "wait", "waiting 11s for outcome=expire item's lock to expire");
        await Task.Delay(TimeSpan.FromSeconds(11));

        var redelivered = await client.DequeueLockedAsync(queueId, count: 3, ttlSeconds: 10);
        if (redelivered is not null && redelivered.Items.Count > 0)
        {
            Step(steps, log, "dequeue", "dequeued expired item again via redelivery");
            foreach (var item in redelivered.Items)
            {
                await client.AcknowledgeAsync(queueId, item.LockId);
                Step(steps, log, "acknowledge", $"acked redelivered item n={GetField(item.Item, "n")} (drained)");
            }
        }
        else
        {
            Step(steps, log, "dequeue", "no redelivered item found on retry", "WARN");
        }

        var dlqQueueId = $"{queueId}-deadletter";
        var dlqDequeued = await client.DequeueLockedAsync(dlqQueueId, count: 1, ttlSeconds: 10);
        if (dlqDequeued is not null && dlqDequeued.Items.Count > 0)
        {
            foreach (var item in dlqDequeued.Items)
            {
                Step(steps, log, "dequeue", $"dequeued dead-lettered item from {dlqQueueId}: n={GetField(item.Item, "n")}");
                await client.AcknowledgeAsync(dlqQueueId, item.LockId);
                Step(steps, log, "acknowledge", $"acked item in {dlqQueueId} (drained)");
            }
        }
        else
        {
            Step(steps, log, "dequeue", $"{dlqQueueId} empty — nothing to drain");
        }

        return (queueId, steps);
    }

    private static async Task<(string, List<StepDto>)> RunPriorityAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-priority";
        var steps = new List<StepDto>();

        var dequeued = await client.DequeueLockedAsync(queueId, count: 10);
        if (dequeued is null || dequeued.Items.Count == 0)
        {
            Step(steps, log, "dequeue", "queue empty — run the producer's scenario first");
            return (queueId, steps);
        }

        var order = string.Join(",", dequeued.Items.Select(i => $"n={GetField(i.Item, "n")}"));
        Step(steps, log, "dequeue", $"dequeued {dequeued.Items.Count} items from {queueId} in order: [{order}]");

        foreach (var item in dequeued.Items)
        {
            await client.AcknowledgeAsync(queueId, item.LockId);
            Step(steps, log, "acknowledge", $"acked item n={GetField(item.Item, "n")}");
        }

        return (queueId, steps);
    }

    private static readonly string[] KnownSessionIds = { "session-a", "session-b" };

    private static async Task<(string, List<StepDto>)> RunSessionsAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-sessions";
        var steps = new List<StepDto>();

        // Round 1: "any available" mode (sessionId: null) - the server picks whichever
        // of the known sessions is currently unclaimed.
        string? claimedFirst = null;
        var anyLease = await client.AcceptSessionAsync(queueId, sessionId: null, leaseSeconds: 30);
        if (anyLease is null)
        {
            Step(steps, log, "accept-session", "no session currently available (any-available mode) — run the producer's scenario first", "WARN");
        }
        else
        {
            claimedFirst = anyLease.SessionId;
            Step(steps, log, "accept-session", $"accepted session {claimedFirst} via any-available mode, leaseId={anyLease.LeaseId}");
            await DrainSessionAsync(client, queueId, claimedFirst, anyLease.LeaseId, steps, log);
        }

        // Round 2: targeted mode - accept whichever known session round 1 didn't return
        // (both, if round 1 found nothing available).
        foreach (var sessionId in KnownSessionIds)
        {
            if (sessionId == claimedFirst)
            {
                continue;
            }

            var lease = await client.AcceptSessionAsync(queueId, sessionId: sessionId, leaseSeconds: 30);
            if (lease is null)
            {
                Step(steps, log, "accept-session", $"session {sessionId} not available (targeted mode) — run the producer's scenario first", "WARN");
                continue;
            }

            Step(steps, log, "accept-session", $"accepted session {sessionId} via targeted mode, leaseId={lease.LeaseId}");
            await DrainSessionAsync(client, queueId, sessionId, lease.LeaseId, steps, log);
        }

        return (queueId, steps);
    }

    private static async Task DrainSessionAsync(
        IDaprMQClient client, string queueId, string sessionId, string leaseId, List<StepDto> steps, Action<string, string> log)
    {
        var sessionQueueId = $"{queueId}-session-{sessionId}";
        var dequeued = await client.DequeueLockedAsync(sessionQueueId, count: 10, ttlSeconds: 30, leaseId: leaseId);

        if (dequeued is null || dequeued.Items.Count == 0)
        {
            Step(steps, log, "dequeue", $"{sessionQueueId} empty");
        }
        else
        {
            var order = string.Join(",", dequeued.Items.Select(i => $"seq={GetField(i.Item, "seq")}"));
            Step(steps, log, "dequeue", $"dequeued {dequeued.Items.Count} items from {sessionQueueId} in order: [{order}]");

            foreach (var item in dequeued.Items)
            {
                await client.AcknowledgeAsync(sessionQueueId, item.LockId, leaseId);
                Step(steps, log, "acknowledge", $"acked item seq={GetField(item.Item, "seq")} for session {sessionId}");
            }
        }

        await client.ReleaseSessionAsync(queueId, sessionId, leaseId);
        Step(steps, log, "release-session", $"released session {sessionId}");
    }

    /// <summary>
    /// Dequeues from the queue the producer's idempotency scenario just filled. Expects
    /// exactly 2 items (n=1 and n=3) since n=2, the duplicate, should never have been
    /// enqueued at all.
    /// </summary>
    private static async Task<(string, List<StepDto>)> RunIdempotencyAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-idempotency";
        var steps = new List<StepDto>();

        var dequeued = await client.DequeueLockedAsync(queueId, count: 5);
        if (dequeued is null || dequeued.Items.Count == 0)
        {
            Step(steps, log, "dequeue", "queue empty — run the producer's scenario first");
            return (queueId, steps);
        }

        var order = string.Join(",", dequeued.Items.Select(i => $"n={GetField(i.Item, "n")}"));
        Step(steps, log, "dequeue",
            $"dequeued {dequeued.Items.Count} item(s) from {queueId} in order: [{order}] - expecting 2 (n=1, n=3); the duplicate n=2 should have been silently dropped by idempotency dedup");

        foreach (var item in dequeued.Items)
        {
            await client.AcknowledgeAsync(queueId, item.LockId);
            Step(steps, log, "acknowledge", $"acked item n={GetField(item.Item, "n")}");
        }

        return (queueId, steps);
    }
}
