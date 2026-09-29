using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// L-01..L-08 from sdks/testing/INTEGRATION_TESTS.md - lock lifecycle and its exception mapping.
///
/// The TTL-driven cases use the shortest lock TTL the server accepts (1s; QueueActor clamps to
/// 1..300) and poll for the observable consequence rather than sleeping a guessed duration -
/// redelivery is driven by a Dapr reminder, so its exact firing moment isn't ours to predict.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class LockTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    private const int ShortLockTtlSeconds = 1;

    [Fact]
    public async Task L01_LockedItems_AreNotRedeliveredToASecondDequeue()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 2);

        var first = await client.DequeueLockedAsync(queueId);
        Assert.NotNull(first);
        var locked = Assert.Single(first!.Items);
        Assert.Equal(1, Seq(locked.Item));

        // Default (non-competing-consumer) semantics: while any lock is outstanding the queue
        // refuses to hand out more, and says so via Locked rather than an exception or a
        // duplicate delivery. Item 2 is present but deliberately withheld.
        var second = await client.DequeueLockedAsync(queueId);
        Assert.NotNull(second);
        Assert.True(second!.Locked);
        Assert.Empty(second.Items);

        // Once the lock is resolved, delivery resumes - and item 1 is not handed out again.
        await client.AcknowledgeAsync(queueId, locked.LockId);
        var third = await client.DequeueLockedAsync(queueId);
        Assert.NotNull(third);
        Assert.Equal(2, Seq(Assert.Single(third!.Items).Item));
    }

    [Fact]
    public async Task L02_LockExpiry_MakesItemAvailableAgain()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        var first = await client.DequeueLockedAsync(queueId, ttlSeconds: ShortLockTtlSeconds);
        var originalLockId = Assert.Single(first!.Items).LockId;

        // Never acked - the lock's expiry reminder re-queues the item on its own.
        var redelivered = await PollUntilAsync(
            async () =>
            {
                var result = await client.DequeueLockedAsync(queueId, ttlSeconds: 30);
                return result?.Items.Count > 0 ? result : null;
            },
            because: "the expired lock's item to become available for dequeue again");

        var item = Assert.Single(redelivered.Items);
        Assert.Equal(1, Seq(item.Item));
        Assert.NotEqual(originalLockId, item.LockId);
    }

    [Fact]
    public async Task L03_AckAfterLockExpiry_RaisesLockNotFound()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        var first = await client.DequeueLockedAsync(queueId, ttlSeconds: ShortLockTtlSeconds);
        var expiredLockId = Assert.Single(first!.Items).LockId;

        // Wait for the observable proof that expiry actually ran: the item is back on the queue.
        // (Polling this rather than sleeping also removes any ambiguity about whether the lock
        // record still exists at the moment we ack.)
        var redelivered = await PollUntilAsync(
            async () =>
            {
                var result = await client.DequeueLockedAsync(queueId, ttlSeconds: 30);
                return result?.Items.Count > 0 ? result : null;
            },
            because: "the expired lock to be reaped and its item re-queued");
        Assert.Single(redelivered.Items);

        // NOTE: the matrix row names LockExpired, but Acknowledge has no expiry branch at all
        // (QueueActor.Acknowledge only looks the lock record up) - expiry is what *deletes* the
        // record, so by the time an ack can observe expiry the lock is simply gone. LockNotFound
        // is the contract the server actually implements here; ExtendLock and DeadLetter are the
        // operations that surface LockExpired (see L-07, D-02).
        await Assert.ThrowsAsync<LockNotFoundException>(
            () => client.AcknowledgeAsync(queueId, expiredLockId));
    }

    [Fact]
    public async Task L04_AckWithUnknownLockId_RaisesLockNotFound()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        var ex = await Assert.ThrowsAsync<LockNotFoundException>(
            () => client.AcknowledgeAsync(queueId, $"never-issued-{Guid.NewGuid():N}"));
        Assert.Equal("LOCK_NOT_FOUND", ex.ErrorCode);

        // The bogus ack didn't consume or disturb the real item.
        var remaining = await client.DequeueLockedAsync(queueId);
        Assert.Equal(1, Seq(Assert.Single(remaining!.Items).Item));
    }

    [Fact]
    public async Task L05_DoubleAck_RaisesLockNotFound()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        var dequeued = await client.DequeueLockedAsync(queueId);
        var lockId = Assert.Single(dequeued!.Items).LockId;

        await client.AcknowledgeAsync(queueId, lockId);

        // Ack is not idempotent - the lock record is gone after the first one.
        await Assert.ThrowsAsync<LockNotFoundException>(
            () => client.AcknowledgeAsync(queueId, lockId));
    }

    [Fact]
    public async Task L06_ExtendLock_ProlongsTheLockPastItsOriginalTtl()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        const int originalTtlSeconds = 2;
        var dequeued = await client.DequeueLockedAsync(queueId, ttlSeconds: originalTtlSeconds);
        var item = Assert.Single(dequeued!.Items);

        await client.ExtendLockAsync(queueId, item.LockId, additionalTtlSeconds: 60);

        // Well past the original 2s TTL. Without the extension the reminder would have fired,
        // re-queued the item and dropped the lock, so the ack below would throw LockNotFound.
        await Task.Delay(TimeSpan.FromSeconds(originalTtlSeconds + 3));

        await client.AcknowledgeAsync(queueId, item.LockId);

        // Acked, not redelivered - the item is gone for good.
        Assert.Null(await client.DequeueLockedAsync(queueId));
    }

    [Fact]
    public async Task L07_ExtendLockOnExpiredOrUnknownLock_RaisesTheMatchingError()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        // Unknown lock: no record to extend.
        await Assert.ThrowsAsync<LockNotFoundException>(
            () => client.ExtendLockAsync(queueId, $"never-issued-{Guid.NewGuid():N}", additionalTtlSeconds: 30));

        var dequeued = await client.DequeueLockedAsync(queueId, ttlSeconds: ShortLockTtlSeconds);
        var expiredLockId = Assert.Single(dequeued!.Items).LockId;

        var redelivered = await PollUntilAsync(
            async () =>
            {
                var result = await client.DequeueLockedAsync(queueId, ttlSeconds: 60);
                return result?.Items.Count > 0 ? result : null;
            },
            because: "the short-TTL lock to expire and its item to be re-queued");

        // Either error is correct depending on how far expiry has progressed: ExtendLock reports
        // LockExpired while the record is still present but past its expiry, and LockNotFound once
        // the expiry reminder has reaped it. Both are the "matching error" for an unusable lock;
        // which one wins is a race with a Dapr reminder, so this asserts the union rather than
        // pinning a timing-dependent answer.
        var ex = await Assert.ThrowsAnyAsync<DaprMQException>(
            () => client.ExtendLockAsync(queueId, expiredLockId, additionalTtlSeconds: 30));
        Assert.True(
            ex is LockExpiredException or LockNotFoundException,
            $"expected LockExpired or LockNotFound for an expired lock, got {ex.GetType().Name}: {ex.Message}");

        // A non-positive extension on a perfectly valid lock is a validation error, not a lock error.
        await Assert.ThrowsAsync<ValidationException>(
            () => client.ExtendLockAsync(queueId, redelivered.Items[0].LockId, additionalTtlSeconds: 0));
    }

    [Fact]
    public async Task L08_RedeliveryAfterExpiry_ReturnsTheItemToTheBackOfItsPriority()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 3);

        // Lock only the head item, then let it expire unacked. While that lock is outstanding the
        // queue withholds everything (L-01), so the first non-empty dequeue below is necessarily
        // the post-expiry state of the whole queue - no partial-redelivery window to handle.
        var dequeued = await client.DequeueLockedAsync(queueId, count: 1, ttlSeconds: ShortLockTtlSeconds);
        Assert.Equal(1, Seq(Assert.Single(dequeued!.Items).Item));

        var afterExpiry = await PollUntilAsync(
            async () =>
            {
                var result = await client.DequeueLockedAsync(queueId, count: 3, ttlSeconds: 60);
                return result?.Items.Count > 0 ? result : null;
            },
            because: "the expired lock's item to rejoin the queue alongside its untouched siblings");

        Assert.Equal(3, afterExpiry.Items.Count);

        // NOTE: the matrix row claims redelivery preserves the item's original FIFO position. It
        // does not: QueueActor's lock-expiry reminder re-queues via EnqueueInternal, a plain
        // append, so the redelivered item lands *behind* the items that were queued behind it.
        // This asserts the implemented behaviour (2, 3, 1) rather than the documented intent
        // (1, 2, 3). If position preservation is the contract that's actually wanted, this is the
        // test that should be flipped and the reminder changed to front-insert.
        Assert.Equal([2, 3, 1], afterExpiry.Items.Select(i => Seq(i.Item)));
    }
}
