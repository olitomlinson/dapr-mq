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

        // Any other daprd error, a broken connection or the actor client's timeout: it may have run.
        DaprApiException or HttpRequestException or IOException or OperationCanceledException => DeliveryOutcome.Unknown,

        _ => null,
    };
}

/// <summary>Runs an actor call, turning delivery failures into <see cref="ActorCallException"/>.</summary>
public static class ActorCall
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                                   && ActorCallClassifier.Classify(ex) is { } outcome)
        {
            throw new ActorCallException(outcome, ex);
        }
    }

    public static Task RunAsync(Func<Task> call, CancellationToken cancellationToken) =>
        RunAsync(async () => { await call(); return true; }, cancellationToken);
}
