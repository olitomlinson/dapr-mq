using Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DaprMQ.Tests;

public class SegmentedQueueTests
{
    private const int SegmentSize = 100;

    private readonly Dictionary<string, object> _data = new();
    private readonly Mock<IActorStateManager> _state = new();
    private readonly SegmentedQueue<long> _queue;

    public SegmentedQueueTests()
    {
        _state.Setup(m => m.TryGetStateAsync<Queue<long>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                _data.TryGetValue(key, out var v) ? new ConditionalValue<Queue<long>>(true, (Queue<long>)v) : new ConditionalValue<Queue<long>>(false, null!));
        _state.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<Queue<long>>(), It.IsAny<CancellationToken>()))
            .Callback((string key, Queue<long> value, CancellationToken _) => _data[key] = value)
            .Returns(Task.CompletedTask);
        _state.Setup(m => m.RemoveStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string key, CancellationToken _) => _data.Remove(key))
            .Returns(Task.CompletedTask);
        _queue = new SegmentedQueue<long>(_state.Object, "queue_1", SegmentSize, NullLogger.Instance);
    }

    private static string Key(int segment) => $"queue_1_seg_{segment}";

    private async Task<QueueMetadata> AppendRangeAsync(QueueMetadata? range, IEnumerable<long> items)
    {
        foreach (var item in items)
        {
            range = await _queue.AppendAsync(range, item);
        }
        return range!;
    }

    private void Seed(int segment, IEnumerable<long> items) => _data[Key(segment)] = new Queue<long>(items);

    // --- Append ---

    [Fact]
    public async Task Append_ToNoRange_StartsSegmentZero()
    {
        var range = await _queue.AppendAsync(null, 7);

        Assert.Equal(new QueueMetadata { HeadSegment = 0, TailSegment = 0, Count = 1 }, range);
        Assert.Equal([7L], (Queue<long>)_data[Key(0)]);
    }

    [Fact]
    public async Task Append_PastAFullSegment_StartsTheNext()
    {
        var range = await AppendRangeAsync(null, Enumerable.Range(0, SegmentSize + 1).Select(i => (long)i));

        Assert.Equal(new QueueMetadata { HeadSegment = 0, TailSegment = 1, Count = SegmentSize + 1 }, range);
        Assert.Equal(SegmentSize, ((Queue<long>)_data[Key(0)]).Count);
        Assert.Equal([(long)SegmentSize], (Queue<long>)_data[Key(1)]);
    }

    [Fact]
    public async Task Append_KeepsTheOffloadedRange()
    {
        Seed(4, [1]);
        var range = await _queue.AppendAsync(new QueueMetadata { HeadSegment = 0, TailSegment = 4, Count = 401, HeadOffloadedSegment = 2, TailOffloadedSegment = 3 }, 2);

        Assert.Equal(2, range.HeadOffloadedSegment);
        Assert.Equal(3, range.TailOffloadedSegment);
        Assert.Equal(402, range.Count);
    }

    // --- PopHead ---

    [Fact]
    public async Task PopHead_ReturnsItemsInFifoOrder()
    {
        var range = await AppendRangeAsync(null, [1, 2, 3]);

        var first = await _queue.PopHeadAsync(range);

        Assert.Equal(1, first.Item);
        Assert.Equal(new QueueMetadata { HeadSegment = 0, TailSegment = 0, Count = 2 }, first.Range);
        Assert.False(first.Desync);
        Assert.Equal([2L, 3L], (Queue<long>)_data[Key(0)]);
    }

    [Fact]
    public async Task PopHead_EmptyingASegment_RemovesIt_AndMovesTheHeadOn()
    {
        var range = await AppendRangeAsync(null, Enumerable.Range(0, SegmentSize + 1).Select(i => (long)i));
        for (int i = 0; i < SegmentSize; i++)
        {
            range = (await _queue.PopHeadAsync(range)).Range!;
        }

        Assert.Equal(new QueueMetadata { HeadSegment = 1, TailSegment = 1, Count = 1 }, range);
        Assert.False(_data.ContainsKey(Key(0)));
    }

    [Fact]
    public async Task PopHead_LastItem_RemovesTheSegment_AndReturnsNoRange()
    {
        var range = await _queue.AppendAsync(null, 9);

        var popped = await _queue.PopHeadAsync(range);

        Assert.Equal(9, popped.Item);
        Assert.Null(popped.Range);
        Assert.False(popped.Desync);
        Assert.Empty(_data);
    }

    [Fact]
    public async Task PopHead_MissingHeadSegment_ReportsDesync()
    {
        var popped = await _queue.PopHeadAsync(new QueueMetadata { HeadSegment = 3, TailSegment = 3, Count = 5 });

        Assert.True(popped.Desync);
        Assert.Null(popped.Range);
    }

    // --- MergeAtHead ---

    [Fact]
    public async Task MergeAtHead_MergesByKeyIntoTheHeadSegment()
    {
        var range = await AppendRangeAsync(null, [2, 4, 6]);

        range = await _queue.MergeAtHeadAsync(range, [5, 1, 3], i => i);

        Assert.Equal(new QueueMetadata { HeadSegment = 0, TailSegment = 0, Count = 6 }, range);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], (Queue<long>)_data[Key(0)]);
    }

    [Fact]
    public async Task MergeAtHead_LargerThanASegment_SpillsBelowTheHead()
    {
        var range = await AppendRangeAsync(null, [1000]);

        range = await _queue.MergeAtHeadAsync(range, Enumerable.Range(0, SegmentSize).Select(i => (long)i).ToList(), i => i);

        Assert.Equal(new QueueMetadata { HeadSegment = -1, TailSegment = 0, Count = SegmentSize + 1 }, range);
        Assert.Equal(SegmentSize, ((Queue<long>)_data[Key(-1)]).Count);
        Assert.Equal([1000L], (Queue<long>)_data[Key(0)]);
    }

    [Fact]
    public async Task MergeAtHead_ToNoRange_StartsSegmentZero()
    {
        var range = await _queue.MergeAtHeadAsync(null, [3, 1], i => i);

        Assert.Equal(new QueueMetadata { HeadSegment = 0, TailSegment = 0, Count = 2 }, range);
        Assert.Equal([1L, 3L], (Queue<long>)_data[Key(0)]);
    }

    // --- Offload / load ---

    [Fact]
    public async Task Offload_UnloadsFullSegmentsBeyondTheBuffer()
    {
        var range = await AppendRangeAsync(null, Enumerable.Range(0, SegmentSize * 5).Select(i => (long)i));

        range = await _queue.OffloadAsync(range, bufferSegments: 1);

        // Head 0 plus one buffer segment stay loaded; 2 and 3 are offloaded; the tail (4) never is.
        Assert.Equal(2, range.HeadOffloadedSegment);
        Assert.Equal(3, range.TailOffloadedSegment);
        _state.Verify(m => m.UnloadStateAsync(Key(2), It.IsAny<UnloadStateOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(m => m.UnloadStateAsync(Key(3), It.IsAny<UnloadStateOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _state.Verify(m => m.UnloadStateAsync(It.IsIn(Key(0), Key(1), Key(4)), It.IsAny<UnloadStateOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Offload_SkipsSegmentsAlreadyOffloaded()
    {
        var range = await AppendRangeAsync(null, Enumerable.Range(0, SegmentSize * 5).Select(i => (long)i));
        range = await _queue.OffloadAsync(range, bufferSegments: 1);

        range = await _queue.OffloadAsync(range, bufferSegments: 1);

        Assert.Equal((2, 3), (range.HeadOffloadedSegment, range.TailOffloadedSegment));
        _state.Verify(m => m.UnloadStateAsync(Key(2), It.IsAny<UnloadStateOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Offload_AnUnloadThatFails_LeavesThatSegmentLoaded_AndDoesNotThrow()
    {
        var range = await AppendRangeAsync(null, Enumerable.Range(0, SegmentSize * 4).Select(i => (long)i));
        _state.Setup(m => m.UnloadStateAsync(Key(2), It.IsAny<UnloadStateOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("modified and not yet persisted"));

        range = await _queue.OffloadAsync(range, bufferSegments: 1);

        Assert.Null(range.HeadOffloadedSegment);
        Assert.Null(range.TailOffloadedSegment);
    }

    [Fact]
    public async Task Load_BringsBackOffloadedSegmentsWithinTheBuffer()
    {
        Seed(1, [1]);
        Seed(2, [2]);
        Seed(3, [3]);
        var range = new QueueMetadata { HeadSegment = 1, TailSegment = 4, Count = 4, HeadOffloadedSegment = 2, TailOffloadedSegment = 3 };

        range = await _queue.LoadAsync(range, bufferSegments: 1);

        // Head 1 + buffer 1 reaches segment 2; segment 3 stays offloaded.
        Assert.Equal((3, 3), (range.HeadOffloadedSegment, range.TailOffloadedSegment));
    }

    [Fact]
    public async Task Load_TheLastOffloadedSegment_ClearsTheRange()
    {
        Seed(2, [2]);
        var range = new QueueMetadata { HeadSegment = 1, TailSegment = 3, Count = 3, HeadOffloadedSegment = 2, TailOffloadedSegment = 2 };

        range = await _queue.LoadAsync(range, bufferSegments: 1);

        Assert.Null(range.HeadOffloadedSegment);
        Assert.Null(range.TailOffloadedSegment);
    }

    [Fact]
    public async Task Load_AMissingSegment_ThrowsWithTheSegmentAndTheRangeSoFar()
    {
        Seed(1, [1]);
        var range = new QueueMetadata { HeadSegment = 0, TailSegment = 3, Count = 3, HeadOffloadedSegment = 1, TailOffloadedSegment = 2 };

        var ex = await Assert.ThrowsAsync<SegmentMissingException>(() => _queue.LoadAsync(range, bufferSegments: 2));

        Assert.Equal(2, ex.Segment);
        Assert.Equal((2, 2), (ex.Range.HeadOffloadedSegment, ex.Range.TailOffloadedSegment));
    }

    // --- Staging ---

    [Fact]
    public async Task NoOperation_SavesState()
    {
        var range = await AppendRangeAsync(null, Enumerable.Range(0, SegmentSize * 4).Select(i => (long)i));
        range = await _queue.OffloadAsync(range, bufferSegments: 1);
        range = await _queue.LoadAsync(range with { HeadSegment = 2 }, bufferSegments: 1);
        range = await _queue.MergeAtHeadAsync(range, [-1], i => i);
        await _queue.PopHeadAsync(range);

        _state.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
