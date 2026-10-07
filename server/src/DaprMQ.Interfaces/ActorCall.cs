using Dapr;
using Dapr.Actors;

namespace DaprMQ.Interfaces;

/// <summary>Whether a failed actor call could have reached the actor (proposals/readiness-and-retries.md, section 2).</summary>
public enum DeliveryOutcome
{
    /// <summary>The call certainly never reached an actor, so it had no effect. Safe to repeat.</summary>
    NotDelivered,

    /// <summary>The call may have reached the actor and run. Only idempotent operations may repeat it.</summary>
    Unknown,
}

/// <summary>An actor call that failed in transport or in the Dapr runtime, not in the actor method itself.</summary>
public class ActorCallException(DeliveryOutcome outcome, Exception inner)
    : Exception($"Actor call failed ({outcome}): {inner.Message}", inner)
{
    public DeliveryOutcome Outcome { get; } = outcome;
}

/// <summary>
/// Classifies actor-call failures from the phase-0 catalogue of observed daprd 1.18 behaviour.
/// Only a failure proven never to have reached an actor is <see cref="DeliveryOutcome.NotDelivered"/>;
/// anything else from the transport or runtime is <see cref="DeliveryOutcome.Unknown"/>.
/// </summary>
public static class ActorCallClassifier
{
    /// <returns>The delivery outcome, or null when the exception isn't a delivery failure (the actor
    /// method ran and threw, or it's a bug), so it should propagate unchanged.</returns>
    public static DeliveryOutcome? Classify(Exception exception) => exception switch
    {
        // The actor ran: its own failure, not delivery's.
        ActorMethodInvocationException or ActorInvokeException => null,

        // daprd found no host for the actor type (after its own 5 s lookup wait), so nothing was sent.
        DaprApiException e when e.Message.Contains("did not find address for actor") || e.Message.Contains("failed to lookup actor")
            => DeliveryOutcome.NotDelivered,

        // The target sidecar refused the call because placement had moved the actor elsewhere
        // (seen while Kubernetes workers start); daprd's own retries ran out before it ran anywhere.
        DaprApiException e when e.Message.Contains("remote actor moved") => DeliveryOutcome.NotDelivered,

        // Our own sidecar refused the connection (e.g. a gateway restarting), so nothing was sent.
        HttpRequestException { InnerException: System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused } }
            => DeliveryOutcome.NotDelivered,

        // Any other daprd error, a broken connection or the actor client's timeout: it may have run.
        DaprApiException or HttpRequestException or IOException or OperationCanceledException => DeliveryOutcome.Unknown,

        _ => null,
    };
}

/// <summary>
/// The limits an API request puts on its actor calls, set per API request (never inside an actor
/// turn); only then does <see cref="ActorCall"/> retry. Two separate limits
/// (proposals/readiness-and-retries.md, section 3):
/// <list type="bullet">
/// <item><see cref="RetryDeadline"/>: how long a call that certainly wasn't delivered may be repeated.</item>
/// <item>A delivered call runs until the caller goes away, its explicit <see cref="CallDeadline"/>, or
/// <see cref="MaxAttempt"/> (a safety limit for hung workers) - never cut off by the retry window, so a
/// slow but progressing call (e.g. queued behind a busy actor) isn't turned into an unknown outcome.</item>
/// </list>
/// </summary>
public sealed class DeliveryBudget(DateTimeOffset retryDeadline, DateTimeOffset? callDeadline, TimeSpan maxAttempt, CancellationToken callerCancelled)
{
    private static readonly AsyncLocal<DeliveryBudget?> CurrentBudget = new();

    public static DeliveryBudget? Current => CurrentBudget.Value;

    /// <summary>No new attempt of an undelivered call starts after this (less <see cref="MinAttemptWindow"/>).</summary>
    public DateTimeOffset RetryDeadline { get; } = retryDeadline;

    /// <summary>The caller's own deadline, if it gave one: bounds every attempt and every retry.</summary>
    public DateTimeOffset? CallDeadline { get; } = callDeadline;

