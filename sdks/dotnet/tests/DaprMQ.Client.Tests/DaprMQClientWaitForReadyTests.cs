using Grpc.Core;
using Grpc.Health.V1;
using Moq;
using ServingStatus = Grpc.Health.V1.HealthCheckResponse.Types.ServingStatus;

namespace DaprMQ.Client.Tests;

public class DaprMQClientWaitForReadyTests
{
    private static AsyncServerStreamingCall<HealthCheckResponse> Call(IAsyncStreamReader<HealthCheckResponse> reader) =>
        new(reader, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static (DaprMQClient client, List<HealthCheckRequest> requests) CreateClient(
        params Func<IAsyncStreamReader<HealthCheckResponse>>[] watchCalls)
    {
        var requests = new List<HealthCheckRequest>();
        var queue = new Queue<Func<IAsyncStreamReader<HealthCheckResponse>>>(watchCalls);
        var mockInvoker = new Mock<CallInvoker>();
        mockInvoker
            .Setup(i => i.AsyncServerStreamingCall(
                It.IsAny<Method<HealthCheckRequest, HealthCheckResponse>>(),
                It.IsAny<string>(),
                It.IsAny<CallOptions>(),
                It.IsAny<HealthCheckRequest>()))
            .Returns((Method<HealthCheckRequest, HealthCheckResponse> _, string _, CallOptions _, HealthCheckRequest request) =>
            {
                requests.Add(request);
                return Call(queue.Dequeue()());
            });

        var client = new DaprMQClient(
            new HttpClient(),
            new global::DaprMQ.ApiServer.Grpc.DaprMQ.DaprMQClient(mockInvoker.Object),
            new Health.HealthClient(mockInvoker.Object));
        return (client, requests);
    }

    private static Func<IAsyncStreamReader<HealthCheckResponse>> Statuses(params ServingStatus[] statuses) => () =>
    {
        var reader = new FakeAsyncStreamReader<HealthCheckResponse>();
        foreach (var status in statuses)
        {
            reader.Add(new HealthCheckResponse { Status = status });
        }
        return reader;
    };

    private static Func<IAsyncStreamReader<HealthCheckResponse>> Fails(StatusCode code) =>
        () => new ThrowingStreamReader(new RpcException(new Status(code, "")));

    [Fact]
    public async Task WaitForReadyAsync_NotServingThenServing_Returns()
    {
        var (client, requests) = CreateClient(Statuses(ServingStatus.NotServing, ServingStatus.Serving));

        await client.WaitForReadyAsync();

        Assert.Equal("daprmq.DaprMQ", Assert.Single(requests).Service);
    }

    [Fact]
    public async Task WaitForReadyAsync_UnavailableThenServing_RetriesAndReturns()
    {
        var (client, requests) = CreateClient(Fails(StatusCode.Unavailable), Statuses(ServingStatus.Serving));

        await client.WaitForReadyAsync();

        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task WaitForReadyAsync_StreamEndsBeforeServing_Reconnects()
    {
        var (client, requests) = CreateClient(
            () => { var r = new FakeAsyncStreamReader<HealthCheckResponse>(); r.Add(new HealthCheckResponse { Status = ServingStatus.NotServing }); r.Complete(); return r; },
            Statuses(ServingStatus.Serving));

        await client.WaitForReadyAsync();

        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task WaitForReadyAsync_Unimplemented_ThrowsNotSupported()
    {
        var (client, _) = CreateClient(Fails(StatusCode.Unimplemented));

        await Assert.ThrowsAsync<NotSupportedException>(() => client.WaitForReadyAsync());
    }

    [Fact]
    public async Task WaitForReadyAsync_NeverServing_HonoursCancellation()
    {
        var (client, _) = CreateClient(Statuses(ServingStatus.NotServing));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WaitForReadyAsync(cts.Token));
    }

    private sealed class ThrowingStreamReader(Exception ex) : IAsyncStreamReader<HealthCheckResponse>
    {
        public HealthCheckResponse Current => throw new InvalidOperationException();

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(ex);
    }
}
