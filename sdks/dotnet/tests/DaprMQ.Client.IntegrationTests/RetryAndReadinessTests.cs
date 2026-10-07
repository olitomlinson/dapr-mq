using System.Diagnostics;
using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;
using Grpc.Net.Client;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>R-01 and R-05 from sdks/testing/RETRIES_AND_READINESS.md, on the shared stack.</summary>
[Collection(DaprMQClientCollection.Name)]
public class RetryAndReadinessTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task R01_WaitForReady_RunningStack_ReturnsAndEnqueueSucceeds()
    {
        var client = CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await client.WaitForReadyAsync(ct: cts.Token);

        var result = await client.EnqueueAsync(NewQueueId(), [new EnqueueItemDto(new { seq = 1 })]);
        Assert.Equal(1, result.ItemsEnqueued);
    }

    [Fact]
    public async Task R05_AutoIdempotencyKeys_FillsMissingKeys_AndKeepsGivenOnes()
    {
        var client = new DaprMQClient(Fixture.ApiClient, SharedGrpcChannel.For(Fixture.GrpcUrl),
            new DaprMQRetryOptions { AutoIdempotencyKeys = true });
        var queueId = NewQueueId();
        EnqueueItemDto[] items = [new(new { seq = 1 }, IdempotencyKey: $"mine-{Guid.NewGuid():N}"), new(new { seq = 2 })];

        await client.EnqueueAsync(queueId, items);
        var second = await client.EnqueueAsync(queueId, items);

        // The caller's key is kept (so the repeat is de-duplicated); the unkeyed item gets a fresh
        // key per call (so it's enqueued again).
        Assert.Equal(1, second.ItemsDeduplicated);
        Assert.Equal(1, second.ItemsEnqueued);
    }
}

/// <summary>R-02 and R-03 from sdks/testing/RETRIES_AND_READINESS.md, against a gateway in front of a worker.</summary>
[Collection(SplitStackCollection.Name)]
public class SplitStackRetryTests(SplitTopologyFixture fixture)
{
    private DaprMQClient CreateClient(TimeSpan retryTimeout)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return new DaprMQClient(fixture.GatewayClient, GrpcChannel.ForAddress(fixture.Environment.ApiServerGrpcUrl),
            new DaprMQRetryOptions { Timeout = retryTimeout });
    }

    [Fact]
    public async Task R02_WorkerReturnsWithinRetryTimeout_EnqueueSucceeds_AndIsStoredOnce()
    {
        var client = CreateClient(TimeSpan.FromSeconds(45));
        var queueId = $"r02-{Guid.NewGuid():N}";
        await fixture.Environment.StopWorkersAsync();
        try
        {
            var enqueue = client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 })]);
            await Task.Delay(TimeSpan.FromSeconds(3));
            await fixture.Environment.StartWorkersAsync();

            Assert.Equal(1, (await enqueue).ItemsEnqueued);
        }
        finally
        {
            await fixture.Environment.StartWorkersAsync();
            await fixture.Environment.WaitForReadyAsync(TimeSpan.FromMinutes(2));
        }

        var dequeued = await client.DequeueLockedAsync(queueId, count: 10);
        Assert.Single(dequeued!.Items);
    }

    [Fact]
    public async Task R03_WorkersStayDown_ThrowsUnavailable_BeforeRetryTimeout()
    {
        var client = CreateClient(TimeSpan.FromSeconds(8));
        await fixture.Environment.StopWorkersAsync();
        try
        {
            var sw = Stopwatch.StartNew();

            var ex = await Assert.ThrowsAsync<DaprMQUnavailableException>(() =>
                client.EnqueueAsync($"r03-{Guid.NewGuid():N}", [new EnqueueItemDto(new { seq = 1 })]));

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"elapsed {sw.Elapsed}");
            Assert.Equal("Enqueue", ex.Operation);
        }
        finally
        {
            await fixture.Environment.StartWorkersAsync();
            await fixture.Environment.WaitForReadyAsync(TimeSpan.FromMinutes(2));
        }
    }
}
