using System.Text.Json;
using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// Q-01..Q-08 from sdks/testing/INTEGRATION_TESTS.md - the SDK's core queue surface against a
/// real API server + Dapr sidecar.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class QueueBasicsTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task Q01_Enqueue_DequeueLocked_Ack_RoundTrips()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        var enqueued = await client.EnqueueAsync(queueId, [new EnqueueItemDto(new { task = "send_email" })]);
        Assert.True(enqueued.Success);
        Assert.Equal(1, enqueued.ItemsEnqueued);
        Assert.Equal(0, enqueued.ItemsDeduplicated);

        var dequeued = await client.DequeueLockedAsync(queueId);
        Assert.NotNull(dequeued);
        Assert.False(dequeued!.Locked);
        var item = Assert.Single(dequeued.Items);
        Assert.Equal("send_email", item.Item.GetProperty("task").GetString());
        Assert.NotEmpty(item.LockId);
        Assert.True(item.LockExpiresAt > 0);

        await client.AcknowledgeAsync(queueId, item.LockId);

        // Ack is the finalization point - nothing is left to hand out.
        Assert.Null(await client.DequeueLockedAsync(queueId));
    }

    [Fact]
    public async Task Q02_FifoOrderPreservedWithinAPriority()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 5);

        var drained = await DrainAsync(client, queueId);

        Assert.Equal([1, 2, 3, 4, 5], drained.Select(i => Seq(i.Item)));
        Assert.All(drained, i => Assert.Equal(1, i.Priority));
    }

    [Fact]
    public async Task Q03_PriorityOrdering_LowerPriorityFirst()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        // Enqueued worst-priority-first on purpose: if ordering were insertion-based rather than
        // priority-based this would come back 5, 2, 0.
        await client.EnqueueAsync(queueId, [
            new EnqueueItemDto(new { seq = 5 }, Priority: 5),
            new EnqueueItemDto(new { seq = 2 }, Priority: 2),
            new EnqueueItemDto(new { seq = 0 }, Priority: 0)
        ]);

        var drained = await DrainAsync(client, queueId);

        Assert.Equal([0, 2, 5], drained.Select(i => i.Priority));
        Assert.Equal([0, 2, 5], drained.Select(i => Seq(i.Item)));
    }

    [Fact]
    public async Task Q04_BulkEnqueue_ManyItemsInOneCall()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        // 250 crosses the 100-items-per-segment boundary twice, so this also covers the segmented
        // storage path rather than just a single-segment append.
        const int count = 250;
        var items = Enumerable.Range(1, count).Select(seq => new EnqueueItemDto(new { seq })).ToList();

        var result = await client.EnqueueAsync(queueId, items);
        Assert.True(result.Success);
        Assert.Equal(count, result.ItemsEnqueued);
        Assert.Equal(0, result.ItemsDeduplicated);

        var dequeued = await client.DequeueLockedAsync(queueId, count: count);
        Assert.NotNull(dequeued);
        Assert.Equal(count, dequeued!.Items.Count);
        Assert.Equal(Enumerable.Range(1, count), dequeued.Items.Select(i => Seq(i.Item)));
    }

    [Fact]
    public async Task Q05_BulkDequeue_ReturnsUpToNItemsInOrder()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 3);

        // Asking for more than the queue holds returns what's there, not an error.
        var dequeued = await client.DequeueLockedAsync(queueId, count: 10);
        Assert.NotNull(dequeued);
        Assert.Equal(3, dequeued!.Items.Count);
        Assert.Equal([1, 2, 3], dequeued.Items.Select(i => Seq(i.Item)));

        // Every returned item carries its own distinct lock.
        Assert.Equal(3, dequeued.Items.Select(i => i.LockId).Distinct().Count());
    }

    [Fact]
    public async Task Q06_DequeueOnEmptyQueue_ReturnsNull_NotAnError()
    {
        var client = CreateClient();

        // A queue that has never existed - created on demand, and empty.
        Assert.Null(await client.DequeueLockedAsync(NewQueueId()));

        // And a queue that has been drained to empty.
        var queueId = NewQueueId();
        await EnqueueSeqAsync(client, queueId, firstSeq: 1, count: 1);
        await DrainAsync(client, queueId);
        Assert.Null(await client.DequeueLockedAsync(queueId));
    }

    [Fact]
    public async Task Q07_ArbitraryJsonPayloads_RoundTripIntact()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        // Passed as a JsonElement rather than an anonymous object so the exact property names
        // (including PascalCase) survive - the SDK serializes with web defaults, which would
        // camelCase a POCO's members but writes a JsonElement verbatim.
        const string rawJson = """
        {
          "nested": { "deep": { "value": 42, "list": [1, 2, [3, 4]] } },
          "unicode": "héllo → 世界 🎉 A",
          "nullField": null,
          "boolField": false,
          "floatField": 1.5,
          "negative": -273,
          "emptyObject": {},
          "emptyArray": [],
          "PascalCase": "preserved",
          "with spaces": "and \"quotes\" and \\ backslash"
        }
        """;
        var payload = Json(rawJson);

        await client.EnqueueAsync(queueId, [new EnqueueItemDto(payload)]);

        var dequeued = await client.DequeueLockedAsync(queueId);
        Assert.NotNull(dequeued);
        var item = Assert.Single(dequeued!.Items).Item;

        Assert.Equal(42, item.GetProperty("nested").GetProperty("deep").GetProperty("value").GetInt32());
        Assert.Equal(3, item.GetProperty("nested").GetProperty("deep").GetProperty("list")[2][0].GetInt32());
        Assert.Equal("héllo → 世界 🎉 A", item.GetProperty("unicode").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("nullField").ValueKind);
        Assert.False(item.GetProperty("boolField").GetBoolean());
        Assert.Equal(1.5, item.GetProperty("floatField").GetDouble());
        Assert.Equal(-273, item.GetProperty("negative").GetInt32());
        Assert.Equal(JsonValueKind.Object, item.GetProperty("emptyObject").ValueKind);
        Assert.Empty(item.GetProperty("emptyArray").EnumerateArray());
        Assert.Equal("preserved", item.GetProperty("PascalCase").GetString());
        Assert.Equal("and \"quotes\" and \\ backslash", item.GetProperty("with spaces").GetString());

        // Whole-document equivalence, not just the fields spot-checked above.
        Assert.Equal(
            JsonSerializer.Serialize(payload),
            JsonSerializer.Serialize(item));
    }

    [Fact]
    public async Task Q08_InvalidEnqueueInput_SurfacesValidationError()
    {
        var client = CreateClient();
        var queueId = NewQueueId();

        var emptyBatch = await Assert.ThrowsAsync<ValidationException>(
            () => client.EnqueueAsync(queueId, []));
        Assert.Contains("empty", emptyBatch.Message, StringComparison.OrdinalIgnoreCase);

        var badPriority = await Assert.ThrowsAsync<ValidationException>(
            () => client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 }, Priority: -1)]));
        Assert.Contains("non-negative", badPriority.Message, StringComparison.OrdinalIgnoreCase);

        // The server's documented per-call ceiling is 10000 items (QueueController.cs), not the
        // 1000 the matrix text quotes - asserted here against the value the server enforces.
        var tooManyItems = Enumerable.Range(1, 10_001).Select(seq => new EnqueueItemDto(new { seq })).ToList();
        var oversized = await Assert.ThrowsAsync<ValidationException>(
            () => client.EnqueueAsync(queueId, tooManyItems));
        Assert.Contains("10000", oversized.Message);

        // A rejected enqueue leaves nothing behind.
        Assert.Null(await client.DequeueLockedAsync(queueId));
    }
}
