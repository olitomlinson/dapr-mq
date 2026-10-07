using DaprMQ.Client.Exceptions;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;
using Grpc.Core;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// X-01..X-03 from sdks/testing/INTEGRATION_TESTS.md - constructing, disposing, and failing to
/// reach a DaprMQClient. Unlike the rest of the suite these build their own clients (and own
/// their transports), since ownership is exactly what's under test.
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class ClientLifecycleTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    private DaprMQClientOptions LiveOptions() => new()
    {
        HttpBaseAddress = new Uri(Fixture.Environment.ApiServerUrl),
        GrpcAddress = Fixture.GrpcUrl
    };

    [Fact]
    public async Task X01_ConstructFromOptions_PerformsAFullRoundTrip()
    {
        // The convenience constructor builds and owns both the HttpClient and the GrpcChannel.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        await using var client = new DaprMQClient(LiveOptions());

        var queueId = NewQueueId();
        const string sessionId = "options-ctor";

        // REST path.
        var enqueued = await client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 })]);
        Assert.Equal(1, enqueued.ItemsEnqueued);

        var dequeued = await client.DequeueLockedAsync(queueId);
        var item = Assert.Single(dequeued!.Items);
        Assert.Equal(1, Seq(item.Item));
        await client.AcknowledgeAsync(queueId, item.LockId);
        Assert.Null(await client.DequeueLockedAsync(queueId));

        // gRPC path, over the channel the same constructor built.
        await client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 2 }, SessionId: sessionId)]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delivered = new List<SessionDelivery>();
        await foreach (var delivery in client
            .ConsumeSessionAsync(queueId, sessionId, leaseSeconds: 300, prefetchCount: 5, cts.Token, sessionIdleTimeoutSeconds: 2)
            .WithCancellation(cts.Token))
        {
            delivered.Add(delivery);
            await delivery.AckAsync(cts.Token);
        }

        Assert.Equal(2, Seq(Assert.Single(delivered).Item));
    }

    [Fact]
    public async Task X02_Dispose_IsIdempotent_AndCancelsInFlightStreams()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        var client = new DaprMQClient(LiveOptions());

        var queueId = NewQueueId();
        const string sessionId = "disposed-mid-stream";
        await client.EnqueueAsync(queueId, [
            new EnqueueItemDto(new { seq = 1 }, SessionId: sessionId),
            new EnqueueItemDto(new { seq = 2 }, SessionId: sessionId)
        ]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stream = client.ConsumeSessionAsync(queueId, sessionId, leaseSeconds: 300, prefetchCount: 1, cts.Token);
        var enumerator = stream.GetAsyncEnumerator(cts.Token);

        // A live, mid-flight stream: one delivery in hand, another still to come.
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(1, Seq(enumerator.Current.Item));

        await client.DisposeAsync();

        // Disposing shuts the owned channel down, so the in-flight stream cannot continue.
        var continued = await Record.ExceptionAsync(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
            }
        });
        Assert.NotNull(continued);
        Assert.True(
            continued is RpcException or ObjectDisposedException or OperationCanceledException,
            $"expected a transport-level failure after dispose, got {continued.GetType().Name}: {continued.Message}");

        // Idempotent: a second (and third) dispose is a no-op rather than a throw.
        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task X03_R04_ServerUnreachable_SurfacesUnavailable_NotAHangOrADomainError()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        // Port 1 on loopback: nothing listens there, so connections are refused immediately.
        await using var client = new DaprMQClient(new DaprMQClientOptions
        {
            HttpBaseAddress = new Uri("http://127.0.0.1:1/"),
            GrpcAddress = "http://127.0.0.1:1",
            Retry = new DaprMQRetryOptions { Timeout = TimeSpan.FromSeconds(2) }
        });

        var queueId = NewQueueId();

        // REST: a refused connection certainly delivered nothing, so it's retried until RetryTimeout
        // runs out and then reported as unavailable - never a queue-domain outcome, never a hang.
        var restFailure = await Record.ExceptionAsync(
            () => client.EnqueueAsync(queueId, [new EnqueueItemDto(new { seq = 1 })]).WaitAsync(TimeSpan.FromSeconds(30)));
        var unavailable = Assert.IsType<DaprMQUnavailableException>(restFailure);
        Assert.Equal("Enqueue", unavailable.Operation);

        // gRPC: likewise an RpcException rather than one of the mapped session errors.
        var grpcFailure = await Record.ExceptionAsync(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await foreach (var _ in client
                .ConsumeSessionAsync(queueId, "any", leaseSeconds: 30, prefetchCount: 1, cts.Token)
                .WithCancellation(cts.Token))
            {
            }
        });
        Assert.NotNull(grpcFailure);
        Assert.IsType<RpcException>(grpcFailure);
        Assert.Equal(StatusCode.Unavailable, ((RpcException)grpcFailure).StatusCode);
    }
}
