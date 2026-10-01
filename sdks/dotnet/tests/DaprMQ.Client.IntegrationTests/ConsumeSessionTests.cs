using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// C-01..C-08 from sdks/testing/INTEGRATION_TESTS.md - the low-level ConsumeSession streaming RPC
/// surfaced as IDaprMQClient.ConsumeSessionAsync. C-09 is not implemented; see the note at the
/// bottom of this file.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class ConsumeSessionTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    private const int DefaultLeaseSeconds = 30;

    /// <summary>
    /// Pulls exactly <paramref name="count"/> deliveries off a stream, optionally acking each,
    /// then leaves the enumeration - which disposes the call and closes the stream, the same
    /// thing a consumer that has finished does.
    /// </summary>
    private static async Task<List<SessionDelivery>> TakeAsync(
        IAsyncEnumerable<SessionDelivery> stream,
        int count,
        bool ack,
        CancellationToken ct)
    {
        var taken = new List<SessionDelivery>();
        await foreach (var delivery in stream.WithCancellation(ct))
        {
            taken.Add(delivery);
            if (ack)
            {
                await delivery.AckAsync(ct);
            }

            if (taken.Count >= count)
            {
                break;
            }
        }

        return taken;
    }

    /// <summary>
    /// Enumerates a stream to its natural end, letting the caller resolve each delivery.
    ///
    /// Ack/DeadLetter are fire-and-forget frames on the request stream - writing one does not wait
    /// for the server to apply it. Breaking out of the loop immediately after the last one can
    /// therefore tear the call down before it lands, leaving the item still locked. Pairing this
    /// with a short sessionIdleTimeoutSeconds makes the server end the stream instead, which it
    /// only does once nothing is outstanding - i.e. once every frame written here has been applied.
    /// </summary>
    private static async Task<List<SessionDelivery>> ConsumeUntilDrainedAsync(
        IAsyncEnumerable<SessionDelivery> stream,
        Func<SessionDelivery, Task> resolve,
        CancellationToken ct)
    {
        var delivered = new List<SessionDelivery>();
        await foreach (var delivery in stream.WithCancellation(ct))
        {
            delivered.Add(delivery);
            await resolve(delivery);
        }

        return delivered;
    }

    private static CancellationTokenSource Deadline(int seconds = 30) => new(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task C01_Stream_AssignsSession_DeliversInOrder_AndAckRemovesItems()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "stream-basics";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 3, sessionId: sessionId);

        using var cts = Deadline();
        var stream = client.ConsumeSessionAsync(
            queueId, sessionId, leaseSeconds: 300, prefetchCount: 5, cts.Token, sessionIdleTimeoutSeconds: 2);
        var delivered = await ConsumeUntilDrainedAsync(stream, d => d.AckAsync(cts.Token), cts.Token);

        Assert.Equal(3, delivered.Count);
        Assert.All(delivered, d => Assert.Equal(sessionId, d.SessionId));
        Assert.Equal([1, 2, 3], delivered.Select(d => Seq(d.Item)));
        Assert.Equal(3, delivered.Select(d => d.LockId).Distinct().Count());
        Assert.All(delivered, d => Assert.Equal(1, d.Priority));

        // Acked deliveries are gone: the session queue is empty for the next claimant.
        var lease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 60);
        Assert.NotNull(lease);
        Assert.Null(await client.DequeueLockedAsync(SessionQueueId(queueId, sessionId), leaseId: lease!.LeaseId));
    }

    [Fact]
    public async Task C02_TargetedStream_OnlyReceivesThatSession()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await client.EnqueueAsync(queueId, [
            new EnqueueItemDto(new { seq = 1 }, SessionId: "wanted"),
            new EnqueueItemDto(new { seq = 2 }, SessionId: "wanted"),
            new EnqueueItemDto(new { seq = 99 }, SessionId: "unwanted")
        ]);

        using var cts = Deadline();
        var stream = client.ConsumeSessionAsync(queueId, "wanted", DefaultLeaseSeconds, prefetchCount: 5, cts.Token);
        var delivered = await TakeAsync(stream, count: 2, ack: true, cts.Token);

        Assert.Equal([1, 2], delivered.Select(d => Seq(d.Item)));
        Assert.All(delivered, d => Assert.Equal("wanted", d.SessionId));

        // The other session was never touched - its item is still claimable and intact.
        var lease = await client.AcceptSessionAsync(queueId, "unwanted", leaseSeconds: 60);
        Assert.NotNull(lease);
        var remaining = await client.DequeueLockedAsync(SessionQueueId(queueId, "unwanted"), leaseId: lease!.LeaseId);
        Assert.Equal(99, Seq(Assert.Single(remaining!.Items).Item));
    }

    [Fact]
    public async Task C03_DeliveryDeadLetter_RoutesToTheSessionQueuesDeadLetterQueue()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "poison-session";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 2, sessionId: sessionId);

        using var cts = Deadline();
        var stream = client.ConsumeSessionAsync(
            queueId, sessionId, leaseSeconds: 300, prefetchCount: 5, cts.Token, sessionIdleTimeoutSeconds: 2);

        var handled = new List<int>();
        await ConsumeUntilDrainedAsync(
            stream,
            delivery =>
            {
                var seq = Seq(delivery.Item);
                handled.Add(seq);
                // The first item is poison, the second is fine - so the session must keep going
                // after a dead-letter rather than ending on it.
                return seq == 1 ? delivery.DeadLetterAsync(cts.Token) : delivery.AckAsync(cts.Token);
            },
            cts.Token);

        Assert.Equal([1, 2], handled);

        // NOTE: the matrix row says "{queueId}-deadletter". The server dead-letters against the
        // actor that holds the item - the per-session QueueActor - so the DLQ is actually the
        // session queue's own: "{queueId}-session-{sessionId}-deadletter". Asserted as implemented.
        var sessionDeadLetterQueueId = DeadLetterQueueId(SessionQueueId(queueId, sessionId));
        var dead = await DrainAsync(client, sessionDeadLetterQueueId);
        Assert.Equal(1, Seq(Assert.Single(dead).Item));

        // And nothing landed on the base queue's DLQ.
        Assert.Null(await client.DequeueLockedAsync(DeadLetterQueueId(queueId)));
    }

    [Fact]
    public async Task C04_PrefetchCount_BoundsUnackedInFlightDeliveries()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "prefetch-bound";
        const int prefetchCount = 2;

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 6, sessionId: sessionId);

        using var cts = Deadline();
        var stream = client.ConsumeSessionAsync(queueId, sessionId, DefaultLeaseSeconds, prefetchCount, cts.Token);

        await using var enumerator = stream.GetAsyncEnumerator(cts.Token);

        // Two deliveries arrive without any ack...
        var held = new List<SessionDelivery>();
        for (var i = 0; i < prefetchCount; i++)
        {
            Assert.True(await enumerator.MoveNextAsync());
            held.Add(enumerator.Current);
        }

        Assert.Equal([1, 2], held.Select(d => Seq(d.Item)));

        // ...and the third does not, for as long as both are still outstanding. The server polls
        // on a 200ms cadence, so a couple of seconds is many missed opportunities to over-deliver.
        var thirdBeforeAck = enumerator.MoveNextAsync().AsTask();
        var elapsed = await Task.WhenAny(thirdBeforeAck, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.NotSame(thirdBeforeAck, elapsed);

        // Freeing one slot releases exactly one more delivery.
        await held[0].AckAsync(cts.Token);
        Assert.True(await thirdBeforeAck.WaitAsync(TimeSpan.FromSeconds(15), cts.Token));
        Assert.Equal(3, Seq(enumerator.Current.Item));

        await held[1].AckAsync(cts.Token);
        await enumerator.Current.AckAsync(cts.Token);
    }

    [Fact]
    public async Task C05_ClientDisconnect_ReleasesTheSessionImmediately()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "disconnect-releases";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 2, sessionId: sessionId);

        using var cts = Deadline();
        // A lease long enough that natural expiry cannot explain the reclaim below.
        var stream = client.ConsumeSessionAsync(queueId, sessionId, leaseSeconds: 300, prefetchCount: 5, cts.Token);
        var delivered = await TakeAsync(stream, count: 1, ack: true, cts.Token);
        Assert.Single(delivered);

        // TakeAsync's break disposed the call, closing the stream. The server releases the
        // session on stream teardown, so a targeted claim succeeds without waiting out the lease.
        var reclaimed = await ClaimWhenFreeAsync(client, queueId, sessionId);
        Assert.Equal(sessionId, reclaimed.SessionId);
    }

    [Fact]
    public async Task C06_SessionIdleTimeout_EndsTheStream_AndReleasesTheSession()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "idle-drain";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);

        using var cts = Deadline();
        // Long lease so renewal never interferes, short idle timeout so the drain is what ends it.
        var stream = client.ConsumeSessionAsync(
            queueId, sessionId, leaseSeconds: 300, prefetchCount: 5, cts.Token, sessionIdleTimeoutSeconds: 2);

        var delivered = new List<SessionDelivery>();
        await foreach (var delivery in stream.WithCancellation(cts.Token))
        {
            delivered.Add(delivery);
            await delivery.AckAsync(cts.Token);
            // Deliberately no break: the loop exits only because the server ends the stream once
            // the session has sat empty for sessionIdleTimeoutSeconds.
        }

        Assert.Equal(1, Seq(Assert.Single(delivered).Item));

        // Draining releases the session there and then, rather than holding the 300s lease.
        var reclaimed = await ClaimWhenFreeAsync(client, queueId, sessionId);
        Assert.Equal(sessionId, reclaimed.SessionId);
    }

    [Fact]
    public async Task C07_SecondStreamOnALeasedSession_SurfacesSessionLocked()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "contended-stream";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 4, sessionId: sessionId);

        using var cts = Deadline();
        var first = client.ConsumeSessionAsync(queueId, sessionId, leaseSeconds: 300, prefetchCount: 1, cts.Token);
        await using var firstEnumerator = first.GetAsyncEnumerator(cts.Token);

        // Hold the session: claimed, with one delivery outstanding and deliberately unacked.
        Assert.True(await firstEnumerator.MoveNextAsync());
        Assert.Equal(1, Seq(firstEnumerator.Current.Item));

        var second = client.ConsumeSessionAsync(queueId, sessionId, leaseSeconds: 30, prefetchCount: 1, cts.Token);
        var ex = await Assert.ThrowsAsync<SessionLockedException>(async () =>
        {
            await foreach (var _ in second.WithCancellation(cts.Token))
            {
            }
        });
        Assert.Equal("SESSION_LOCKED", ex.ErrorCode);
    }

    [Fact]
    public async Task C08_UnackedDeliveryAtDisconnect_IsRedeliveredToTheNextConsumer()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "redeliver-on-disconnect";
        // The item lock inherits the stream's leaseSeconds as its TTL, so a short lease is what
        // makes the abandoned delivery recoverable promptly.
        const int leaseSeconds = 2;

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);

        using var cts = Deadline(60);

        // First consumer takes the item and walks away without acking.
        var abandoned = await TakeAsync(
            client.ConsumeSessionAsync(queueId, sessionId, leaseSeconds, prefetchCount: 1, cts.Token),
            count: 1, ack: false, cts.Token);
        Assert.Equal(1, Seq(Assert.Single(abandoned).Item));

        // A second consumer gets the very same item once the abandoned lock lapses.
        var redelivered = await PollUntilAsync(
            async () =>
            {
                try
                {
                    var taken = await TakeAsync(
                        client.ConsumeSessionAsync(queueId, sessionId, leaseSeconds: 30, prefetchCount: 1, cts.Token),
                        count: 1, ack: true, cts.Token);
                    return taken.Count > 0 ? taken : null;
                }
                catch (SessionLockedException)
                {
                    // The previous stream's session release hasn't landed yet.
                    return null;
                }
            },
            because: "the unacked delivery to be redelivered to a fresh consumer",
            timeoutSeconds: 40,
            intervalMs: 1000);

        var item = Assert.Single(redelivered);
        Assert.Equal(1, Seq(item.Item));
        Assert.NotEqual(abandoned[0].LockId, item.LockId);
    }

    // C-09 (lease lost mid-stream surfaces SessionLost) is permanently out of scope for
    // integration. The server emits SessionLost only when its own background RenewSessionLease
    // call fails, and it renews every leaseSeconds/2 against a lease it alone holds the id for -
    // ConsumeSession never exposes that lease id to the client (see SessionDelivery's doc
    // comment). With no supported way to invalidate someone else's lease from the SDK surface,
    // the only lever is racing the renewal interval against the expiry with leaseSeconds = 1,
    // which lands on either side of the boundary depending on sub-second timing - not a test
    // worth having. A deterministic version would need a server-side test-only revocation seam;
    // that was weighed against the plumbing/fidelity of the alternatives and declined in favor of
    // leaving this to unit tests (see docs/issues/wont-fix/consume-session-lease-loss-has-no-test-seam.md).
    //
    // Coverage instead lives at the unit level:
    // DaprMQClientConsumeSessionTests.ConsumeSessionAsync_SessionLostFrame_ThrowsSessionLostException
    // asserts the frame maps to SessionLostException. SessionQueueConsumer's own handling of that
    // exception (the catch (SessionLostException) branch in SessionQueueConsumer.cs, which resets
    // backoff rather than treating the claim as failed) has no dedicated unit test of its own.
}
