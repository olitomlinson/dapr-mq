using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// B-01..B-02 from sdks/testing/INTEGRATION_TESTS.md - settling a bulk dequeue in one batch call,
/// and per-lock outcomes when some locks are no longer held.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class AcknowledgeBatchTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task B01_AcknowledgeBatch_SettlesABulkDequeueInOneCall()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 50);
        var locked = (await client.DequeueLockedAsync(queueId, count: 50))!.Items;
        var lockIds = locked.Select(i => i.LockId).ToList();

        var result = await client.AcknowledgeBatchAsync(queueId, lockIds);

        Assert.Equal(50, result.ItemsAcknowledged);
        Assert.Equal(lockIds, result.Results.Select(r => r.LockId));
        Assert.All(result.Results, r => Assert.Equal(AcknowledgeOutcomes.Acknowledged, r.Outcome));
        Assert.Empty(await DrainAsync(client, queueId));
    }

    [Fact]
    public async Task B02_AcknowledgeBatch_ReportsLocksNoLongerHeld_WithoutFailingTheRest()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 2);
        var locked = (await client.DequeueLockedAsync(queueId, count: 2))!.Items;
        await client.AcknowledgeAsync(queueId, locked[0].LockId);

        var result = await client.AcknowledgeBatchAsync(queueId, [locked[0].LockId, locked[1].LockId, "never-issued"]);

        Assert.Equal(1, result.ItemsAcknowledged);
        Assert.Equal(
            [AcknowledgeOutcomes.LockNotFound, AcknowledgeOutcomes.Acknowledged, AcknowledgeOutcomes.LockNotFound],
            result.Results.Select(r => r.Outcome));
        Assert.Empty(await DrainAsync(client, queueId));
    }
}
