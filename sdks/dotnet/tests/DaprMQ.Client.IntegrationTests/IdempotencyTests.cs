using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// I-01..I-03 from sdks/testing/INTEGRATION_TESTS.md - idempotency-key deduplication as seen
/// through the SDK's EnqueueResult.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class IdempotencyTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task I01_DuplicateIdempotencyKey_IsSkipped_AndReportedAsDeduplicated()
    {
        var client = CreateClient();
        var queueId = NewQueueId();
        var key = Guid.NewGuid().ToString();

        var first = await client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 }, IdempotencyKey: key)]);
        Assert.True(first.Success);
        Assert.Equal(1, first.ItemsEnqueued);
        Assert.Equal(0, first.ItemsDeduplicated);

        // Different payload, same key - the key is what's deduped, not the content.
        var second = await client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 2 }, IdempotencyKey: key)]);
        Assert.True(second.Success);
        Assert.Equal(0, second.ItemsEnqueued);
        Assert.Equal(1, second.ItemsDeduplicated);

        var drained = await DrainAsync(client, queueId);
        var item = Assert.Single(drained);
        Assert.Equal(1, Seq(item.Item));
    }

    [Fact]
    public async Task I02_SameKeyOnDifferentQueues_DoesNotDedupe()
    {
        var client = CreateClient();
        var queueA = NewQueueId();
        var queueB = NewQueueId();
        var key = Guid.NewGuid().ToString();

        // Dedup state lives in the target QueueActor's own state, so it is scoped per queue.
        var a = await client.EnqueueAsync(queueA, [new EnqueueItemDto(new { seq = 1 }, IdempotencyKey: key)]);
        var b = await client.EnqueueAsync(queueB, [new EnqueueItemDto(new { seq = 1 }, IdempotencyKey: key)]);

        Assert.Equal(1, a.ItemsEnqueued);
        Assert.Equal(0, a.ItemsDeduplicated);
        Assert.Equal(1, b.ItemsEnqueued);
        Assert.Equal(0, b.ItemsDeduplicated);

        Assert.Single(await DrainAsync(client, queueA));
        Assert.Single(await DrainAsync(client, queueB));
    }

    [Fact]
    public async Task I03_OverLengthOrControlCharacterKey_SurfacesValidationError()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        // The key is folded into a state key, so the actor caps it at 128 chars.
        var overLength = new string('k', 129);
        var tooLong = await Assert.ThrowsAsync<ValidationException>(
            () => client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 }, IdempotencyKey: overLength)]));
        Assert.Contains("128", tooLong.Message);

        var controlChar = await Assert.ThrowsAsync<ValidationException>(
            () => client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 }, IdempotencyKey: "bad\u0001key")]));
        Assert.Contains("control characters", controlChar.Message, StringComparison.OrdinalIgnoreCase);

        // Exactly at the limit is accepted - the boundary is inclusive.
        var atLimit = await client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 }, IdempotencyKey: new string('k', 128))]);
        Assert.Equal(1, atLimit.ItemsEnqueued);

        // Neither rejected enqueue left anything behind; only the accepted one did.
        Assert.Single(await DrainAsync(client, queueId));
    }
}