    /// <summary>Longest a single attempt may run when the caller gave no tighter deadline.</summary>
    public TimeSpan MaxAttempt { get; } = maxAttempt;

    public CancellationToken CallerCancelled { get; } = callerCancelled;

    /// <summary>
    /// No new attempt starts with less retry time than this: daprd takes ~5 s to report "no host", so
    /// starting later only reports not-delivered later.
    /// </summary>
    public TimeSpan MinAttemptWindow { get; init; } = TimeSpan.FromSeconds(6);

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(2);

    internal TimeSpan RetryRemaining =>
        (CallDeadline is { } call && call < RetryDeadline ? call : RetryDeadline) - DateTimeOffset.UtcNow;

    internal TimeSpan AttemptLimit =>
        CallDeadline is { } call && call - DateTimeOffset.UtcNow < MaxAttempt ? call - DateTimeOffset.UtcNow : MaxAttempt;

    /// <summary>Makes <paramref name="budget"/> current for this async flow until disposed.</summary>
    public static IDisposable Begin(DeliveryBudget budget)
    {
        var previous = CurrentBudget.Value;
        CurrentBudget.Value = budget;
        return new Scope(() => CurrentBudget.Value = previous);
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}

/// <summary>
/// Runs an actor call, turning delivery failures into <see cref="ActorCallException"/>. Inside a
/// <see cref="DeliveryBudget"/>, not-delivered failures are retried (capped, jittered backoff) within
/// its retry window, and each attempt is bounded by the caller's deadline or the safety limit;
/// outside one, it makes a single attempt.
/// </summary>
public static class ActorCall
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("DaprMQ.Delivery");
    private static readonly System.Diagnostics.Metrics.Counter<long> Retries =
        Meter.CreateCounter<long>("daprmq.delivery.retries", description: "Actor calls retried after a not-delivered failure");
    private static readonly System.Diagnostics.Metrics.Counter<long> Failures =
        Meter.CreateCounter<long>("daprmq.delivery.failures", description: "Actor calls that failed delivery, by outcome");

    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        if (DeliveryBudget.Current is not { } budget)
        {
            return await AttemptAsync(call, cancellationToken);
        }

        if (budget.CallDeadline is { } callDeadline && callDeadline <= DateTimeOffset.UtcNow)
        {
            throw Failed(DeliveryOutcome.NotDelivered, new TimeoutException("The request's deadline passed before the call was attempted."));
        }

        var backoff = budget.InitialBackoff;
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.CallerCancelled);
            attempt.CancelAfter(budget.AttemptLimit is { } limit && limit > TimeSpan.Zero ? limit : TimeSpan.FromMilliseconds(1));
            try
            {
                return await AttemptAsync(call, attempt.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && !budget.CallerCancelled.IsCancellationRequested)
            {
                // The caller's deadline or the safety limit cut the attempt off mid-flight: it may have
                // reached the actor.
                throw Failed(DeliveryOutcome.Unknown, ex);
            }
            catch (ActorCallException ex) when (ex.Outcome == DeliveryOutcome.NotDelivered)
            {
                var delay = TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * backoff.Ticks));
                if (budget.RetryRemaining - delay < budget.MinAttemptWindow)
                {
                    Failures.Add(1, new KeyValuePair<string, object?>("outcome", "not-delivered"));
                    throw;
                }

                Retries.Add(1);
                await Task.Delay(delay, budget.CallerCancelled);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, budget.MaxBackoff.Ticks));
            }
        }
    }

    public static Task RunAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken) =>
        RunAsync(async ct => { await call(ct); return true; }, cancellationToken);

    private static async Task<T> AttemptAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call(cancellationToken);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                                   && ActorCallClassifier.Classify(ex) is { } outcome)
        {
            throw Failed(outcome, ex);
        }
    }

    private static ActorCallException Failed(DeliveryOutcome outcome, Exception inner)
    {
        if (outcome == DeliveryOutcome.Unknown)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("outcome", "unknown"));
        }

        return new ActorCallException(outcome, inner);
    }
}
