using System.Collections.Concurrent;
using System.Diagnostics;

namespace DaprMQ.Client.Perf;

/// <summary>
/// Seed N sessions x M messages, then drain them with one SessionQueueConsumer whose handler takes
/// SettleMs per message. Completion is the moment every (session, seq) has been handled once.
/// </summary>
public sealed class SessionDrainScenario(IDaprMQClient client, PerfOptions options)
{
    private const int SeedParallelism = 16;

    public async Task<SessionDrainResult> RunAsync(CancellationToken ct)
    {
        // No "-session-" in the base id: QueueActor treats that marker as "I am a per-session actor".
        var queueId = $"perf-drain-{Guid.NewGuid():N}";
        var scenario = options.Scenario;

        Console.WriteLine($"Queue {queueId}: {scenario.Key}, prefetch {scenario.PrefetchCount}, lease {scenario.LeaseSeconds}s, idle-timeout {scenario.SessionIdleTimeoutSeconds}s, publish {scenario.PublishMode}");

        // The clock is the consume clock: it starts when the consumer does, which in concurrent
        // mode is also when publishing starts - so there the wall clock includes the publish.
        var clock = new Stopwatch();
        double Now() => clock.Elapsed.TotalMilliseconds;

        var recorder = new RecordingDaprMQClient(client, Now);
        var handlers = new ConcurrentQueue<HandlerRecord>();
        var seen = new ConcurrentDictionary<(string, int), byte>();
        var expected = scenario.Sessions * scenario.MessagesPerSession;
        var allHandled = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = new SessionQueueConsumer(recorder, queueId, new SessionQueueConsumerOptions
        {
            MaxConcurrentSessions = scenario.MaxConcurrentSessions,
            PrefetchCount = scenario.PrefetchCount,
            LeaseSeconds = scenario.LeaseSeconds,
            SessionIdleTimeoutSeconds = scenario.SessionIdleTimeoutSeconds,
            DrainTimeout = TimeSpan.FromSeconds(5),
        }, async (ctx, handlerCt) =>
        {
            var start = Now();
            var deliveryLatencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ctx.Item.GetProperty("publishedAt").GetInt64();
            await Task.Delay(scenario.SettleMs, handlerCt);
            var end = Now();

            var seq = ctx.Item.GetProperty("seq").GetInt32();
            handlers.Enqueue(new HandlerRecord(ctx.SessionId, seq, start, end, deliveryLatencyMs));
            if (seen.TryAdd((ctx.SessionId, seq), 0) && seen.Count == expected)
            {
                allHandled.TrySetResult(end);
            }
        });

        var timeout = TimeSpan.FromSeconds(scenario.IdealSeconds * 3 + 600);

        using var progress = new Timer(_ =>
            Console.WriteLine($"  t={clock.Elapsed:hh\\:mm\\:ss}  handled {seen.Count}/{expected}  streams {recorder.Streams.Count}"),
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        double seedSeconds;
        Task finished;
        if (scenario.PublishMode == PublishModes.Concurrent)
        {
            clock.Start();
            await consumer.StartAsync(ct);
            try
            {
                seedSeconds = await SeedAndReportAsync(queueId, ct);
                finished = await Task.WhenAny(allHandled.Task, Task.Delay(timeout, ct));
            }
            finally
            {
                await consumer.StopAsync(CancellationToken.None);
            }
        }
        else
        {
            seedSeconds = await SeedAndReportAsync(queueId, ct);
            clock.Start();
            await consumer.StartAsync(ct);
            finished = await Task.WhenAny(allHandled.Task, Task.Delay(timeout, ct));
            await consumer.StopAsync(CancellationToken.None);
        }

        if (finished != allHandled.Task)
        {
            throw new TimeoutException($"Only {seen.Count}/{expected} messages handled after {timeout}.");
        }

        var completedMs = await allHandled.Task;
        return SessionDrainMetrics.Compute(scenario, recorder.Streams, handlers.ToArray(), completedMs, seedSeconds);
    }

    private async Task<double> SeedAndReportAsync(string queueId, CancellationToken ct)
    {
        var seedSeconds = await SeedAsync(queueId, ct);
        Console.WriteLine($"Seeded {options.Sessions * options.MessagesPerSession} messages in {seedSeconds:F1}s");
        return seedSeconds;
    }

    private async Task<double> SeedAsync(string queueId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (options.PublishIntervalMs > 0 || options.PublishJitterMs > 0)
        {
            // Rate limited: every session publishes at once, one message at a time on its own schedule.
            await Task.WhenAll(Enumerable.Range(0, options.Sessions).Select(async session =>
            {
                var sessionId = SessionId(session);
                var delays = PublishSchedule.Delays(options.MessagesPerSession, options.PublishIntervalMs, options.PublishJitterMs, Random.Shared);
                for (var seq = 0; seq < delays.Length; seq++)
                {
                    await Task.Delay(delays[seq], ct);
                    await EnqueueAsync(queueId, sessionId, [seq], ct);
                }
            }));
            return sw.Elapsed.TotalSeconds;
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, options.Sessions),
            new ParallelOptions { MaxDegreeOfParallelism = SeedParallelism, CancellationToken = ct },
            (session, token) => new ValueTask(EnqueueAsync(queueId, SessionId(session), Enumerable.Range(0, options.MessagesPerSession), token)));
        return sw.Elapsed.TotalSeconds;
    }

    private static string SessionId(int session) => $"s{session:D5}";

    private async Task EnqueueAsync(string queueId, string sessionId, IEnumerable<int> seqs, CancellationToken ct)
    {
        // Same process publishes and consumes, so wall-clock ms is a consistent enqueue -> handler clock.
        var publishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var items = seqs.Select(seq => new EnqueueItemDto(new { session = sessionId, seq, publishedAt }, SessionId: sessionId)).ToList();
        var result = await client.EnqueueAsync(queueId, items, ct);
        if (!result.Success || result.ItemsEnqueued != items.Count)
        {
            throw new InvalidOperationException($"Publishing to session {sessionId} failed: {result.Message}");
        }
    }
}
