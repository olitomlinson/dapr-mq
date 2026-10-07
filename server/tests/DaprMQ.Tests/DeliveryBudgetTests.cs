using System.Diagnostics;
using Dapr;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Server retries (proposals/readiness-and-retries.md, section 3): only inside an API request's
/// delivery budget, and two separate limits. The retry window bounds how long a not-delivered call is
/// repeated; a delivered call runs until the caller goes away, its explicit call deadline, or the
/// per-attempt safety limit - never cut off by the retry window.
/// </summary>
public class DeliveryBudgetTests
{
    private static Exception NoHost() => new DaprApiException("failed to lookup actor: did not find address for actor 'QueueActor/q'");

    private static DeliveryBudget Budget(
        TimeSpan retryWindow, TimeSpan? callDeadline = null, TimeSpan? maxAttempt = null,
        TimeSpan? minAttemptWindow = null, CancellationToken callerCancelled = default)
    {
        var now = DateTimeOffset.UtcNow;
        return new DeliveryBudget(now + retryWindow, callDeadline is { } d ? now + d : null, maxAttempt ?? TimeSpan.FromSeconds(5), callerCancelled)
        {
            MinAttemptWindow = minAttemptWindow ?? TimeSpan.FromMilliseconds(20),
            InitialBackoff = TimeSpan.FromMilliseconds(5),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
        };
    }

    [Fact]
    public async Task NoBudget_MakesOneAttempt()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync<int>(_ => { attempts++; throw NoHost(); }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.NotDelivered, ex.Outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetriesNotDelivered_UntilItSucceeds()
    {
        var attempts = 0;
        using (DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(5))))
        {
            var result = await ActorCall.RunAsync(_ => ++attempts < 3 ? throw NoHost() : Task.FromResult(42), CancellationToken.None);

            Assert.Equal(42, result);
        }

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task StopsRetrying_WhenTooLittleOfTheRetryWindowIsLeft_AndReportsNotDelivered()
    {
        var attempts = 0;
        var sw = Stopwatch.StartNew();
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromMilliseconds(300), minAttemptWindow: TimeSpan.FromMilliseconds(200)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync<int>(_ => { attempts++; throw NoHost(); }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.NotDelivered, ex.Outcome);
        Assert.True(attempts > 1, $"attempts={attempts}");
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(300), $"elapsed={sw.Elapsed}");
    }

    [Fact]
    public async Task ACallDeadline_AlsoLimitsRetries()
    {
        var attempts = 0;
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(30), callDeadline: TimeSpan.FromMilliseconds(300), minAttemptWindow: TimeSpan.FromMilliseconds(200)));

        await Assert.ThrowsAsync<ActorCallException>(() => ActorCall.RunAsync<int>(_ => { attempts++; throw NoHost(); }, CancellationToken.None));

        Assert.InRange(attempts, 1, 100);
    }

    [Fact]
    public async Task NeverRetriesUnknown()
    {
        var attempts = 0;
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(5)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync<int>(_ => { attempts++; throw new HttpRequestException("reset"); }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.Unknown, ex.Outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ASlowDeliveredCall_OutlivesTheRetryWindow()
    {
        // The actor is busy (e.g. thousands of calls queued on one queue): slow, but progressing.
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromMilliseconds(50)));

        var result = await ActorCall.RunAsync(async ct => { await Task.Delay(300, ct); return 42; }, CancellationToken.None);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task AnAttemptCutOffByTheCallersDeadline_IsUnknown()
    {
        var sw = Stopwatch.StartNew();
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(30), callDeadline: TimeSpan.FromMilliseconds(200)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync<int>(async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.Unknown, ex.Outcome);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AHungAttempt_IsCutOffByTheSafetyLimit_AsUnknown()
    {
        var sw = Stopwatch.StartNew();
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(30), maxAttempt: TimeSpan.FromMilliseconds(200)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync<int>(async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.Unknown, ex.Outcome);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallDeadlineAlreadyPassed_DoesntCallAndReportsNotDelivered()
    {
        var attempts = 0;
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(30), callDeadline: TimeSpan.FromMilliseconds(-1)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync(_ => { attempts++; return Task.FromResult(0); }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.NotDelivered, ex.Outcome);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task CallerGoingAway_IsCancellationNotAFailure()
    {
        using var caller = new CancellationTokenSource();
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(5), callerCancelled: caller.Token));
        caller.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ActorCall.RunAsync<int>(async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, CancellationToken.None));
    }

    [Fact]
    public async Task Budget_IsScopedToTheRequest()
    {
        using (DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(5))))
        {
            Assert.NotNull(DeliveryBudget.Current);
        }

        Assert.Null(DeliveryBudget.Current);
        await Task.CompletedTask;
    }
}
