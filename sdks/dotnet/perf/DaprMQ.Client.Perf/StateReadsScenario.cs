using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.Client.Perf;

/// <summary>
/// Counts actor-state reads and writes per operation for a fixed set of steps, from the Postgres
/// statement log (log_statement=all). Each step's setup runs unmeasured; only the log lines its
/// measured operations produce are counted. Every step except topic-relay is driven synchronously
/// on a warm actor, so its counts are deterministic and a change in them is a change in the code.
/// </summary>
public sealed class StateReadsScenario(DaprTestEnvironment stack, DaprMQClient client)
{
    private const int Ops = 50;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public async Task<IReadOnlyList<StateReadsStep>> RunAsync(CancellationToken ct)
    {
        var steps = new List<StateReadsStep>();
        async Task Step(string name, int operations, Func<Task> measured)
        {
            steps.Add(await MeasureAsync(name, operations, measured));
            Console.WriteLine($"{name,-18} x{operations,-4} reads/op {steps[^1].ReadsPerOp,6:F2}   writes/op {steps[^1].WritesPerOp,6:F2}");
        }

        // A queue's first operation: actor activation (and its lock sweep) plus the first write.
        var fresh = Enumerable.Range(0, 20).Select(i => Queue($"new-{i}")).ToList();
        await Step("enqueue-new-queue", fresh.Count, async () =>
        {
            foreach (var queueId in fresh)
            {
                await EnqueueAsync(queueId, 1, ct);
            }
        });

        var warm = Queue("enqueue");
        await EnqueueAsync(warm, 1, ct);
        await Step("enqueue", Ops, async () =>
        {
            for (var i = 0; i < Ops; i++)
            {
                await EnqueueAsync(warm, 1, ct);
            }
        });

        var batched = Queue("enqueue-batch");
        await EnqueueAsync(batched, 1, ct);
        await Step("enqueue-batch-10", 10, async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await EnqueueAsync(batched, 10, ct);
            }
        });

        // Every dequeue comes from segment 0, so no segment is emptied (and deleted) mid-step.
        var drain = Queue("dequeue-ack");
        await EnqueueAsync(drain, 150, ct);
        await Step("dequeue-ack", Ops, async () =>
        {
            for (var i = 0; i < Ops; i++)
            {
                var item = Assert(await client.DequeueLockedAsync(drain, ct: ct)).Items.Single();
                await client.AcknowledgeAsync(drain, item.LockId, ct: ct);
            }
        });

        // A session consumer's full cycle: claim, drain three messages, release.
        var sessions = Queue("sessions");
        var sessionIds = Enumerable.Range(0, 10).Select(i => $"s{i}").ToList();
        foreach (var sessionId in sessionIds)
        {
            await client.EnqueueAsync(sessions, Items(3, sessionId), ct);
        }
        await Step("session-cycle", sessionIds.Count, async () =>
        {
            foreach (var sessionId in sessionIds)
            {
                var lease = Assert(await client.AcceptSessionAsync(sessions, sessionId, ct: ct));
                var sessionQueue = $"{sessions}-session-{sessionId}";
                for (var i = 0; i < 3; i++)
                {
                    var item = Assert(await client.DequeueLockedAsync(sessionQueue, leaseId: lease.LeaseId, ct: ct)).Items.Single();
                    await client.AcknowledgeAsync(sessionQueue, item.LockId, lease.LeaseId, ct);
                }
                await client.ReleaseSessionAsync(sessions, sessionId, lease.LeaseId, ct);
            }
        });

        // Locks are swept lazily: one dequeue after expiry reclaims all of them.
        var expiring = Queue("lock-expiry");
        const int locked = 20;
        await EnqueueAsync(expiring, locked, ct);
        Assert(await client.DequeueLockedAsync(expiring, count: locked, ttlSeconds: 1, ct: ct));
        await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
        await Step("lock-expiry-sweep", locked, async () =>
        {
            Assert(await client.DequeueLockedAsync(expiring, ct: ct));
        });

        // Publish interleaved with the relay reminder's ~1 s ticks, so this step's counts depend on
        // timing (hence its looser tolerance). Per publish, including the relay to 2 subscribers.
        var topic = Queue("topic");
        await PostAsync($"/topic/{topic}/subscribers/sub-a", null, ct);
        await PostAsync($"/topic/{topic}/subscribers/sub-b", null, ct);
        await PublishAsync(topic, 0, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await Step("topic-relay", 10, async () =>
        {
            for (var i = 1; i <= 10; i++)
            {
                await PublishAsync(topic, i, ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        });

        return steps;
    }

    private async Task<StateReadsStep> MeasureAsync(string name, int operations, Func<Task> measured)
    {
        var before = (await stack.GetPostgresLogsAsync()).Length;
        await measured();
        var log = (await stack.GetPostgresLogsAsync())[before..];

        var queries = StatementLog.Parse(log);
        return new StateReadsStep(name, operations,
            queries.Count(q => q.Kind == StateQueryKind.Read),
            queries.Count(q => q.Kind == StateQueryKind.Write),
            StatementLog.Summarise(queries));
    }

    private string Queue(string name) => $"state-reads-{_run}-{name}";

    private static List<EnqueueItemDto> Items(int count, string? sessionId = null) =>
        Enumerable.Range(0, count).Select(i => new EnqueueItemDto(new { seq = i }, SessionId: sessionId)).ToList();

    private async Task EnqueueAsync(string queueId, int count, CancellationToken ct) =>
        Assert(await client.EnqueueAsync(queueId, Items(count), ct) is { Success: true } r ? r : null);

    private Task PublishAsync(string topicId, int seq, CancellationToken ct) =>
        PostAsync($"/topic/{topicId}/publish",
            JsonContent.Create(new ApiPublishRequest([new ApiEnqueueItem(JsonSerializer.SerializeToElement(new { seq }), Priority: 1)])), ct);

    private async Task PostAsync(string path, HttpContent? content, CancellationToken ct)
    {
        using var response = await stack.ApiClient.PostAsync(path, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
        }
    }

    private static T Assert<T>(T? value) where T : class =>
        value ?? throw new InvalidOperationException("Expected a result from the server, got none.");
}
