using Dapr.Actors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

public class SessionReleaseHandoffTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly Mock<ISessionCoordinatorActorInvoker> _invoker = new();

    public SessionReleaseHandoffTests()
    {
        _invoker.Setup(i => i.InvokeMethodAsync<ReleaseSessionRequest, ReleaseSessionResponse>(
                It.IsAny<ActorId>(), "ReleaseSession", It.IsAny<ReleaseSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseSessionResponse { Success = true });
    }

    private SessionReleaseHandoff Create(int windowMs = 500) =>
        new(_invoker.Object, TimeSpan.FromMilliseconds(windowMs), _time, NullLogger<SessionReleaseHandoff>.Instance);

    private void VerifyReleased(string queueId, string sessionId, string leaseId, Func<Times> times) =>
        _invoker.Verify(i => i.InvokeMethodAsync<ReleaseSessionRequest, ReleaseSessionResponse>(
            It.Is<ActorId>(a => a.GetId() == queueId), "ReleaseSession",
            It.Is<ReleaseSessionRequest>(r => r.SessionId == sessionId && r.LeaseId == leaseId), It.IsAny<CancellationToken>()), times);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout");
            }
            await Task.Delay(10);
        }
    }

    [Fact]
    public void TryTake_ReturnsTheParkedLease_Once()
    {
        var handoff = Create();
        Assert.True(handoff.Park("orders", "s1", "lease-1"));

        Assert.Equal(new SessionReleaseHandoff.ParkedRelease("orders", "s1", "lease-1"), handoff.TryTake("orders"));
        Assert.Null(handoff.TryTake("orders"));
    }

    [Fact]
    public void TryTake_OtherQueue_ReturnsNull()
    {
        var handoff = Create();
        handoff.Park("orders", "s1", "lease-1");

        Assert.Null(handoff.TryTake("payments"));
    }

    [Fact]
    public void TryTake_OldestFirst()
    {
        var handoff = Create();
        handoff.Park("orders", "s1", "lease-1");
        handoff.Park("orders", "s2", "lease-2");

        Assert.Equal("s1", handoff.TryTake("orders")!.SessionId);
        Assert.Equal("s2", handoff.TryTake("orders")!.SessionId);
    }

    [Fact]
    public async Task NotTakenWithinTheWindow_ReleasesItself()
    {
        var handoff = Create(windowMs: 500);
        handoff.Park("orders", "s1", "lease-1");

        _time.Advance(TimeSpan.FromMilliseconds(499));
        VerifyReleased("orders", "s1", "lease-1", Times.Never);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitUntilAsync(() => _invoker.Invocations.Count == 1);
        VerifyReleased("orders", "s1", "lease-1", Times.Once);
        Assert.Null(handoff.TryTake("orders"));
    }

    [Fact]
    public void Taken_IsNeverReleasedByTheWindow()
    {
        var handoff = Create(windowMs: 500);
        handoff.Park("orders", "s1", "lease-1");
        handoff.TryTake("orders");

        _time.Advance(TimeSpan.FromSeconds(5));

        VerifyReleased("orders", "s1", "lease-1", Times.Never);
    }

    [Fact]
    public async Task Stop_ReleasesEverythingParked()
    {
        var handoff = Create();
        handoff.Park("orders", "s1", "lease-1");
        handoff.Park("payments", "s2", "lease-2");

        await handoff.StopAsync(CancellationToken.None);

        VerifyReleased("orders", "s1", "lease-1", Times.Once);
        VerifyReleased("payments", "s2", "lease-2", Times.Once);
        Assert.Null(handoff.TryTake("orders"));
    }

    [Fact]
    public void ZeroWindow_ParksNothing()
    {
        var handoff = Create(windowMs: 0);

        Assert.False(handoff.Park("orders", "s1", "lease-1"));
        Assert.Null(handoff.TryTake("orders"));
    }
}
