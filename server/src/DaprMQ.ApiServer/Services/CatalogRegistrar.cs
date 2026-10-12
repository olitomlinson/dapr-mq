using System.Collections.Concurrent;
using System.Threading.Channels;
using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.Interfaces;

namespace DaprMQ.ApiServer.Services;

/// <summary>
/// Worker-local catalog registration, off the actors' turns. <see cref="Register"/> is a
/// non-blocking channel write that drops ids already sent by this process; the background loop
/// groups entries by CatalogActor shard and sends at most one call per shard per second.
/// Nothing is persisted: an id whose send fails is forgotten here, so the actor's next activation
/// registers it again. A worker restart re-sends each id once, and the shard drops the ones it knows.
/// </summary>
public sealed class CatalogRegistrar : BackgroundService, ICatalogRegistrar
{
    public static readonly TimeSpan MinShardInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly ICatalogActorInvoker _invoker;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogRegistrar> _logger;
    private readonly ConcurrentDictionary<string, byte> _sent = new();
    private readonly Channel<CatalogEntry> _channel = Channel.CreateUnbounded<CatalogEntry>(new UnboundedChannelOptions { SingleReader = true });

    // Owned by the single reader (FlushAsync).
    private readonly Dictionary<int, List<CatalogEntry>> _pending = new();
    private readonly Dictionary<int, DateTimeOffset> _lastSentAt = new();

    public CatalogRegistrar(ICatalogActorInvoker invoker, TimeProvider time, ILogger<CatalogRegistrar> logger)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Register(CatalogEntry entry)
    {
        if (_sent.TryAdd(entry.Id, 0))
        {
            _channel.Writer.TryWrite(entry);
        }
    }

    /// <summary>
    /// Drains the channel and sends each shard's pending entries, skipping shards sent to less than
    /// <see cref="MinShardInterval"/> ago (they go on a later flush). Single caller only.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (_channel.Reader.TryRead(out var entry))
        {
            var shard = CatalogShards.ShardFor(entry.Id);
            if (!_pending.TryGetValue(shard, out var entries))
            {
                _pending[shard] = entries = [];
            }
            entries.Add(entry);
        }

        var now = _time.GetUtcNow();
        var due = _pending
            .Where(p => !_lastSentAt.TryGetValue(p.Key, out var last) || now - last >= MinShardInterval)
            .ToList();
        foreach (var (shard, _) in due)
        {
            _pending.Remove(shard);
            _lastSentAt[shard] = now;
        }

        await Task.WhenAll(due.Select(p => SendAsync(p.Key, p.Value, cancellationToken)));
    }

    private async Task SendAsync(int shard, List<CatalogEntry> entries, CancellationToken cancellationToken)
    {
        try
        {
            await _invoker.InvokeMethodAsync<RegisterCatalogBatchRequest, RegisterCatalogBatchResponse>(
                new ActorId(CatalogShards.ActorIdFor(shard)),
                ActorMethodNames.RegisterCatalogBatch,
                new RegisterCatalogBatchRequest { Entries = entries },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (var entry in entries)
            {
                _sent.TryRemove(entry.Id, out _);
            }
            _logger.LogWarning(ex, "Catalog registration of {Count} entries to shard {Shard} failed; they register again on next activation",
                entries.Count, shard);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_pending.Count == 0)
                {
                    await _channel.Reader.WaitToReadAsync(stoppingToken);
                }
                await FlushAsync(stoppingToken);
                await Task.Delay(TickInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
