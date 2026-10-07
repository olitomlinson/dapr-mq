using DaprMQ.Client;

/// <summary>
/// Continuous consumer for the KEDA autoscale demo: dequeue a batch with competing consumers (so every
/// replica KEDA adds gets its own locks instead of a 423), simulate per-item work, acknowledge, repeat.
/// See examples/shared/SCENARIOS.md (`autoscale`).
/// </summary>
public sealed class AutoscaleWorker(
    AppState state, string queueSuffix, int batchSize, TimeSpan itemDelay, Action<string, string> log) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processed = 0;
        log("INFO", $"worker started: draining {{queuePrefix}}-{queueSuffix} in batches of {batchSize}, {itemDelay.TotalMilliseconds}ms per item");

        while (!stoppingToken.IsCancellationRequested)
        {
            var (client, queuePrefix) = state.Snapshot();
            var queueId = $"{queuePrefix}-{queueSuffix}";

            try
            {
                var batch = await client.DequeueLockedAsync(queueId, count: batchSize, allowCompetingConsumers: true, ct: stoppingToken);
                if (batch is null || batch.Items.Count == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                // Finish a batch we already hold even if shutdown starts (KEDA scaling in), rather than
                // leaving its locks to expire and be redelivered.
                foreach (var item in batch.Items)
                {
                    await Task.Delay(itemDelay, CancellationToken.None);
                    await client.AcknowledgeAsync(queueId, item.LockId, ct: CancellationToken.None);
                }

                processed += batch.Items.Count;
                log("INFO", $"processed {batch.Items.Count} items from {queueId} (total {processed})");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log("WARN", $"worker error on {queueId}: {ex.Message}");
                await Task.Delay(ErrorDelay, stoppingToken).ContinueWith(_ => { });
            }
        }

        log("INFO", $"worker stopping after {processed} items");
    }
}
