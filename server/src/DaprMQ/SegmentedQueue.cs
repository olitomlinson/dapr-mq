using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;

namespace DaprMQ;

/// <summary>
/// Thrown by <see cref="SegmentedQueue{T}.LoadAsync"/> when a segment recorded as offloaded is missing
/// or empty in the store. <see cref="Range"/> is the range as it stood when the load failed, so the
/// owner can record exactly where things went wrong.
/// </summary>
internal sealed class SegmentMissingException(int segment, QueueMetadata range)
    : Exception($"Segment {segment} missing or empty")
{
    public int Segment { get; } = segment;
    public QueueMetadata Range { get; } = range;
}

/// <summary>
/// A FIFO list stored as fixed-size segments under "{keyPrefix}_seg_{n}", items running from segment
/// HeadSegment to TailSegment. Shared by every actor that keeps a segmented list.
///
/// It stages and never saves, so the owning actor still commits its whole turn with one
/// SaveStateAsync. It doesn't own a metadata key either: the owner passes in the list's
/// <see cref="QueueMetadata"/> record and stages the one it gets back, which lets QueueActor keep
/// every priority's record inside its single metadata blob.
/// </summary>
internal sealed class SegmentedQueue<T>(IActorStateManager state, string keyPrefix, int segmentSize, ILogger logger)
{
    public string SegmentKey(int segment) => $"{keyPrefix}_seg_{segment}";

    /// <summary>Appends to the tail, starting a new segment when the tail is full.</summary>
    public async Task<QueueMetadata> AppendAsync(QueueMetadata? range, T item)
    {
        range ??= new QueueMetadata { HeadSegment = 0, TailSegment = 0, Count = 0 };
        int tailSegment = range.TailSegment;

        var segment = await state.TryGetStateAsync<Queue<T>>(SegmentKey(tailSegment));
        var segmentQueue = segment.HasValue ? segment.Value : new Queue<T>();

        if (segmentQueue.Count >= segmentSize)
        {
            tailSegment++;
            segmentQueue = new Queue<T>();
        }

        segmentQueue.Enqueue(item);
        await state.SetStateAsync(SegmentKey(tailSegment), segmentQueue);

        return range with { TailSegment = tailSegment, Count = range.Count + 1 };
    }

    /// <summary>
    /// Removes the head item. Range is null once the list is empty (its last segment is removed too),
    /// so the owner drops its record. Desync means the record claimed items the head segment doesn't
    /// have; the owner drops its record then as well.
    /// </summary>
    public async Task<(T? Item, QueueMetadata? Range, bool Desync)> PopHeadAsync(QueueMetadata range)
    {
        string segmentKey = SegmentKey(range.HeadSegment);
        var segment = await state.TryGetStateAsync<Queue<T>>(segmentKey);
        if (!segment.HasValue || segment.Value.Count == 0)
        {
            return (default, null, true);
        }

        var segmentQueue = segment.Value;
        var item = segmentQueue.Dequeue();

        if (segmentQueue.Count > 0)
        {
            await state.SetStateAsync(segmentKey, segmentQueue);
            return (item, range with { Count = range.Count - 1 }, false);
        }

        await state.RemoveStateAsync(segmentKey);
        if (range.HeadSegment < range.TailSegment)
        {
            return (item, range with { HeadSegment = range.HeadSegment + 1, Count = range.Count - 1 }, false);
        }

        return (item, null, false);
    }

