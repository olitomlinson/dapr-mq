using System.Collections.Concurrent;
using System.Diagnostics;

namespace DaprMQ.Client.Perf;

/// <summary>
/// P-05: publish Messages to a plain queue and drain them with one QueueConsumer whose handler takes
/// SettleMs per message. Completion is the moment every seq has been handled once.
/// </summary>
public sealed class QueueDrainScenario(IDaprMQClient client, QueueDrainParams scenario)
{
    private const int SeedBatch = 100;
    private const int SeedParallelism = 16;

    public async Task<QueueDrainResult> RunAsync(CancellationToken ct)
    {
        var queueId = $"perf-queue-{Guid.NewGuid():N}";
        Console.WriteLine($"Queue {queueId}: {scenario.Key}");

        // The clock is the consume clock. With a live publisher it starts with publishing, so the wall
        // clock includes the publish.
        var clock = new Stopwatch();
        double Now() => clock.Elapsed.TotalMilliseconds;

        var handled = new ConcurrentQueue<QueueHandlerRecord>();
        var seen = new ConcurrentDictionary<int, byte>();
        var allHandled = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var consumer = new QueueConsumer(client, queueId, new QueueConsumerOptions
        {
            MaxActiveMessages = scenario.MaxActiveMessages,
            MaxConcurrentHandlers = scenario.MaxConcurrentHandlers,
            StrictOrder = scenario.StrictOrder,
            DrainTimeout = TimeSpan.FromSeconds(5),
        }, async (ctx, handlerCt) =>
        {
            var start = Now();
            var deliveryLatencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ctx.Item.GetProperty("publishedAt").GetInt64();
            var seq = ctx.Item.GetProperty("seq").GetInt32();
            if (scenario.HandlerMs(seq) is > 0 and var settleMs)
            {
                await Task.Delay(settleMs, handlerCt);
            }

            var end = Now();
            handled.Enqueue(new QueueHandlerRecord(seq, start, end, deliveryLatencyMs));
            if (seen.TryAdd(seq, 0) && seen.Count == scenario.Messages)
            {
                allHandled.TrySetResult(end);
            }
        });

        var timeout = TimeSpan.FromSeconds((scenario.IdealSeconds ?? 0) * 3 + 600);
        using var progress = new Timer(_ =>
            Console.WriteLine($"  t={clock.Elapsed:hh\\:mm\\:ss}  handled {seen.Count}/{scenario.Messages}"),
            null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        double seedSeconds;
        if (scenario.LivePublish)
        {
            clock.Start();
            await consumer.StartAsync(ct);
            seedSeconds = await PublishLiveAsync(queueId, ct);
        }
        else
        {
            seedSeconds = await SeedAsync(queueId, ct);
            clock.Start();
            await consumer.StartAsync(ct);
        }

        Console.WriteLine($"Published {scenario.Messages} messages in {seedSeconds:F1}s");
        var finished = await Task.WhenAny(allHandled.Task, Task.Delay(timeout, ct));
        await consumer.StopAsync(CancellationToken.None);

        if (finished != allHandled.Task)
        {
            throw new TimeoutException($"Only {seen.Count}/{scenario.Messages} messages handled after {timeout}.");
        }

        return QueueDrainMetrics.Compute(scenario, handled.ToArray(), await allHandled.Task, seedSeconds);
    }

    private async Task<double> SeedAsync(string queueId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Batches go in seq order one after another per worker, but the workers run in parallel, so
        // the queue's order isn't seq order. Strict order checks handler order against queue order,
        // so it seeds with one worker.
        var parallelism = scenario.StrictOrder ? 1 : SeedParallelism;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, (scenario.Messages + SeedBatch - 1) / SeedBatch),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            (batch, token) => new ValueTask(EnqueueAsync(queueId,
                Enumerable.Range(batch * SeedBatch, Math.Min(SeedBatch, scenario.Messages - batch * SeedBatch)), token)));
        return sw.Elapsed.TotalSeconds;
    }

    private async Task<double> PublishLiveAsync(string queueId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var delays = PublishSchedule.Delays(scenario.Messages, scenario.PublishIntervalMs, scenario.PublishJitterMs, Random.Shared);
        for (var seq = 0; seq < delays.Length; seq++)
        {
            await Task.Delay(delays[seq], ct);
            await EnqueueAsync(queueId, [seq], ct);
        }

        return sw.Elapsed.TotalSeconds;
    }

    private async Task EnqueueAsync(string queueId, IEnumerable<int> seqs, CancellationToken ct)
    {
        // Same process publishes and consumes, so wall-clock ms is a consistent publish -> handler clock.
        var publishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var items = seqs.Select(seq => new EnqueueItemDto(new { seq, publishedAt })).ToList();
        var result = await client.EnqueueAsync(queueId, items, ct);
        if (!result.Success || result.ItemsEnqueued != items.Count)
        {
            throw new InvalidOperationException($"Publishing to {queueId} failed: {result.Message}");
        }
    }
}
