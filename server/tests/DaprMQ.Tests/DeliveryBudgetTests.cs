using System.Diagnostics;
using Dapr;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// Server retries (proposals/readiness-and-retries.md, section 3): only inside an API request's
/// delivery budget, only for not-delivered failures, and never past the caller's deadline.
/// </summary>
public class DeliveryBudgetTests
{
    private static Exception NoHost() => new DaprApiException("failed to lookup actor: did not find address for actor 'QueueActor/q'");

    private static DeliveryBudget Budget(TimeSpan timeout, TimeSpan? minAttemptWindow = null, CancellationToken callerCancelled = default) =>
        new(DateTimeOffset.UtcNow + timeout, callerCancelled)
        {
            MinAttemptWindow = minAttemptWindow ?? TimeSpan.FromMilliseconds(20),
            InitialBackoff = TimeSpan.FromMilliseconds(5),
            MaxBackoff = TimeSpan.FromMilliseconds(20),
        };

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
    public async Task Budget_RetriesNotDelivered_UntilItSucceeds()
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
    public async Task Budget_StopsWhenTooLittleTimeIsLeftForAnotherAttempt_AndReportsNotDelivered()
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
    public async Task Budget_NeverRetriesUnknown()
    {
        var attempts = 0;
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromSeconds(5)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync<int>(_ => { attempts++; throw new HttpRequestException("reset"); }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.Unknown, ex.Outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Budget_AnAttemptCutOffByTheDeadline_IsUnknown()
    {
        var sw = Stopwatch.StartNew();
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromMilliseconds(200)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync<int>(async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.Unknown, ex.Outcome);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Budget_DeadlineAlreadyPassed_DoesntCallAndReportsNotDelivered()
    {
        var attempts = 0;
        using var _ = DeliveryBudget.Begin(Budget(TimeSpan.FromMilliseconds(-1)));

        var ex = await Assert.ThrowsAsync<ActorCallException>(() =>
            ActorCall.RunAsync(_ => { attempts++; return Task.FromResult(0); }, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.NotDelivered, ex.Outcome);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Budget_CallerGoingAway_IsCancellationNotAFailure()
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
