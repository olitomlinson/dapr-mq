using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// QS-01..QS-04 from sdks/testing/INTEGRATION_TESTS.md - the Consume stream for plain queues,
/// surfaced as IDaprMQClient.ConsumeAsync. Leaving the enumeration half-closes the stream and waits
/// for the server to finish, so settlements written before a break are applied.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class QueueStreamTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    private static CancellationTokenSource Deadline(int seconds = 30) => new(TimeSpan.FromSeconds(seconds));

    [Fact]
    public async Task QS01_Consume_DeliversInOrder_AndAckRemovesItems()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 3);

        using var cts = Deadline();
        var delivered = new List<QueueDelivery>();
        await foreach (var delivery in client.ConsumeAsync(queueId, new ConsumeOptions { PrefetchCount = 2, AllowCompetingConsumers = true }, cts.Token))
        {
            delivered.Add(delivery);
            await delivery.AckAsync(cts.Token);
            if (delivered.Count == 3)
            {
                break;
            }
        }

        Assert.Equal([1, 2, 3], delivered.Select(d => Seq(d.Item)));
        Assert.All(delivered, d => Assert.Equal(1, d.DeliveryCount));
        Assert.Null(await client.DequeueLockedAsync(queueId));
    }

    [Fact]
    public async Task QS02_Consume_NackRedeliversWithTheNextDeliveryCount()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        using var cts = Deadline();
        var delivered = new List<QueueDelivery>();
        await foreach (var delivery in client.ConsumeAsync(queueId, ct: cts.Token))
        {
            delivered.Add(delivery);
            if (delivered.Count == 1)
            {
                await delivery.NackAsync(cts.Token);
                continue;
            }
            await delivery.AckAsync(cts.Token);
            break;
        }

        Assert.Equal([1, 1], delivered.Select(d => Seq(d.Item)));
        Assert.Equal([1, 2], delivered.Select(d => d.DeliveryCount));
    }

    [Fact]
    public async Task QS03_Consume_KeepsADeliveredItemLockedPastItsTtl()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        using var cts = Deadline();
        var settleFailures = new List<string>();
        var options = new ConsumeOptions
        {
            LockTtl = TimeSpan.FromSeconds(2),
            AllowCompetingConsumers = true,
            OnSettleFailed = (lockId, _) => settleFailures.Add(lockId)
        };
        await foreach (var delivery in client.ConsumeAsync(queueId, options, cts.Token))
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
            Assert.Null(await client.DequeueLockedAsync(queueId, allowCompetingConsumers: true));
            await delivery.AckAsync(cts.Token);
            break;
        }

        Assert.Empty(settleFailures);
    }

    [Fact]
    public async Task QS04_ClosingTheStream_ReturnsUnsettledItemsStraightAway()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);

        using var cts = Deadline();
        await foreach (var _ in client.ConsumeAsync(queueId, new ConsumeOptions { LockTtl = TimeSpan.FromSeconds(300) }, cts.Token))
        {
            break;
        }

        var back = await client.DequeueLockedAsync(queueId);
        Assert.Equal(1, Seq(Assert.Single(back!.Items).Item));
    }
}
