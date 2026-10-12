using System.Collections.Concurrent;

namespace DaprMQ.Operator.Console;

/// <summary>
/// Short-lived response cache shared by every console user, for list and detail reads (browse pages
/// aren't cached). Concurrent callers share the in-flight read. Only 200s are kept: a 404 for a
/// queue about to be created, or a failure, is never served from here.
/// </summary>
public sealed class ConsoleResponseCache
{
    private const int PruneAbove = 1024;

    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, Task<WorkerReply> Read)> _entries = new();

    public ConsoleResponseCache(TimeSpan ttl, TimeProvider timeProvider)
    {
        _ttl = ttl;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<WorkerReply> GetOrAddAsync(string key, Func<Task<WorkerReply>> read, CancellationToken cancellationToken)
    {
        if (_ttl <= TimeSpan.Zero)
        {
            return await read();
        }

        var now = _timeProvider.GetUtcNow();
        if (!_entries.TryGetValue(key, out var entry) || entry.ExpiresAt <= now || entry.Read.IsFaulted || entry.Read.IsCanceled)
        {
            if (_entries.Count > PruneAbove)
            {
                foreach (var expired in _entries.Where(e => e.Value.ExpiresAt <= now))
                {
                    _entries.TryRemove(expired);
                }
            }

            entry = (now + _ttl, read());
            _entries[key] = entry;
        }

        try
        {
            var reply = await entry.Read.WaitAsync(cancellationToken);
            if (reply.StatusCode != StatusCodes.Status200OK)
            {
                _entries.TryRemove(KeyValuePair.Create(key, entry));
            }

            return reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _entries.TryRemove(KeyValuePair.Create(key, entry));
            throw;
        }
    }
}
