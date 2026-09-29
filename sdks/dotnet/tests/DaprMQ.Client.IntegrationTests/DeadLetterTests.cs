using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// D-01..D-02 from sdks/testing/INTEGRATION_TESTS.md - dead-lettering a locked item and the
/// errors raised when the lock backing it is no longer usable.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class DeadLetterTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task D01_DeadLetter_RemovesFromSourceQueue_AndAppearsOnDeadLetterQueue()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        var deadLetterQueueId = DeadLetterQueueId(queueId);

        await client.EnqueueAsync(queueId, [
            new EnqueueItemDto(Json("""{"seq":1,"poison":true}""")),
            new EnqueueItemDto(Json("""{"seq":2,"poison":false}"""))
        ]);

        var dequeued = await client.DequeueLockedAsync(queueId);
        var poison = Assert.Single(dequeued!.Items);
        Assert.Equal(1, Seq(poison.Item));

        await client.DeadLetterAsync(queueId, poison.LockId);

        // Gone from the source queue: the next dequeue is the item that was behind it, and
        // draining never yields the poison item again.
        var remaining = await DrainAsync(client, queueId);
        var survivor = Assert.Single(remaining);
        Assert.Equal(2, Seq(survivor.Item));

        // And present, intact, on the conventional "{queueId}-deadletter" queue.
        var dead = await DrainAsync(client, deadLetterQueueId);
        var deadItem = Assert.Single(dead);
        Assert.Equal(1, Seq(deadItem.Item));
        Assert.True(deadItem.Item.GetProperty("poison").GetBoolean());

        // Dead-lettering voids the lock; it can't then be acked.
        await Assert.ThrowsAsync<LockNotFoundException>(
            () => client.AcknowledgeAsync(queueId, poison.LockId));
    }

    [Fact]
    public async Task D02_DeadLetterWithUnknownOrExpiredLock_RaisesTheMatchingError()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        var unknown = await Assert.ThrowsAsync<LockNotFoundException>(
            () => client.DeadLetterAsync(queueId, $"never-issued-{Guid.NewGuid():N}"));
        Assert.Equal("LOCK_NOT_FOUND", unknown.ErrorCode);

        var dequeued = await client.DequeueLockedAsync(queueId, ttlSeconds: 1);
        var expiredLockId = Assert.Single(dequeued!.Items).LockId;

        await PollUntilTrueAsync(
            async () =>
            {
                var result = await client.DequeueLockedAsync(queueId, ttlSeconds: 60);
                return result?.Items.Count > 0;
            },
            because: "the short-TTL lock to expire and its item to be re-queued");

        // As with ExtendLock (L-07), which of the two fires is a race with the expiry reminder:
        // LockExpired while the record survives past its expiry, LockNotFound once it's reaped.
        var ex = await Assert.ThrowsAnyAsync<DaprMQException>(
            () => client.DeadLetterAsync(queueId, expiredLockId));
        Assert.True(
            ex is LockExpiredException or LockNotFoundException,
            $"expected LockExpired or LockNotFound for an expired lock, got {ex.GetType().Name}: {ex.Message}");

        // Nothing was dead-lettered by either failed attempt.
        Assert.Null(await client.DequeueLockedAsync(DeadLetterQueueId(queueId)));
    }
}
