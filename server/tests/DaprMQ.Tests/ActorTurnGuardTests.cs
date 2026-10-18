using DaprMQ.ApiServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DaprMQ.Tests;

/// <summary>
/// The temporary per-actor turn guard (dapr/dapr#10835): one turn at a time per actor, a call whose request
/// is aborted while it waits never runs, and a call in the same reentrancy chain may enter.
/// </summary>
public class ActorTurnGuardTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task SecondCallOnTheSameActor_WaitsUntilTheFirstReleases()
    {
        var guard = new ActorTurnGuard();
        var first = await guard.AcquireAsync("QueueActor/q", null, CancellationToken.None);

        var second = guard.AcquireAsync("QueueActor/q", null, CancellationToken.None);
        await Task.Delay(Short);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lease.Waited);
        Assert.False(first.Waited);
    }

    [Fact]
    public async Task CallsOnDifferentActors_DoNotWaitForEachOther()
    {
        var guard = new ActorTurnGuard();
        using var a = await guard.AcquireAsync("QueueActor/a", null, CancellationToken.None);

        using var b = await guard.AcquireAsync("QueueActor/b", null, CancellationToken.None).WaitAsync(Short);

        Assert.False(b.Waited);
    }

    [Fact]
    public async Task CallInTheSameReentrancyChain_EntersWhileTheChainHoldsTheTurn()
    {
        // A -> B -> A: the callback carries the chain's reentrancy id and must not deadlock behind it.
        var guard = new ActorTurnGuard();
        using var outer = await guard.AcquireAsync("QueueActor/q-session-s", "chain-1", CancellationToken.None);

        using var callback = await guard.AcquireAsync("QueueActor/q-session-s", "chain-1", CancellationToken.None).WaitAsync(Short);

        Assert.False(callback.Waited);
    }

    [Fact]
    public async Task CallFromAnotherReentrancyChain_Waits()
    {
        var guard = new ActorTurnGuard();
        var first = await guard.AcquireAsync("QueueActor/q", "chain-1", CancellationToken.None);

        var other = guard.AcquireAsync("QueueActor/q", "chain-2", CancellationToken.None);
        await Task.Delay(Short);
        Assert.False(other.IsCompleted);

        first.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task CallCancelledWhileWaiting_NeverEnters_AndTheActorStaysUsable()
    {
        // What dapr/dapr#10835 should do: a call its caller cancelled while queued is dropped, not dispatched.
        var guard = new ActorTurnGuard();
        var first = await guard.AcquireAsync("QueueActor/q", null, CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var cancelled = guard.AcquireAsync("QueueActor/q", null, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        first.Dispose();
        using var next = await guard.AcquireAsync("QueueActor/q", null, CancellationToken.None).WaitAsync(Short);
        Assert.False(next.Waited);
    }

    [Fact]
    public async Task IdleActors_LeaveNoEntryBehind()
    {
        var guard = new ActorTurnGuard();
        var first = await guard.AcquireAsync("QueueActor/q", null, CancellationToken.None);
        var second = guard.AcquireAsync("QueueActor/q", null, CancellationToken.None);
        first.Dispose();
        (await second).Dispose();

        Assert.Equal(0, guard.TrackedActors);
    }

    [Theory]
    [InlineData("/actors/QueueActor/q1/method/DequeueLocked", "QueueActor/q1")]
    [InlineData("/actors/QueueActor/q1/method/remind/reminder-1", "QueueActor/q1")]
    [InlineData("/actors/QueueActor/q1/method/timer/timer-1", "QueueActor/q1")]
    [InlineData("/actors/QueueActor/q1", "QueueActor/q1")] // DELETE: deactivation
    [InlineData("/dapr/config", null)]
    [InlineData("/healthz", null)]
    [InlineData("/queue/q1/enqueue", null)]
    public void ActorKey_IsTheTypeAndIdOfAnActorRequest(string path, string? expected)
    {
        Assert.Equal(expected, ActorTurnGuardMiddleware.ActorKeyOf(new PathString(path)));
    }

    [Fact]
    public async Task Middleware_RunsConcurrentRequestsToOneActorOneAtATime()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        int inProgress = 0, max = 0;
        app.UseActorTurnGuard(NullLogger.Instance);
        app.Run(async _ =>
        {
            var n = Interlocked.Increment(ref inProgress);
            InterlockedMax(ref max, n);
            await Task.Delay(50);
            Interlocked.Decrement(ref inProgress);
        });
        var pipeline = app.Build();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/actors/QueueActor/q1/method/DequeueLocked";
            return pipeline(context);
        }));

        Assert.Equal(1, max);
    }

    [Fact]
    public async Task Middleware_DropsARequestAbortedWhileItWaits()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        var release = new TaskCompletionSource();
        var ran = new List<string>();
        app.UseActorTurnGuard(NullLogger.Instance);
        app.Run(async context =>
        {
            lock (ran) { ran.Add(context.Request.Headers["x-call"]!); }
            if (context.Request.Headers["x-call"] == "first")
            {
                await release.Task;
            }
        });
        var pipeline = app.Build();

        var first = new DefaultHttpContext();
        first.Request.Path = "/actors/QueueActor/q1/method/Work";
        first.Request.Headers["x-call"] = "first";
        var running = pipeline(first);

        using var aborted = new CancellationTokenSource();
        var second = new DefaultHttpContext { RequestAborted = aborted.Token };
        second.Request.Path = "/actors/QueueActor/q1/method/Work";
        second.Request.Headers["x-call"] = "second";
        var waiting = pipeline(second);
        await Task.Delay(Short);
        aborted.Cancel();
        await waiting;

        release.SetResult();
        await running;
        Assert.Equal(["first"], ran);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
