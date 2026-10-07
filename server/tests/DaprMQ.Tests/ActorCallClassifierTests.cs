using Dapr;
using Dapr.Actors;
using DaprMQ.Interfaces;

namespace DaprMQ.Tests;

/// <summary>
/// The phase-0 failure catalogue (proposals/readiness-and-retries.md, "Phase 0 findings"), one row
/// per observed daprd 1.18 failure. Only an exception proven never to have reached an actor is
/// NotDelivered; anything else from the transport or runtime is Unknown.
/// </summary>
public class ActorCallClassifierTests
{
    public static TheoryData<string, Exception, DeliveryOutcome?> Catalogue => new()
    {
        {
            "no worker hosts the actor type (daprd's 5 s lookup wait expired)",
            new DaprApiException("error invoke actor method: failed to lookup actor: api error: code = FailedPrecondition desc = did not find address for actor 'QueueActor/q1'"),
            DeliveryOutcome.NotDelivered
        },
        {
            "Kubernetes, workers starting: the target refused the call because placement moved the actor (daprd retried 5 times)",
            new DaprApiException("error invoke actor method: failed to invoke target 10.1.5.53:50002 after 5 retries. Error: rpc error: code = Internal desc = error invoke actor method: remote actor moved"),
            DeliveryOutcome.NotDelivered
        },
        {
            "Kubernetes, gateway restarting: its own sidecar refused the connection, so nothing was sent",
            new HttpRequestException("Connection refused (localhost:3500)", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)),
            DeliveryOutcome.NotDelivered
        },
        {
            "worker killed while the request body was still being sent (judgement call: Unknown for now)",
            new HttpRequestException("Error while copying content to a stream.", new IOException("Unable to write data to the transport connection: Broken pipe.")),
            DeliveryOutcome.Unknown
        },
        {
            "worker app hung, or placement down: the actor client's own timeout",
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException("The operation was canceled.")),
            DeliveryOutcome.Unknown
        },
        {
            "any other daprd error",
            new DaprApiException("error invoke actor method: something else"),
            DeliveryOutcome.Unknown
        },
        {
            "the actor method itself threw: the actor ran, so it's a bug, not a delivery failure",
            new ActorMethodInvocationException("boom", false),
            null
        },
        {
            "an exception from the actor service",
            new ActorInvokeException("System.InvalidOperationException", "boom"),
            null
        },
        {
            "not a transport failure at all",
            new InvalidOperationException("bug"),
            null
        },
    };

    [Theory]
    [MemberData(nameof(Catalogue))]
    public void Classify(string mode, Exception exception, DeliveryOutcome? expected)
    {
        Assert.True(expected == ActorCallClassifier.Classify(exception), mode);
    }

    [Fact]
    public async Task RunAsync_WrapsDeliveryFailures_WithTheOutcome()
    {
        var lookup = new DaprApiException("failed to lookup actor: did not find address for actor 'QueueActor/q1'");

        var ex = await Assert.ThrowsAsync<ActorCallException>(() => ActorCall.RunAsync<int>(_ => throw lookup, CancellationToken.None));

        Assert.Equal(DeliveryOutcome.NotDelivered, ex.Outcome);
        Assert.Same(lookup, ex.InnerException);
    }

    [Fact]
    public async Task RunAsync_LeavesActorMethodFailuresAlone()
    {
        await Assert.ThrowsAsync<ActorMethodInvocationException>(() =>
            ActorCall.RunAsync<int>(_ => throw new ActorMethodInvocationException("boom", false), CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_IsNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            ActorCall.RunAsync<int>(_ => throw new TaskCanceledException(), cts.Token));
    }

    [Fact]
    public async Task RunAsync_ReturnsTheResult()
    {
        Assert.Equal(42, await ActorCall.RunAsync(_ => Task.FromResult(42), CancellationToken.None));
    }
}
