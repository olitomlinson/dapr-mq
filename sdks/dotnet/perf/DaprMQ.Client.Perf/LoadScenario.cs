using System.Diagnostics;

namespace DaprMQ.Client.Perf;

public sealed record LoadResult(IReadOnlyList<LoadStep> Steps, IReadOnlyList<StepTimeline> Timelines, bool Drained, IReadOnlyList<string> SampleErrors);

/// <summary>
/// Closed-loop load (P-01..P-03): each step runs `concurrency` workers back to back on one shared
/// client for warmup + duration, worker w on queue w % queues. A ramp is a sequence of steps, each
/// on fresh queues. See sdks/testing/PERFORMANCE_TESTS.md#closed-loop-load-p-01p-03.
/// </summary>
public sealed class LoadScenario(IDaprMQClient client, LoadParams load)
{
    private const int SeedBatch = 100;
    private const int SeedParallelism = 16;
    private const int MaxSampleErrors = 5;

    public async Task<LoadResult> RunAsync(CancellationToken ct)
    {
        var steps = new List<LoadStep>();
        var timelines = new List<StepTimeline>();
        var errors = new List<string>();
        var drained = false;
        var pad = new string('x', load.PayloadBytes);

        foreach (var concurrency in load.Concurrency)
        {
            var queues = load.QueuesAt(concurrency);
            var runId = Guid.NewGuid().ToString("N")[..12];
            // No "-session-" in the base id: QueueActor treats that marker as a per-session actor.
            var queueIds = Enumerable.Range(0, queues).Select(q => $"perf-{load.Scenario}-{runId}-q{q}").ToArray();

            if (load.Scenario == LoadScenarios.DequeueAck)
            {
                var seedSw = Stopwatch.StartNew();
                await SeedAsync(queueIds, pad, ct);
                Console.WriteLine($"  seeded {queues} x {load.SeedPerQueue} in {seedSw.Elapsed.TotalSeconds:F1}s");
            }

            var warmupMs = load.WarmupSeconds * 1000.0;
            var durationMs = load.DurationSeconds * 1000.0;
            var clock = Stopwatch.StartNew();
            var perWorker = new List<OpRecord>[concurrency];
            var stepDrained = false;

            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(w => Task.Run(async () =>
            {
                var records = perWorker[w] = new List<OpRecord>(4096);
                var queueId = queueIds[w % queues];
                var seq = 0;
                while (clock.Elapsed.TotalMilliseconds < warmupMs + durationMs && !ct.IsCancellationRequested)
                {
                    var start = clock.Elapsed.TotalMilliseconds;
                    int messages;
                    var error = false;
                    try
                    {
                        messages = await OperationAsync(queueId, w, seq, pad, ct);
                        seq += Math.Max(messages, 1);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        messages = 0;
                        error = true;
                        lock (errors)
                        {
                            if (errors.Count < MaxSampleErrors)
                            {
                                errors.Add($"{ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }

                    if (messages < 0)
                    {
                        stepDrained = true;
                        break;
                    }

                    var end = clock.Elapsed.TotalMilliseconds;
                    records.Add(new OpRecord(end, end - start, messages, error));
                }
            }, ct)));

            var (step, timeline) = LoadMetrics.ComputeStep(concurrency, queues, perWorker.SelectMany(r => r).ToList(), warmupMs, durationMs);
            steps.Add(step);
            timelines.Add(timeline);
            drained |= stepDrained;

            Console.WriteLine($"  c={concurrency,-4} q={queues,-4} {step.MessagesPerSecond,10:F0} msg/s  p50 {step.LatencyMs.P50,7:F2}  p95 {step.LatencyMs.P95,7:F2}  p99 {step.LatencyMs.P99,7:F2} ms  errors {step.Errors}{(stepDrained ? "  DRAINED" : "")}");
        }

        return new LoadResult(steps, timelines, drained, errors);
    }

    /// <summary>One operation. Returns the messages it moved, or -1 when a dequeue found the queue empty.</summary>
    private async Task<int> OperationAsync(string queueId, int worker, int seq, string pad, CancellationToken ct)
    {
        if (load.Scenario == LoadScenarios.DequeueAck)
        {
            var result = await client.DequeueLockedAsync(queueId, load.DequeueCount, ct: ct);
            if (result == null || result.Items.Count == 0)
            {
                return -1;
            }

            foreach (var item in result.Items)
            {
                await client.AcknowledgeAsync(queueId, item.LockId, ct: ct);
            }

            return result.Items.Count;
        }

        var batch = load.Scenario == LoadScenarios.EnqueueBatch ? load.BatchSize : 1;
        var items = Items(worker, seq, batch, pad);
        var enqueued = await client.EnqueueAsync(queueId, items, ct);
        if (!enqueued.Success || enqueued.ItemsEnqueued != batch)
        {
            throw new InvalidOperationException($"Enqueue returned {enqueued.ItemsEnqueued}/{batch}: {enqueued.Message}");
        }

        return batch;
    }

    private static List<EnqueueItemDto> Items(int worker, int seq, int count, string pad)
    {
        var publishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Enumerable.Range(seq, count).Select(n => new EnqueueItemDto(new { worker, seq = n, publishedAt, pad })).ToList();
    }

    private async Task SeedAsync(string[] queueIds, string pad, CancellationToken ct)
    {
        var batches = queueIds.SelectMany((queueId, q) =>
            Enumerable.Range(0, (int)Math.Ceiling(load.SeedPerQueue / (double)SeedBatch))
                .Select(b => (queueId, q, from: b * SeedBatch, count: Math.Min(SeedBatch, load.SeedPerQueue - b * SeedBatch))));

        // Batches for one queue go in order (one actor serialises them anyway); queues in parallel.
        await Parallel.ForEachAsync(
            batches.GroupBy(b => b.queueId),
            new ParallelOptions { MaxDegreeOfParallelism = SeedParallelism, CancellationToken = ct },
            async (queue, token) =>
            {
                foreach (var (queueId, q, from, count) in queue)
                {
                    var result = await client.EnqueueAsync(queueId, Items(q, from, count, pad), token);
                    if (!result.Success || result.ItemsEnqueued != count)
                    {
                        throw new InvalidOperationException($"Seeding {queueId} failed: {result.Message}");
                    }
                }
            });
    }
}
