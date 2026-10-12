using Dapr.Actors;
using Dapr.Actors.Runtime;
using DaprMQ.Interfaces;
using Moq;

namespace DaprMQ.Tests;

public class CatalogActorTests
{
    private readonly Dictionary<string, object> _state = new();
    private readonly Mock<IActorStateManager> _stateManager = new();

    public CatalogActorTests()
    {
        _stateManager.Setup(m => m.TryGetStateAsync<Dictionary<string, CatalogEntry>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => _state.TryGetValue(key, out var value)
                ? new ConditionalValue<Dictionary<string, CatalogEntry>>(true, (Dictionary<string, CatalogEntry>)value)
                : new ConditionalValue<Dictionary<string, CatalogEntry>>(false, null!));
        _stateManager.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, CatalogEntry>>(), It.IsAny<CancellationToken>()))
            .Returns((string key, Dictionary<string, CatalogEntry> value, CancellationToken _) =>
            {
                // Store a copy, as Dapr would serialize it, so later in-memory changes can't leak in.
                _state[key] = new Dictionary<string, CatalogEntry>(value);
                return Task.CompletedTask;
            });
        _stateManager.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private async Task<CatalogActor> ActivateAsync()
    {
        var host = ActorHost.CreateForTest<CatalogActor>(new ActorTestOptions { ActorId = new ActorId("catalog-0") });
        var actor = new CatalogActor(host);
        typeof(Actor).GetProperty("StateManager")!.SetValue(actor, _stateManager.Object);
        var onActivate = typeof(CatalogActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)onActivate.Invoke(actor, null)!;
        return actor;
    }

    private static CatalogEntry Entry(string id) => new() { Id = id, Kind = CatalogEntryKind.Queue };

    private Dictionary<string, CatalogEntry> Stored => (Dictionary<string, CatalogEntry>)_state[CatalogActor.EntriesKey];

    [Fact]
    public async Task RegisterBatch_NewIds_SavedOnce()
    {
        var actor = await ActivateAsync();

        var response = await actor.RegisterBatchAsync(new RegisterCatalogBatchRequest { Entries = [Entry("a"), Entry("b")] });

        Assert.Equal(2, response.Added);
        Assert.Equal(["a", "b"], Stored.Keys.Order());
        _stateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RegisterBatch_KnownIds_NoWrite()
    {
        var actor = await ActivateAsync();
        await actor.RegisterBatchAsync(new RegisterCatalogBatchRequest { Entries = [Entry("a")] });
        _stateManager.Invocations.Clear();

        var response = await actor.RegisterBatchAsync(new RegisterCatalogBatchRequest { Entries = [Entry("a")] });

        Assert.Equal(0, response.Added);
        _stateManager.Verify(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, CatalogEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
        _stateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterBatch_DuplicateWithinBatch_AddedOnce_FirstWins()
    {
        var actor = await ActivateAsync();
        var first = Entry("a") with { ParentId = "first" };

        var response = await actor.RegisterBatchAsync(new RegisterCatalogBatchRequest { Entries = [first, Entry("a") with { ParentId = "second" }] });

        Assert.Equal(1, response.Added);
        Assert.Equal(first, Stored["a"]);
    }

    [Fact]
    public async Task Activation_LoadsExistingEntries_SoTheyCostNoWrite()
    {
        _state[CatalogActor.EntriesKey] = new Dictionary<string, CatalogEntry> { ["a"] = Entry("a") };
        var actor = await ActivateAsync();

        var response = await actor.RegisterBatchAsync(new RegisterCatalogBatchRequest { Entries = [Entry("a"), Entry("b")] });

        Assert.Equal(1, response.Added);
        Assert.Equal(["a", "b"], Stored.Keys.Order());
    }

    [Fact]
    public async Task RegisterBatch_EmptyRequest_NoWrite()
    {
        var actor = await ActivateAsync();

        var response = await actor.RegisterBatchAsync(new RegisterCatalogBatchRequest());

        Assert.Equal(0, response.Added);
        _stateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterBatch_FailedSave_IdsStayUnknown()
    {
        var actor = await ActivateAsync();
        _stateManager.SetupSequence(m => m.SaveStateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store down"))
            .Returns(Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actor.RegisterBatchAsync(new RegisterCatalogBatchRequest { Entries = [Entry("a")] }));
        var retry = await actor.RegisterBatchAsync(new RegisterCatalogBatchRequest { Entries = [Entry("a")] });

        Assert.Equal(1, retry.Added);
    }
}
