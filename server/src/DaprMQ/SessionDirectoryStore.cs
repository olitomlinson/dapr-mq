using System.Text;
using Dapr.Actors.Runtime;

namespace DaprMQ;

/// <summary>A ReadyQueue item. Live only while its session's entry has the same ReadySeq.</summary>
public record ReadyItem(string SessionId, long Seq);

/// <summary>The ReadyQueue's segment pointers: items run from segment Head to segment Tail.</summary>
public record ReadyMeta
{
    public long Head { get; init; }
    public long Tail { get; init; }
    public long NextSeq { get; init; } = 1;
}

/// <summary>
/// The SessionCoordinatorActor's state keys. The directory is split into fixed hash buckets and the
/// ReadyQueue into FIFO segments, so a turn writes only the few small keys it changed rather than
/// every session (proposals/session-directory-segments.md).
/// </summary>
public static class SessionDirectoryKeys
{
    public const int BucketCount = 64;
    public const int ReadySegmentSize = 100;
    public const string SummaryKey = "dir_summary";
    public const string ReadyMetaKey = "ready_meta";

    public static string BucketKey(int bucket) => $"dir_{bucket}";

    public static string BucketKeyFor(string sessionId) => BucketKey(BucketFor(sessionId));

    public static string ReadySegmentKey(long segment) => $"ready_{segment}";

    /// <summary>FNV-1a, as CatalogShards: string.GetHashCode is randomised per process.</summary>
    public static int BucketFor(string sessionId)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(sessionId))
        {
            hash = unchecked((hash ^ b) * prime);
        }

        return (int)(hash % BucketCount);
    }
}

/// <summary>
/// The directory and ReadyQueue over the actor's state manager. Every change copies the one bucket
/// or segment it touches and stages it; nothing here saves, so the actor's turn commits everything
/// it staged in its one save. The state manager caches what it has read, so each key goes to the
/// store at most once per activation.
///
/// A session leaves the ReadyQueue by having its entry's ReadySeq cleared: its item stays where it
/// is, stale, and is dropped when it reaches the head. Nothing is removed from the middle.
/// </summary>
public sealed class SessionDirectoryStore(IActorStateManager state)
{
    public async Task<SessionEntry?> GetEntryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var bucket = await GetBucketAsync(SessionDirectoryKeys.BucketFor(sessionId), cancellationToken);
        return bucket.TryGetValue(sessionId, out var entry) ? entry : null;
    }

    public async Task SetEntryAsync(string sessionId, SessionEntry entry)
    {
        int n = SessionDirectoryKeys.BucketFor(sessionId);
        var bucket = new Dictionary<string, SessionEntry>(await GetBucketAsync(n)) { [sessionId] = entry };
        await state.SetStateAsync(SessionDirectoryKeys.BucketKey(n), bucket);
    }

    public async Task RemoveEntryAsync(string sessionId)
    {
        int n = SessionDirectoryKeys.BucketFor(sessionId);
        var bucket = new Dictionary<string, SessionEntry>(await GetBucketAsync(n));
        if (bucket.Remove(sessionId))
        {
            await state.SetStateAsync(SessionDirectoryKeys.BucketKey(n), bucket);
        }
    }

    /// <summary>Every entry, in no particular order. Reads all buckets (cached after the first time).</summary>
    public async Task<List<KeyValuePair<string, SessionEntry>>> AllEntriesAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<KeyValuePair<string, SessionEntry>>();
        for (int n = 0; n < SessionDirectoryKeys.BucketCount; n++)
        {
            all.AddRange(await GetBucketAsync(n, cancellationToken));
        }
        return all;
    }

    /// <summary>Appends the session to the ReadyQueue's tail; returns its entry marked as queued there.</summary>
    public async Task<SessionEntry> EnqueueReadyAsync(string sessionId, SessionEntry entry)
    {
        var meta = await GetReadyMetaAsync();
        var tail = await GetSegmentAsync(meta.Tail);
        long seq = meta.NextSeq;
        if (tail.Count >= SessionDirectoryKeys.ReadySegmentSize)
        {
            meta = meta with { Tail = meta.Tail + 1 };
            tail = [];
        }

        await state.SetStateAsync(SessionDirectoryKeys.ReadySegmentKey(meta.Tail), new List<ReadyItem>(tail) { new(sessionId, seq) });
        await state.SetStateAsync(SessionDirectoryKeys.ReadyMetaKey, meta with { NextSeq = seq + 1 });
        return entry with { ReadySeq = seq };
    }

    /// <summary>The ReadyQueue's head item, live or stale; null when the queue is empty.</summary>
    public async Task<ReadyItem?> PeekReadyAsync()
    {
        var meta = await GetReadyMetaAsync();
        while (true)
        {
            var head = await GetSegmentAsync(meta.Head);
            if (head.Count > 0)
            {
                return head[0];
            }
            if (meta.Head >= meta.Tail)
            {
                return null;
            }

            // An emptied segment before the tail: move past it.
            await state.TryRemoveStateAsync(SessionDirectoryKeys.ReadySegmentKey(meta.Head));
            meta = meta with { Head = meta.Head + 1 };
            await state.SetStateAsync(SessionDirectoryKeys.ReadyMetaKey, meta);
        }
    }

    /// <summary>Removes the head item. Call after <see cref="PeekReadyAsync"/> returned one.</summary>
    public async Task PopReadyAsync()
    {
        var meta = await GetReadyMetaAsync();
        var head = await GetSegmentAsync(meta.Head);
        if (head.Count == 0)
        {
            return;
        }

        var rest = head.GetRange(1, head.Count - 1);
        if (rest.Count == 0 && meta.Head < meta.Tail)
        {
            await state.TryRemoveStateAsync(SessionDirectoryKeys.ReadySegmentKey(meta.Head));
            await state.SetStateAsync(SessionDirectoryKeys.ReadyMetaKey, meta with { Head = meta.Head + 1 });
        }
        else
        {
            await state.SetStateAsync(SessionDirectoryKeys.ReadySegmentKey(meta.Head), rest);
        }
    }

    public async Task<DirectorySummary> GetSummaryAsync()
    {
        var result = await state.TryGetStateAsync<DirectorySummary>(SessionDirectoryKeys.SummaryKey);
        return result.HasValue ? result.Value : new DirectorySummary();
    }

    public Task SetSummaryAsync(DirectorySummary summary) => state.SetStateAsync(SessionDirectoryKeys.SummaryKey, summary);


    private async Task<Dictionary<string, SessionEntry>> GetBucketAsync(int n, CancellationToken cancellationToken = default)
    {
        var result = await state.TryGetStateAsync<Dictionary<string, SessionEntry>>(SessionDirectoryKeys.BucketKey(n), cancellationToken);
        return result.HasValue ? result.Value : [];
    }

    private async Task<ReadyMeta> GetReadyMetaAsync()
    {
        var result = await state.TryGetStateAsync<ReadyMeta>(SessionDirectoryKeys.ReadyMetaKey);
        return result.HasValue ? result.Value : new ReadyMeta();
    }

    private async Task<List<ReadyItem>> GetSegmentAsync(long segment)
    {
        var result = await state.TryGetStateAsync<List<ReadyItem>>(SessionDirectoryKeys.ReadySegmentKey(segment));
        return result.HasValue ? result.Value : [];
    }
}
