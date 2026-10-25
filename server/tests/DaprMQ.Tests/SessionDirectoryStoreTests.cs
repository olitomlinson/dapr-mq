using Dapr.Actors.Runtime;
using Moq;

namespace DaprMQ.Tests;

public class SessionDirectoryStoreTests
{
    private readonly Dictionary<string, object> _data = new();
    private readonly Mock<IActorStateManager> _state = new();
    private readonly SessionDirectoryStore _store;

    public SessionDirectoryStoreTests()
    {
        SetupTryGet<Dictionary<string, SessionEntry>>();
        SetupTryGet<List<ReadyItem>>();
        SetupTryGet<ReadyMeta>();
        SetupTryGet<DirectorySummary>();
        _state.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(i => _data[(string)i.Arguments[0]] = i.Arguments[1]))
            .Returns(Task.CompletedTask);
        _state.Setup(m => m.TryRemoveStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => _data.Remove(key));
        _store = new SessionDirectoryStore(_state.Object);
    }

    private void SetupTryGet<T>() where T : class =>
        _state.Setup(m => m.TryGetStateAsync<T>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) =>
                _data.TryGetValue(key, out var v) && v is T value ? new ConditionalValue<T>(true, value) : new ConditionalValue<T>(false, null!));

    private List<string> KeysWritten() =>
        _state.Invocations.Where(i => i.Method.Name == nameof(IActorStateManager.SetStateAsync)).Select(i => (string)i.Arguments[0]).ToList();

    private async Task<List<string>> DrainLiveAsync()
    {
        var live = new List<string>();
        while (await _store.PeekReadyAsync() is { } item)
        {
            await _store.PopReadyAsync();
            if ((await _store.GetEntryAsync(item.SessionId))?.ReadySeq == item.Seq)
            {
                live.Add(item.SessionId);
            }
        }
        return live;
    }

    // --- Buckets ---

    [Fact]
    public async Task AnEntry_RoundTripsThroughItsBucket_AndWritesOnlyThatBucket()
    {
        await _store.SetEntryAsync("order-42", new SessionEntry { Active = true });

        Assert.True((await _store.GetEntryAsync("order-42"))!.Active);
        Assert.Equal([SessionDirectoryKeys.BucketKeyFor("order-42")], KeysWritten());
        Assert.Null(await _store.GetEntryAsync("unknown"));
    }

    [Fact]
    public async Task RemoveEntry_DropsIt_AndAMissingEntryWritesNothing()
    {
        await _store.SetEntryAsync("a", new SessionEntry());
        _state.Invocations.Clear();

        await _store.RemoveEntryAsync("a");
        await _store.RemoveEntryAsync("never-there");

        Assert.Null(await _store.GetEntryAsync("a"));
        Assert.Single(KeysWritten());
    }

    [Fact]
    public async Task AllEntries_SpansEveryBucket()
    {
        var ids = Enumerable.Range(0, 300).Select(i => $"s{i}").ToList();
        foreach (var id in ids)
        {
            await _store.SetEntryAsync(id, new SessionEntry());
        }

        var all = await _store.AllEntriesAsync();

        Assert.Equal(ids.Order(), all.Select(e => e.Key).Order());
        Assert.True(_data.Keys.Count(k => k.StartsWith("dir_")) > 32, "300 ids should spread over most of the 64 buckets");
    }

    [Fact]
    public void BucketFor_IsStableAndInRange()
    {
        Assert.Equal(SessionDirectoryKeys.BucketFor("order-42"), SessionDirectoryKeys.BucketFor("order-42"));
        Assert.All(Enumerable.Range(0, 1000), i => Assert.InRange(SessionDirectoryKeys.BucketFor($"s{i}"), 0, 63));
    }

    // --- The ReadyQueue ---

    [Fact]
    public async Task EnqueueReady_FillsASegment_ThenStartsTheNext()
    {
        for (int i = 0; i < 150; i++)
        {
            var entry = await _store.EnqueueReadyAsync($"s{i}", new SessionEntry { Active = true });
            await _store.SetEntryAsync($"s{i}", entry);
        }

        Assert.Equal(100, ((List<ReadyItem>)_data[SessionDirectoryKeys.ReadySegmentKey(0)]).Count);
        Assert.Equal(50, ((List<ReadyItem>)_data[SessionDirectoryKeys.ReadySegmentKey(1)]).Count);
        Assert.Equal(Enumerable.Range(0, 150).Select(i => $"s{i}"), await DrainLiveAsync());
    }

    [Fact]
    public async Task EnqueueReady_StagesOnlyTheTailAndTheMeta()
    {
        await _store.EnqueueReadyAsync("a", new SessionEntry());

        Assert.Equal([SessionDirectoryKeys.ReadySegmentKey(0), SessionDirectoryKeys.ReadyMetaKey], KeysWritten());
    }

    [Fact]
    public async Task PopReady_PastAFullSegment_RemovesIt_AndMovesTheHeadOn()
    {
        for (int i = 0; i < 101; i++)
        {
            await _store.EnqueueReadyAsync($"s{i}", new SessionEntry());
        }

        for (int i = 0; i < 100; i++)
        {
            await _store.PopReadyAsync();
        }

        Assert.False(_data.ContainsKey(SessionDirectoryKeys.ReadySegmentKey(0)));
        Assert.Equal("s100", (await _store.PeekReadyAsync())!.SessionId);
        Assert.Equal(1, ((ReadyMeta)_data[SessionDirectoryKeys.ReadyMetaKey]).Head);
    }

    [Fact]
    public async Task PeekReady_EmptyQueue_ReturnsNull()
    {
        Assert.Null(await _store.PeekReadyAsync());

        await _store.EnqueueReadyAsync("a", new SessionEntry());
        await _store.PopReadyAsync();

        Assert.Null(await _store.PeekReadyAsync());
    }

    [Fact]
    public async Task ARequeuedSession_HasOneLiveItem_TheNewest()
    {
        // Leaving the queue only clears ReadySeq; the old item stays, stale.
        var first = await _store.EnqueueReadyAsync("a", new SessionEntry { Active = true });
        await _store.SetEntryAsync("a", first with { ReadySeq = null });
        await _store.SetEntryAsync("b", await _store.EnqueueReadyAsync("b", new SessionEntry { Active = true }));
        await _store.SetEntryAsync("a", await _store.EnqueueReadyAsync("a", first with { ReadySeq = null }));

        Assert.Equal(["b", "a"], await DrainLiveAsync());
    }
}
