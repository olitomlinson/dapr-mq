using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class SessionActivityReporterTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    private readonly Mock<ISessionCoordinatorActorInvoker> _invoker = new();
    private readonly List<(string QueueId, List<SessionChange> Changes)> _calls = [];
    private SessionActivityReporter _reporter;

    // Sessions the fake coordinator defers, whether calls fail, and a gate that holds calls in flight.
    private readonly HashSet<string> _defer = [];
    private bool _fail;
    private TaskCompletionSource? _hold;

    public SessionActivityReporterTests()
    {
        _invoker.Setup(i => i.InvokeMethodAsync<ReportSessionChangesRequest, ReportSessionChangesResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.ReportSessionChanges, It.IsAny<ReportSessionChangesRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (ActorId id, string _, ReportSessionChangesRequest request, CancellationToken _) =>
            {
                lock (_calls)
                {
                    _calls.Add((id.GetId(), request.Changes));
                }
                if (_hold is { } hold)
                {
                    await hold.Task;
                }
                if (_fail)
                {
                    throw new TimeoutException("coordinator unreachable");
                }
                return new ReportSessionChangesResponse
                {
                    Success = true,
                    Deferred = request.Changes.Where(c => _defer.Contains(c.SessionId)).Select(c => c.SessionId).ToList()
                };
            });
        _reporter = Create(new SessionDirectoryConfig());
    }

    private SessionActivityReporter Create(SessionDirectoryConfig config) =>
        new(_invoker.Object, config, _time, NullLogger<SessionActivityReporter>.Instance);

    private List<(string QueueId, List<SessionChange> Changes)> Take()
    {
        lock (_calls)
        {
            var taken = _calls.ToList();
            _calls.Clear();
            return taken;
        }
    }

    /// <summary>Calls made without a tick, once every send has finished.</summary>
    private async Task<List<(string QueueId, List<SessionChange> Changes)>> SentAsync()
    {
        await _reporter.WhenIdleAsync();
        return Take();
    }

    /// <summary>Calls made by one tick (anything sent before it is discarded first).</summary>
    private async Task<List<(string QueueId, List<SessionChange> Changes)>> TickAsync()
    {
        await _reporter.WhenIdleAsync();
        Take();
        await _reporter.FlushAsync(CancellationToken.None);
        await _reporter.WhenIdleAsync();
        return Take();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout");
            }
            await Task.Delay(5);
        }
    }

    private int CallCount()
    {
        lock (_calls)
        {
            return _calls.Count;
        }
    }

    private static (string, bool)[] Flat(List<(string QueueId, List<SessionChange> Changes)> calls) =>
        calls.SelectMany(c => c.Changes.Select(ch => (ch.SessionId, ch.HasWork))).OrderBy(c => c.SessionId).ToArray();

    // --- Active: sent straight away, one call in flight per queue ---

    [Fact]
    public async Task Active_IsSentStraightAway_WithoutATick()
    {
        _reporter.Observe("orders", "a", hasWork: true);

        var calls = await SentAsync();

        var call = Assert.Single(calls);
        Assert.Equal("orders", call.QueueId);
        Assert.Equal([("a", true)], Flat(calls));
        Assert.Empty(await TickAsync());
    }

    [Fact]
    public async Task WhileACallIsInFlight_LaterSessionsGoTogetherInTheNextCall()
    {
        _hold = new TaskCompletionSource();
        _reporter.Observe("orders", "a", hasWork: true);
        await WaitUntilAsync(() => CallCount() == 1);

        _reporter.Observe("orders", "b", hasWork: true);
        _reporter.Observe("orders", "c", hasWork: true);
        await Task.Delay(50);
        Assert.Equal(1, CallCount());

        _hold.SetResult();
        var calls = await SentAsync();

        Assert.Equal(2, calls.Count);
        Assert.Equal([("a", true)], Flat([calls[0]]));
        Assert.Equal([("b", true), ("c", true)], Flat([calls[1]]));
    }

    [Fact]
    public async Task EachQueueHasItsOwnSender()
    {
        _hold = new TaskCompletionSource();
        _reporter.Observe("orders", "a", hasWork: true);
        await WaitUntilAsync(() => CallCount() == 1);

        _reporter.Observe("payments", "x", hasWork: true);
        await WaitUntilAsync(() => CallCount() == 2);
        _hold.SetResult();

        var calls = await SentAsync();
        Assert.Equal(["orders", "payments"], calls.Select(c => c.QueueId).Order());
    }

    [Fact]
    public async Task ABacklogOverTheBatchCap_GoesInConsecutiveCalls()
    {
        _reporter = Create(new SessionDirectoryConfig { ReportBatchMax = 2 });
        _hold = new TaskCompletionSource();
        _reporter.Observe("orders", "a", hasWork: true);
        await WaitUntilAsync(() => CallCount() == 1);
        foreach (var id in new[] { "b", "c", "d", "e", "f" })
        {
            _reporter.Observe("orders", id, hasWork: true);
        }

        _hold.SetResult();
        var calls = await SentAsync();

        Assert.Equal([1, 2, 2, 1], calls.Select(c => c.Changes.Count));
        Assert.Equal(6, Flat(calls).Distinct().Count());
    }

    [Fact]
    public async Task FailedSend_RetriesWithBackoff_NotOnTheTick()
    {
        _fail = true;
        _reporter.Observe("orders", "a", hasWork: true);
        await WaitUntilAsync(() => CallCount() == 1);
        await Task.Delay(50); // let the sender start its backoff timer before the clock moves

        _time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => CallCount() == 2);
        _fail = false;
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromMilliseconds(199));
        await Task.Delay(50);
        Assert.Equal(2, CallCount()); // the second wait is 200ms

        _time.Advance(TimeSpan.FromMilliseconds(1));
        var calls = await SentAsync();
        Assert.Equal(3, calls.Count);
        Assert.Empty(await TickAsync());
    }

    [Fact]
    public async Task UnchangedState_IsNotSentAgain_AndAQuietTickMakesNoCalls()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        _reporter.Observe("orders", "a", hasWork: true);

        Assert.Empty(await SentAsync());
        Assert.Empty(await TickAsync());
    }

    // --- Empty: still debounced, and sent on the tick ---

    [Fact]
    public async Task Empty_WaitsForTheDebounce_AndGoesOnTheTick()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        _reporter.Observe("orders", "a", hasWork: false);

        Assert.Empty(await SentAsync());
        _time.Advance(TimeSpan.FromSeconds(10));
        var early = await TickAsync();
        _time.Advance(TimeSpan.FromSeconds(21));
        var settled = await TickAsync();

        Assert.Empty(early);
        Assert.Equal([("a", false)], Flat(settled));
    }

    [Fact]
    public async Task FlappingThatEndsWithWork_SendsNothing()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        for (int i = 0; i < 50; i++)
        {
            _reporter.Observe("orders", "a", hasWork: false);
            _time.Advance(TimeSpan.FromMilliseconds(100));
            _reporter.Observe("orders", "a", hasWork: true);
        }

        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Empty(await SentAsync());
        Assert.Empty(await TickAsync());
    }

    [Fact]
    public async Task SessionFirstSeenEmpty_SendsEmptyAfterTheDebounce()
    {
        // LastSent is unknown, so the coordinator is told - this corrects a stale Active.
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal([("a", false)], Flat(await TickAsync()));
    }

    [Fact]
    public async Task Deactivating_SendsEmptyOnTheNextTick_ThenForgetsTheSession()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        _reporter.Observe("orders", "a", hasWork: false);
        _reporter.MarkDeactivating("orders", "a");

        var final = await TickAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        var after = await TickAsync();

        Assert.Equal([("a", false)], Flat(final));
        Assert.Empty(after);
    }

    [Fact]
    public async Task Deactivating_AlreadyReported_IsForgottenWithoutACall()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        _reporter.MarkDeactivating("orders", "a");

        Assert.Empty(await TickAsync());
        // Forgotten: a later empty observation for a fresh activation starts from unknown.
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal([("a", false)], Flat(await TickAsync()));
    }

    [Fact]
    public async Task Deactivating_UnknownSession_TracksNothing()
    {
        // Never observed on this worker: nothing to report early and nothing to forget.
        _reporter.MarkDeactivating("orders", "a");
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Empty(await TickAsync());
    }

    // --- What the coordinator already knows ---

    [Fact]
    public async Task ObservedAsReported_IsNotSent()
    {
        // A lease end: the coordinator decided this state itself.
        _reporter.Observe("orders", "a", hasWork: true, reported: true);

        Assert.Empty(await SentAsync());
        Assert.Empty(await TickAsync());
    }

    [Fact]
    public async Task ReportedEmpty_MakesALaterRevivalBeSentStraightAway()
    {
        // A release told the coordinator the session is empty; new work within the debounce
        // must still go out as Active.
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        _reporter.Observe("orders", "a", hasWork: false, reported: true);
        _reporter.Observe("orders", "a", hasWork: true);

        Assert.Equal([("a", true)], Flat(await SentAsync()));
    }

    [Fact]
    public async Task ResetReported_SendsTheCurrentStateAgain_StraightAwayWhenItHasWork()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();

        _reporter.ResetReported("orders", "a");

        Assert.Equal([("a", true)], Flat(await SentAsync()));
    }

    [Fact]
    public async Task FailedCall_TheNextSendCarriesTheCurrentState_NotTheFailedBatch()
    {
        _fail = true;
        _reporter.Observe("orders", "a", hasWork: true);
        await WaitUntilAsync(() => CallCount() == 1);
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));
        _fail = false;
        await _reporter.WhenIdleAsync();
        Take();

        // The coordinator was never told Active, so the current state (Empty) is what goes.
        Assert.Equal([("a", false)], Flat(await TickAsync()));
    }

    [Fact]
    public async Task Deferred_IsSentAgainNextTick_UntilAccepted()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));
        _defer.Add("a");

        var deferred = await TickAsync();
        Assert.Empty(await SentAsync()); // not resent until the next tick
        _defer.Clear();
        var retried = await TickAsync();
        var done = await TickAsync();

        Assert.Equal([("a", false)], Flat(deferred));
        Assert.Equal([("a", false)], Flat(retried));
        Assert.Empty(done);
    }

    // --- Shutdown ---

    [Fact]
    public async Task Stop_SendsEverythingPending_EmptyIncluded()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await SentAsync();
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31)); // due, but the tick hasn't run

        await _reporter.StopAsync(CancellationToken.None);

        Assert.Equal([("a", false)], Flat(Take()));
    }

    [Fact]
    public async Task Stop_GivesUpWhenTheHostStopsWaiting()
    {
        _hold = new TaskCompletionSource(); // the coordinator never answers
        _reporter.Observe("orders", "a", hasWork: true);
        await WaitUntilAsync(() => CallCount() == 1);
        using var hostGivesUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var stop = _reporter.StopAsync(hostGivesUp.Token);

        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(3))));
        _hold.SetResult();
    }
}
