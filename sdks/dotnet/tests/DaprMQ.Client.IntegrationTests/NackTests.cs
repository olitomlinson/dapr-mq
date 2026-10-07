using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// N-01..N-02 from sdks/testing/INTEGRATION_TESTS.md - nacking a locked item back to its original
/// position, and the dead-letter escalation once the max delivery count is exceeded.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class NackTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    // Server default (DAPRMQ_MAX_DELIVERY_COUNT unset).
    private const int MaxDeliveryCount = 10;

    [Fact]
    public async Task N01_Nack_ReturnsItemToItsOriginalPosition()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 2);

        var first = Assert.Single((await client.DequeueLockedAsync(queueId))!.Items);
        Assert.Equal(1, Seq(first.Item));

        var result = await client.NackAsync(queueId, first.LockId);

        Assert.False(result.DeadLettered);
        Assert.Equal(1, result.DeliveryCount);

        // Nack voids the lock.
        await Assert.ThrowsAsync<LockNotFoundException>(() => client.AcknowledgeAsync(queueId, first.LockId));

        var remaining = await DrainAsync(client, queueId);
        Assert.Equal([1, 2], remaining.Select(i => Seq(i.Item)));
    }

    [Fact]
    public async Task N02_NackPastMaxDeliveryCount_DeadLetters()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        NackResult? result = null;
        for (int i = 0; i <= MaxDeliveryCount; i++)
        {
            var locked = Assert.Single((await client.DequeueLockedAsync(queueId))!.Items);
            result = await client.NackAsync(queueId, locked.LockId);
        }

        Assert.True(result!.DeadLettered);
        Assert.Equal(DeadLetterQueueId(queueId), result.DlqId);
        Assert.Empty(await DrainAsync(client, queueId));
        Assert.Single(await DrainAsync(client, DeadLetterQueueId(queueId)));
    }
}
