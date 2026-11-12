using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// E-01..E-03 from sdks/testing/INTEGRATION_TESTS.md - message expiry through the SDK. The test stack sets
/// DAPRMQ_MIN_EXPIRY_TTL_SECONDS to 1 so an item can expire within a test.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class MessageExpiryTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    // Server default, DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS unset.
    private const int DeadLetterExpiryTtlSeconds = 2_419_200;

    [Fact]
    public async Task E01_ExpiredItem_IsNotDelivered()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await client.EnqueueAsync(queueId, [new EnqueueItemDto(Json("""{"seq":1}"""), ExpiryTtlSeconds: 1)]);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.Null(await client.DequeueLockedAsync(queueId));
    }

    [Fact]
    public async Task E02_DequeuedAndConsumedItems_CarryTheirExpiry()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        double before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await client.EnqueueAsync(queueId, [
            new EnqueueItemDto(Json("""{"seq":1}"""), ExpiryTtlSeconds: 3600),
            new EnqueueItemDto(Json("""{"seq":2}"""), ExpiryTtlSeconds: 7200)
        ]);

        var locked = Assert.Single((await client.DequeueLockedAsync(queueId))!.Items);
        Assert.Equal(3600, locked.ExpiryTtlSeconds);
        Assert.InRange(locked.ExpiryAt!.Value, before + 3600 - 5, before + 3600 + 30);
        Assert.Null(locked.OriginalExpiryTtlSeconds);
        await client.AcknowledgeAsync(queueId, locked.LockId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var delivery in client.ConsumeAsync(queueId, ct: cts.Token))
        {
            Assert.Equal(7200, delivery.ExpiryTtlSeconds);
            Assert.InRange(delivery.ExpiryAt!.Value, before + 7200 - 5, before + 7200 + 30);
            await delivery.AckAsync(CancellationToken.None);
            break;
        }
    }

    [Fact]
    public async Task E03_DeadLetteredItem_CarriesTheOriginalTimeToLive()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        await client.EnqueueAsync(queueId, [new EnqueueItemDto(Json("""{"seq":1}"""), ExpiryTtlSeconds: 3600)]);
        var locked = Assert.Single((await client.DequeueLockedAsync(queueId))!.Items);
        await client.DeadLetterAsync(queueId, locked.LockId);

        var dead = Assert.Single((await client.DequeueLockedAsync(DeadLetterQueueId(queueId)))!.Items);

        Assert.Equal(DeadLetterExpiryTtlSeconds, dead.ExpiryTtlSeconds);
        Assert.Equal(3600, dead.OriginalExpiryTtlSeconds);
    }
}
