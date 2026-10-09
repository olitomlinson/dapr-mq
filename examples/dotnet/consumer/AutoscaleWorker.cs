using DaprMQ.Client;

/// <summary>
/// Continuous consumer for the KEDA autoscale demo: a <see cref="QueueConsumer"/> with competing consumers
/// (so every replica KEDA adds holds its own locks), simulating per-item work. See
/// examples/shared/SCENARIOS.md (`autoscale`).
/// </summary>
public sealed class AutoscaleWorker(
    AppState state, string queueSuffix, int batchSize, TimeSpan itemDelay, Action<string, string> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processed = 0;
        var (client, queuePrefix) = state.Snapshot();
        var queueId = $"{queuePrefix}-{queueSuffix}";
        log("INFO", $"worker started: draining {queueId}, {batchSize} in flight, {itemDelay.TotalMilliseconds}ms per item");

        // One handler at a time over a window of batchSize keeps each replica's pace, and the backlog
        // KEDA sees, the same as dequeuing batchSize at once. The server renews the locks it holds.
        var options = new QueueConsumerOptions { MaxActiveMessages = batchSize, MaxConcurrentHandlers = 1 };
        await using var consumer = new QueueConsumer(client, queueId, options, async (_, _) =>
        {
            await Task.Delay(itemDelay, CancellationToken.None);
            if (Interlocked.Increment(ref processed) % batchSize == 0)
            {
                log("INFO", $"processed {batchSize} items from {queueId} (total {processed})");
            }
        });
        await consumer.StartAsync();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        // Disposing stops the consumer: a message already being handled finishes and is acked (KEDA scaling
        // in), and the rest held by this replica go straight back to the queue.
        log("INFO", $"worker stopping after {processed} items");
    }
}