    /// <summary>
    /// Merges items back in at the head, by <paramref name="key"/>. The head segment is already in key
    /// order, so this is a linear merge; the merged run is laid out ending on the original head slot so
    /// anything in later segments still follows it, spilling into HeadSegment-1, -2, ... A null range
    /// (a drained list) restores into segment 0.
    /// </summary>
    public async Task<QueueMetadata> MergeAtHeadAsync(QueueMetadata? range, IReadOnlyList<T> items, Func<T, long> key)
    {
        int headSegment = range?.HeadSegment ?? 0;
        int tailSegment = range?.TailSegment ?? headSegment - 1;
        int count = range?.Count ?? 0;

        var headState = await state.TryGetStateAsync<Queue<T>>(SegmentKey(headSegment));
        var headItems = headState.HasValue ? headState.Value.ToList() : new List<T>();

        var restored = items.OrderBy(key).ToList();
        var merged = new List<T>(restored.Count + headItems.Count);
        int r = 0, h = 0;
        while (r < restored.Count || h < headItems.Count)
        {
            merged.Add(h >= headItems.Count || (r < restored.Count && key(restored[r]) <= key(headItems[h]))
                ? restored[r++]
                : headItems[h++]);
        }

        int chunks = (merged.Count + segmentSize - 1) / segmentSize;
        int firstSegment = headSegment - (chunks - 1);

        for (int c = 0; c < chunks; c++)
        {
            await state.SetStateAsync(SegmentKey(firstSegment + c), new Queue<T>(merged.Skip(c * segmentSize).Take(segmentSize)));
        }

        return (range ?? new QueueMetadata()) with
        {
            HeadSegment = firstSegment,
            TailSegment = Math.Max(tailSegment, headSegment),
            Count = count + restored.Count
        };
    }

    /// <summary>
    /// Unloads full segments more than <paramref name="bufferSegments"/> past the head (never the tail)
    /// from the actor's memory; they stay in the store under the same key and are recorded in the
    /// offloaded range. Best-effort: a segment that can't be unloaded stays loaded, and nothing throws.
    /// </summary>
    public async Task<QueueMetadata> OffloadAsync(QueueMetadata range, int bufferSegments)
    {
        try
        {
            int? offloadedHead = range.HeadOffloadedSegment, offloadedTail = range.TailOffloadedSegment;

            for (int segmentNum = range.HeadSegment + bufferSegments + 1; segmentNum < range.TailSegment; segmentNum++)
            {
                if (offloadedHead != null && offloadedTail != null && segmentNum >= offloadedHead && segmentNum <= offloadedTail)
                {
                    continue;
                }

                string segmentKey = SegmentKey(segmentNum);
                var segment = await state.TryGetStateAsync<Queue<T>>(segmentKey);
                if (!segment.HasValue || segment.Value.Count != segmentSize)
                {
                    continue;
                }

                try
                {
                    var updated = range.HeadOffloadedSegment.HasValue
                        ? range with { TailOffloadedSegment = segmentNum }
                        : range with { HeadOffloadedSegment = segmentNum, TailOffloadedSegment = segmentNum };

                    // Stays in the permanent store at the same key.
                    await state.UnloadStateAsync(segmentKey);
                    range = updated;
                    logger.LogDebug("[OFFLOAD-SUCCESS] {SegmentKey}: unloaded from actor memory", segmentKey);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("[OFFLOAD-FAILED] {SegmentKey}: failed to offload - {Error}", segmentKey, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error checking/offloading segments of {KeyPrefix}", keyPrefix);
        }

        return range;
    }

    /// <summary>
    /// Loads back the offloaded segments now within <paramref name="bufferSegments"/> of the head, taking
    /// them off the front of the offloaded range. Throws <see cref="SegmentMissingException"/> if one is
    /// missing or empty: serving past it would lose items, so the owner must stop.
    /// </summary>
    public async Task<QueueMetadata> LoadAsync(QueueMetadata range, int bufferSegments)
    {
        if (range.HeadOffloadedSegment is not { } head || range.TailOffloadedSegment is not { } tail)
        {
            return range;
        }

        int maxOffloaded = range.HeadSegment + bufferSegments;

        for (int segmentNum = head; segmentNum <= tail && segmentNum <= maxOffloaded; segmentNum++)
        {
            // Dapr hydrates the segment from the store on this read.
            var segment = await state.TryGetStateAsync<Queue<T>>(SegmentKey(segmentNum));
            if (!segment.HasValue || segment.Value == null || segment.Value.Count == 0)
            {
                throw new SegmentMissingException(segmentNum, range);
            }

            if (segmentNum == range.HeadOffloadedSegment)
            {
                range = range.HeadOffloadedSegment == range.TailOffloadedSegment
                    ? range with { HeadOffloadedSegment = null, TailOffloadedSegment = null }
                    : range with { HeadOffloadedSegment = segmentNum + 1 };
            }

            logger.LogDebug("[LOAD-SUCCESS] {SegmentKey}: loaded {Count} items from the store", SegmentKey(segmentNum), segment.Value.Count);
        }

        return range;
    }
}
