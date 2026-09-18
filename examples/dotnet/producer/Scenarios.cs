using DaprMQ.Client;

/// <summary>Producer side of the 4 demo scenarios - see examples/shared/SCENARIOS.md.</summary>
public static class Scenarios
{
    public static readonly IReadOnlyList<ScenarioDescriptor> Descriptors = new List<ScenarioDescriptor>
    {
        new("basic", "producer", "Enqueues 3 plain items."),
        new("ack-deadletter", "producer", "Enqueues 3 items tagged ack/deadletter/expire."),
        new("priority", "producer", "Enqueues normal-priority items, then fast-lane items."),
        new("sessions", "producer", "Enqueues items across two sessions."),
        new("idempotency", "producer", "Enqueues an item, a duplicate reusing its idempotency key, then a distinct item."),
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

    private static void Step(List<StepDto> steps, Action<string, string> log, string action, string detail)
    {
        log("INFO", detail);
        steps.Add(new StepDto(action, detail));
    }

    private static async Task<(string, List<StepDto>)> RunBasicAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-basic";
        var steps = new List<StepDto>();

        var items = new[]
        {
            new EnqueueItemDto(new { n = 1, message = "hello from producer" }),
            new EnqueueItemDto(new { n = 2, message = "hello from producer" }),
            new EnqueueItemDto(new { n = 3, message = "hello from producer" }),
        };

        await client.EnqueueAsync(queueId, items);

        for (var i = 0; i < items.Length; i++)
        {
            Step(steps, log, "enqueue", $"Enqueued item {i + 1}/{items.Length} to queue {queueId} (priority={items[i].Priority})");
        }

        return (queueId, steps);
    }

    private static async Task<(string, List<StepDto>)> RunAckDeadletterAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-ackdlq";
        var steps = new List<StepDto>();

        var payloads = new (string Outcome, int N)[]
        {
            ("ack", 1),
            ("deadletter", 2),
            ("expire", 3),
        };
        var items = payloads.Select(p => new EnqueueItemDto(new { outcome = p.Outcome, n = p.N })).ToArray();

        await client.EnqueueAsync(queueId, items);

        foreach (var p in payloads)
        {
            Step(steps, log, "enqueue", $"Enqueued item to queue {queueId}: outcome={p.Outcome}, n={p.N}");
        }

        return (queueId, steps);
    }

    private static async Task<(string, List<StepDto>)> RunPriorityAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-priority";
        var steps = new List<StepDto>();

        // Deliberately priority-inverted enqueue order: normal (priority=1) items first,
        // then fast-lane (priority=0) items, to demonstrate priority ordering on dequeue.
        var normal = new[]
        {
            new EnqueueItemDto(new { priority = 1, n = 1 }, Priority: 1),
            new EnqueueItemDto(new { priority = 1, n = 2 }, Priority: 1),
            new EnqueueItemDto(new { priority = 1, n = 3 }, Priority: 1),
        };
        await client.EnqueueAsync(queueId, normal);
        for (var i = 0; i < normal.Length; i++)
        {
            Step(steps, log, "enqueue", $"Enqueued item {i + 1}/3 (n={i + 1}) to queue {queueId} (priority=1)");
        }

        var fast = new[]
        {
            new EnqueueItemDto(new { priority = 0, n = 4 }, Priority: 0),
            new EnqueueItemDto(new { priority = 0, n = 5 }, Priority: 0),
            new EnqueueItemDto(new { priority = 0, n = 6 }, Priority: 0),
        };
        await client.EnqueueAsync(queueId, fast);
        for (var i = 0; i < fast.Length; i++)
        {
            Step(steps, log, "enqueue", $"Enqueued item {i + 1}/3 (n={i + 4}) to queue {queueId} (priority=0)");
        }

        return (queueId, steps);
    }

    private static async Task<(string, List<StepDto>)> RunSessionsAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-sessions";
        var steps = new List<StepDto>();

        var items = new[]
        {
            new EnqueueItemDto(new { sessionId = "session-a", seq = 1 }, SessionId: "session-a"),
            new EnqueueItemDto(new { sessionId = "session-a", seq = 2 }, SessionId: "session-a"),
            new EnqueueItemDto(new { sessionId = "session-b", seq = 1 }, SessionId: "session-b"),
            new EnqueueItemDto(new { sessionId = "session-b", seq = 2 }, SessionId: "session-b"),
        };

        await client.EnqueueAsync(queueId, items);

        foreach (var item in items)
        {
            Step(steps, log, "enqueue", $"Enqueued item to queue {queueId} for session {item.SessionId}");
        }

        return (queueId, steps);
    }

    /// <summary>
    /// Enqueues an item, a duplicate reusing its idempotency key (expected to be
    /// deduplicated), then a distinct item under a different key. The key is suffixed
    /// with the current time so repeated runs don't collide with a previous run's key
    /// still inside the dedup TTL window.
    /// </summary>
    private static async Task<(string, List<StepDto>)> RunIdempotencyAsync(
        IDaprMQClient client, string queuePrefix, Action<string, string> log)
    {
        var queueId = $"{queuePrefix}-idempotency";
        var steps = new List<StepDto>();
        var key = $"idempotency-demo-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var first = await client.EnqueueAsync(queueId, new[] { new EnqueueItemDto(new { n = 1 }, IdempotencyKey: key) });
        Step(steps, log, "enqueue",
            $"Enqueued item n=1 with idempotencyKey={key} (itemsEnqueued={first.ItemsEnqueued}, itemsDeduplicated={first.ItemsDeduplicated})");

        var duplicate = await client.EnqueueAsync(queueId, new[] { new EnqueueItemDto(new { n = 2 }, IdempotencyKey: key) });
        Step(steps, log, "enqueue",
            $"Enqueued item n=2 reusing idempotencyKey={key} (itemsEnqueued={duplicate.ItemsEnqueued}, itemsDeduplicated={duplicate.ItemsDeduplicated}) - expected to be deduplicated");

        var distinctKey = $"{key}-b";
        var distinct = await client.EnqueueAsync(queueId, new[] { new EnqueueItemDto(new { n = 3 }, IdempotencyKey: distinctKey) });
        Step(steps, log, "enqueue",
            $"Enqueued item n=3 with a different idempotencyKey={distinctKey} (itemsEnqueued={distinct.ItemsEnqueued}, itemsDeduplicated={distinct.ItemsDeduplicated})");

        return (queueId, steps);
    }
}
