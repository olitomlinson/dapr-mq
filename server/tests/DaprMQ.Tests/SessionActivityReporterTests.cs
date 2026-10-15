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
    private readonly SessionActivityReporter _reporter;

    // Sessions the fake coordinator defers, and whether calls fail.
    private readonly HashSet<string> _defer = [];
    private bool _fail;

    public SessionActivityReporterTests()
    {
        _invoker.Setup(i => i.InvokeMethodAsync<ReportSessionChangesRequest, ReportSessionChangesResponse>(
                It.IsAny<ActorId>(), ActorMethodNames.ReportSessionChanges, It.IsAny<ReportSessionChangesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActorId id, string _, ReportSessionChangesRequest request, CancellationToken _) =>
            {
                lock (_calls)
                {
                    _calls.Add((id.GetId(), request.Changes));
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
        _reporter = new SessionActivityReporter(_invoker.Object, new SessionDirectoryConfig(), _time, NullLogger<SessionActivityReporter>.Instance);
    }

    private async Task<List<(string QueueId, List<SessionChange> Changes)>> FlushAsync()
    {
        _calls.Clear();
        await _reporter.FlushAsync(CancellationToken.None);
        return [.. _calls];
    }

    private static (string, bool)[] Flat(List<(string QueueId, List<SessionChange> Changes)> calls) =>
        calls.SelectMany(c => c.Changes.Select(ch => (ch.SessionId, ch.HasWork))).OrderBy(c => c.SessionId).ToArray();

    [Fact]
    public async Task Active_IsSentOnTheNextTick()
    {
        _reporter.Observe("orders", "a", hasWork: true);

        var calls = await FlushAsync();

        var call = Assert.Single(calls);
        Assert.Equal("orders", call.QueueId);
        Assert.Equal([("a", true)], Flat(calls));
    }

    [Fact]
    public async Task OneCallPerQueue()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        _reporter.Observe("orders", "b", hasWork: true);
        _reporter.Observe("payments", "c", hasWork: true);

        var calls = await FlushAsync();

        Assert.Equal(2, calls.Count);
        Assert.Equal(2, calls.Single(c => c.QueueId == "orders").Changes.Count);
    }

    [Fact]
    public async Task UnchangedState_IsNotSentAgain_AndAQuietTickMakesNoCalls()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();
        _reporter.Observe("orders", "a", hasWork: true);

        Assert.Empty(await FlushAsync());
    }

    [Fact]
    public async Task Empty_WaitsForTheDebounce()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();
        _reporter.Observe("orders", "a", hasWork: false);

        _time.Advance(TimeSpan.FromSeconds(10));
        var early = await FlushAsync();
        _time.Advance(TimeSpan.FromSeconds(21));
        var settled = await FlushAsync();

        Assert.Empty(early);
        Assert.Equal([("a", false)], Flat(settled));
    }

    [Fact]
    public async Task FlappingThatEndsWithWork_SendsNothing()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();
        for (int i = 0; i < 50; i++)
        {
            _reporter.Observe("orders", "a", hasWork: false);
            _time.Advance(TimeSpan.FromMilliseconds(100));
            _reporter.Observe("orders", "a", hasWork: true);
        }

        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Empty(await FlushAsync());
    }

    [Fact]
    public async Task SessionFirstSeenEmpty_SendsEmptyAfterTheDebounce()
    {
        // LastSent is unknown, so the coordinator is told - this corrects a stale Active.
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal([("a", false)], Flat(await FlushAsync()));
    }

    [Fact]
    public async Task Deactivating_SendsEmptyWithoutWaiting_ThenForgetsTheSession()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();
        _reporter.Observe("orders", "a", hasWork: false, deactivating: true);

        var final = await FlushAsync();
        _time.Advance(TimeSpan.FromMinutes(5));
        var after = await FlushAsync();

        Assert.Equal([("a", false)], Flat(final));
        Assert.Empty(after);
    }

    [Fact]
    public async Task Deactivating_AlreadyReported_IsForgottenWithoutACall()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();
        _reporter.Observe("orders", "a", hasWork: true, deactivating: true);

        Assert.Empty(await FlushAsync());
        // Forgotten: a later empty observation for a fresh activation starts from unknown.
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal([("a", false)], Flat(await FlushAsync()));
    }

    [Fact]
    public async Task MarkReported_SuppressesTheDuplicateActive()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        _reporter.MarkReported("orders", "a", hasWork: true);

        Assert.Empty(await FlushAsync());
    }

    [Fact]
    public async Task MarkReported_Empty_MakesALaterRevivalBeSent()
    {
        // A release told the coordinator the session is empty; new work within the debounce
        // must still go out as Active.
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();
        _reporter.Observe("orders", "a", hasWork: false);
        _reporter.MarkReported("orders", "a", hasWork: false);
        _reporter.Observe("orders", "a", hasWork: true);

        Assert.Equal([("a", true)], Flat(await FlushAsync()));
    }

    [Fact]
    public async Task ResetReported_SendsTheCurrentStateAgain()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();

        _reporter.ResetReported("orders", "a");

        Assert.Equal([("a", true)], Flat(await FlushAsync()));
    }

    [Fact]
    public async Task FailedCall_NextTickSendsCurrentState_NotTheFailedBatch()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        _fail = true;
        await FlushAsync();
        _fail = false;
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));

        // The coordinator was never told Active, so the current state (Empty) is what goes.
        Assert.Equal([("a", false)], Flat(await FlushAsync()));
    }

    [Fact]
    public async Task Deferred_IsSentAgainNextTick_UntilAccepted()
    {
        _reporter.Observe("orders", "a", hasWork: true);
        await FlushAsync();
        _reporter.Observe("orders", "a", hasWork: false);
        _time.Advance(TimeSpan.FromSeconds(31));
        _defer.Add("a");

        var deferred = await FlushAsync();
        _defer.Clear();
        var retried = await FlushAsync();
        var done = await FlushAsync();

        Assert.Equal([("a", false)], Flat(deferred));
        Assert.Equal([("a", false)], Flat(retried));
        Assert.Empty(done);
    }
}
