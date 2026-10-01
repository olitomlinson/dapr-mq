using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// S-01..S-12 from sdks/testing/INTEGRATION_TESTS.md - the manual (unary) session API:
/// AcceptSession / RenewSessionLease / ReleaseSession plus the lease-scoped queue operations that
/// hang off them. Queue ops for a claimed session target the derived id "{queueId}-session-{id}"
/// and carry the lease.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class SessionTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task S01_EnqueueWithSessionId_AcceptTargeted_ThenDequeueAck_YieldsFifoOrder()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "order-42";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 3, sessionId: sessionId);

        var lease = await client.AcceptSessionAsync(queueId, sessionId);
        Assert.NotNull(lease);
        Assert.Equal(sessionId, lease!.SessionId);
        Assert.NotEmpty(lease.LeaseId);
        Assert.True(lease.LeaseExpiresAt > 0);

        var sessionQueueId = SessionQueueId(queueId, sessionId);
        var drained = await DrainAsync(client, sessionQueueId, leaseId: lease.LeaseId);

        Assert.Equal([1, 2, 3], drained.Select(i => Seq(i.Item)));
    }

    [Fact]
    public async Task S02_AcceptSessionWithNoId_ClaimsAnyAvailableSession_AndReturnsItsId()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "the-only-session";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);

        var lease = await client.AcceptSessionAsync(queueId);
        Assert.NotNull(lease);
        Assert.Equal(sessionId, lease!.SessionId);
        Assert.NotEmpty(lease.LeaseId);
    }

    [Fact]
    public async Task S03_AcceptSessionWithNoSessionsAvailable_ReturnsNull()
    {
        var client = CreateClient();

        // A queue that has never had a session enqueued to it knows of no sessions at all.
        Assert.Null(await client.AcceptSessionAsync(NewQueueId()));

        // And one whose only session is already claimed has none *available*.
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: "taken");
        Assert.NotNull(await client.AcceptSessionAsync(queueId));
        Assert.Null(await client.AcceptSessionAsync(queueId));
    }

    [Fact]
    public async Task S04_AcceptSessionOnAlreadyLeasedSession_RaisesSessionLocked()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "contended";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);

        var first = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 300);
        Assert.NotNull(first);

        // A *targeted* claim on a leased session is an error, unlike the untargeted form which
        // simply reports nothing available (S-03).
        var ex = await Assert.ThrowsAsync<SessionLockedException>(
            () => client.AcceptSessionAsync(queueId, sessionId));
        Assert.Equal("SESSION_LOCKED", ex.ErrorCode);
    }

    [Fact]
    public async Task S05_AcceptSessionOnUnknownSession_RaisesSessionNotFound()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        // Activate the queue's session coordinator with a real session, so the failure below is
        // "that session is unknown" rather than "this queue has no sessions at all".
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: "a-real-session");

        var ex = await Assert.ThrowsAsync<SessionNotFoundException>(
            () => client.AcceptSessionAsync(queueId, "no-such-session"));
        Assert.Equal("SESSION_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task S06_DequeueOnLeasedSession_WithoutOrWithWrongLeaseId_IsRejected()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "guarded";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);
        var lease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 300);
        var sessionQueueId = SessionQueueId(queueId, sessionId);

        // No lease at all.
        await Assert.ThrowsAsync<ValidationException>(
            () => client.DequeueLockedAsync(sessionQueueId));

        // Someone else's lease.
        await Assert.ThrowsAsync<ValidationException>(
            () => client.DequeueLockedAsync(sessionQueueId, leaseId: $"not-the-holder-{Guid.NewGuid():N}"));

        // The real holder still gets through.
        var dequeued = await client.DequeueLockedAsync(sessionQueueId, leaseId: lease!.LeaseId);
        Assert.Equal(1, Seq(Assert.Single(dequeued!.Items).Item));
    }

    [Fact]
    public async Task S07_RenewSessionLease_ExtendsExpiry_KeepingTheSessionUsable()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "renew-me";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);

        const int originalLeaseSeconds = 3;
        var lease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: originalLeaseSeconds);
        Assert.NotNull(lease);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var renewed = await client.RenewSessionLeaseAsync(queueId, sessionId, lease!.LeaseId, additionalSeconds: 60);
        Assert.Equal(sessionId, renewed.SessionId);
        Assert.Equal(lease.LeaseId, renewed.LeaseId);
        Assert.True(renewed.LeaseExpiresAt > lease.LeaseExpiresAt,
            $"renewal should push expiry out: was {lease.LeaseExpiresAt}, now {renewed.LeaseExpiresAt}");

        // Past the original 3s window - only the renewal keeps this working.
        await Task.Delay(TimeSpan.FromSeconds(3));

        var dequeued = await client.DequeueLockedAsync(SessionQueueId(queueId, sessionId), leaseId: lease.LeaseId);
        Assert.Equal(1, Seq(Assert.Single(dequeued!.Items).Item));
    }

    [Fact]
    public async Task S08_ExpiredLease_IsReclaimable_AndOldLeaseRaisesSessionLeaseExpired()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "expires-and-reclaimed";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);

        const int leaseSeconds = 2;
        var firstLease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: leaseSeconds);
        Assert.NotNull(firstLease);
        var sessionQueueId = SessionQueueId(queueId, sessionId);

        // The first holder takes the item and never acks it, so a lock is outstanding when the
        // lease lapses - the case where reclaiming those locks must not drop the lease guard.
        var taken = await client.DequeueLockedAsync(sessionQueueId, leaseId: firstLease!.LeaseId);
        Assert.Equal(1, Seq(Assert.Single(taken!.Items).Item));

        // Poll straight across the expiry boundary. Until it passes the item stays locked; after it,
        // the original holder is told the lease expired rather than being handed the item again.
        var deadline = DateTime.UtcNow.AddSeconds(leaseSeconds + 5);
        while (true)
        {
            Assert.True(DateTime.UtcNow < deadline, "lease never reported as expired");
            try
            {
                var polled = await client.DequeueLockedAsync(sessionQueueId, leaseId: firstLease.LeaseId);
                Assert.True(polled!.Locked);
                Assert.Empty(polled.Items);
            }
            catch (SessionLeaseExpiredException)
            {
                break;
            }
            await Task.Delay(250);
        }

        // Reclaiming the lock left the stale lease record in place, so it keeps refusing every
        // caller - the old holder, and one presenting no lease at all.
        await Assert.ThrowsAsync<SessionLeaseExpiredException>(
            () => client.DequeueLockedAsync(sessionQueueId, leaseId: firstLease.LeaseId));
        await Assert.ThrowsAsync<SessionLeaseExpiredException>(
            () => client.DequeueLockedAsync(sessionQueueId));

        // Renewal of a lapsed lease is refused for the same reason.
        await Assert.ThrowsAsync<SessionLeaseExpiredException>(
            () => client.RenewSessionLeaseAsync(queueId, sessionId, firstLease!.LeaseId));

        // And the session is free for someone else to pick up, with a fresh lease id.
        var secondLease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 60);
        Assert.NotNull(secondLease);
        Assert.Equal(sessionId, secondLease!.SessionId);
        Assert.NotEqual(firstLease!.LeaseId, secondLease.LeaseId);

        // The new holder gets through, and the item is still there untouched.
        var dequeued = await client.DequeueLockedAsync(sessionQueueId, leaseId: secondLease.LeaseId);
        Assert.Equal(1, Seq(Assert.Single(dequeued!.Items).Item));

        // The superseded lease is now rejected as the wrong holder, not as an expired one.
        await Assert.ThrowsAsync<ValidationException>(
            () => client.DequeueLockedAsync(sessionQueueId, leaseId: firstLease.LeaseId));
    }

    [Fact]
    public async Task S09_ReleaseSession_FreesImmediately_WithoutWaitingForExpiry()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "release-me";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);

        // A lease long enough that natural expiry cannot explain the reclaim below.
        var lease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 300);
        Assert.NotNull(lease);

        await client.ReleaseSessionAsync(queueId, sessionId, lease!.LeaseId);

        var reclaimed = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 60);
        Assert.NotNull(reclaimed);
        Assert.Equal(sessionId, reclaimed!.SessionId);
        Assert.NotEqual(lease.LeaseId, reclaimed.LeaseId);
    }

    [Fact]
    public async Task S10_ReleaseSession_IsIdempotent_AndRejectsAWrongLease()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "release-twice";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1, sessionId: sessionId);
        var lease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 300);

        await client.ReleaseSessionAsync(queueId, sessionId, lease!.LeaseId);
        // Same lease again - already gone, still a success.
        await client.ReleaseSessionAsync(queueId, sessionId, lease.LeaseId);

        // A different holder's lease is refused rather than silently honoured.
        var reclaimed = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 300);
        Assert.NotNull(reclaimed);
        await Assert.ThrowsAsync<InvalidLeaseIdException>(
            () => client.ReleaseSessionAsync(queueId, sessionId, $"not-the-holder-{Guid.NewGuid():N}"));

        // The genuine holder is unaffected by the rejected release.
        var dequeued = await client.DequeueLockedAsync(SessionQueueId(queueId, sessionId), leaseId: reclaimed!.LeaseId);
        Assert.Equal(1, Seq(Assert.Single(dequeued!.Items).Item));
    }

    [Fact]
    public async Task S11_AckExtendLockAndDeadLetter_HonourTheLeaseIdOnASessionQueue()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        const string sessionId = "lease-scoped-ops";

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 2, sessionId: sessionId);

        var lease = await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 300);
        var sessionQueueId = SessionQueueId(queueId, sessionId);
        var wrongLeaseId = $"not-the-holder-{Guid.NewGuid():N}";

        var first = await client.DequeueLockedAsync(sessionQueueId, leaseId: lease!.LeaseId);
        var firstItem = Assert.Single(first!.Items);

        // ExtendLock: rejected without the lease, accepted with it.
        await Assert.ThrowsAsync<ValidationException>(
            () => client.ExtendLockAsync(sessionQueueId, firstItem.LockId, additionalTtlSeconds: 30));
        await Assert.ThrowsAsync<ValidationException>(
            () => client.ExtendLockAsync(sessionQueueId, firstItem.LockId, additionalTtlSeconds: 30, leaseId: wrongLeaseId));
        await client.ExtendLockAsync(sessionQueueId, firstItem.LockId, additionalTtlSeconds: 30, leaseId: lease.LeaseId);

        // Acknowledge: same guard.
        await Assert.ThrowsAsync<InvalidLeaseIdException>(
            () => client.AcknowledgeAsync(sessionQueueId, firstItem.LockId));
        await Assert.ThrowsAsync<InvalidLeaseIdException>(
            () => client.AcknowledgeAsync(sessionQueueId, firstItem.LockId, leaseId: wrongLeaseId));
        await client.AcknowledgeAsync(sessionQueueId, firstItem.LockId, leaseId: lease.LeaseId);

        // DeadLetter: same guard, and it routes off the *session* queue's own DLQ.
        var second = await client.DequeueLockedAsync(sessionQueueId, leaseId: lease.LeaseId);
        var secondItem = Assert.Single(second!.Items);
        Assert.Equal(2, Seq(secondItem.Item));

        await Assert.ThrowsAsync<InvalidLeaseIdException>(
            () => client.DeadLetterAsync(sessionQueueId, secondItem.LockId));
        await Assert.ThrowsAsync<InvalidLeaseIdException>(
            () => client.DeadLetterAsync(sessionQueueId, secondItem.LockId, leaseId: wrongLeaseId));
        await client.DeadLetterAsync(sessionQueueId, secondItem.LockId, leaseId: lease.LeaseId);

        var dead = await DrainAsync(client, DeadLetterQueueId(sessionQueueId));
        Assert.Equal(2, Seq(Assert.Single(dead).Item));
    }

    [Fact]
    public async Task S12_TwoSessions_KeepIndependentFifoOrder()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        // Interleaved on the wire, so shared ordering would show up as interleaved delivery.
        await client.EnqueueAsync(queueId, [
            new EnqueueItemDto(new { seq = 1 }, SessionId: "s1"),
            new EnqueueItemDto(new { seq = 1 }, SessionId: "s2"),
            new EnqueueItemDto(new { seq = 2 }, SessionId: "s1"),
            new EnqueueItemDto(new { seq = 2 }, SessionId: "s2"),
            new EnqueueItemDto(new { seq = 3 }, SessionId: "s1"),
            new EnqueueItemDto(new { seq = 3 }, SessionId: "s2")
        ]);

        var leaseA = await client.AcceptSessionAsync(queueId, "s1", leaseSeconds: 300);
        var leaseB = await client.AcceptSessionAsync(queueId, "s2", leaseSeconds: 300);
        Assert.NotNull(leaseA);
        Assert.NotNull(leaseB);

        // Drained concurrently - independent actor instances, so neither blocks the other.
        var drainA = DrainAsync(client, SessionQueueId(queueId, "s1"), leaseId: leaseA!.LeaseId);
        var drainB = DrainAsync(client, SessionQueueId(queueId, "s2"), leaseId: leaseB!.LeaseId);
        await Task.WhenAll(drainA, drainB);

        Assert.Equal([1, 2, 3], (await drainA).Select(i => Seq(i.Item)));
        Assert.Equal([1, 2, 3], (await drainB).Select(i => Seq(i.Item)));

        // Nothing landed on the plain, session-free queue.
        Assert.Null(await client.DequeueLockedAsync(queueId));
    }
}
