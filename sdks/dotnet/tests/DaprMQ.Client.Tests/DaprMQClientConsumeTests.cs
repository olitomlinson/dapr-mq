using DaprMQ.ApiServer.Grpc;
using DaprMQ.Client.Exceptions;
using Grpc.Core;
using Moq;

namespace DaprMQ.Client.Tests;

public class DaprMQClientConsumeTests
{
    private static (DaprMQClient client, FakeClientStreamWriter<ConsumeRequest> requests, FakeAsyncStreamReader<ConsumeResponse> responses)
        CreateClientWithFakeStream()
    {
        var requests = new FakeClientStreamWriter<ConsumeRequest>();
        var responses = new FakeAsyncStreamReader<ConsumeResponse>();
        var call = new AsyncDuplexStreamingCall<ConsumeRequest, ConsumeResponse>(
            requests, responses, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        return (ClientFor(call), requests, responses);
    }

    private static DaprMQClient ClientFor(AsyncDuplexStreamingCall<ConsumeRequest, ConsumeResponse> call)
    {
        var mockInvoker = new Mock<CallInvoker>();
        mockInvoker
            .Setup(i => i.AsyncDuplexStreamingCall(
                It.IsAny<Method<ConsumeRequest, ConsumeResponse>>(),
                It.IsAny<string>(),
                It.IsAny<CallOptions>()))
            .Returns(call);
        return new DaprMQClient(new HttpClient(), new global::DaprMQ.ApiServer.Grpc.DaprMQ.DaprMQClient(mockInvoker.Object));
    }

    private static ConsumeResponse Delivered(string lockId, int deliveryCount = 1) => new()
    {
        Delivered = new ConsumeDelivered
        {
            LockId = lockId, ItemJson = "{\"task\":\"x\"}", Priority = 1, LockExpiresAt = 123.0, DeliveryCount = deliveryCount
        }
    };

    [Fact]
    public async Task ConsumeAsync_YieldsDeliveredItem_AndAckWritesAckFrame()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();
        responses.Add(Delivered("L1", deliveryCount: 2));
        responses.Complete();

        var deliveries = new List<QueueDelivery>();
        await foreach (var delivery in client.ConsumeAsync("q"))
        {
            deliveries.Add(delivery);
            await delivery.AckAsync(CancellationToken.None);
        }

        var d = Assert.Single(deliveries);
        Assert.Equal("L1", d.LockId);
        Assert.Equal("x", d.Item.GetProperty("task").GetString());
        Assert.Equal(1, d.Priority);
        Assert.Equal(123.0, d.LockExpiresAt);
        Assert.Equal(2, d.DeliveryCount);

        var written = requests.WrittenSoFar;
        Assert.Equal(2, written.Count); // Start, then Ack
        Assert.Equal("L1", written[1].Ack.LockId);
    }

    [Fact]
    public async Task ConsumeAsync_StartFrame_UsesDefaults()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();
        responses.Complete();

        await foreach (var _ in client.ConsumeAsync("orders")) { }

        var start = requests.WrittenSoFar[0].Start;
        Assert.Equal("orders", start.QueueId);
        Assert.Equal(1, start.PrefetchCount);
        Assert.Equal(30, start.LockTtlSeconds);
        Assert.False(start.AllowCompetingConsumers);
    }

    [Fact]
    public async Task ConsumeAsync_StartFrame_CarriesOptions_WithLockTtlRoundedUpToWholeSeconds()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();
        responses.Complete();

        var options = new ConsumeOptions
        {
            PrefetchCount = 50, LockTtl = TimeSpan.FromMilliseconds(10_200), AllowCompetingConsumers = true
        };
        await foreach (var _ in client.ConsumeAsync("orders", options)) { }

        var start = requests.WrittenSoFar[0].Start;
        Assert.Equal(50, start.PrefetchCount);
        Assert.Equal(11, start.LockTtlSeconds);
        Assert.True(start.AllowCompetingConsumers);
    }

    [Fact]
    public async Task ConsumeAsync_NackAndDeadLetter_WriteTheirFrames()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();
        responses.Add(Delivered("L1"));
        responses.Add(Delivered("L2"));
        responses.Complete();

        await foreach (var delivery in client.ConsumeAsync("q", new ConsumeOptions { PrefetchCount = 2 }))
        {
            if (delivery.LockId == "L1")
            {
                await delivery.NackAsync(CancellationToken.None);
            }
            else
            {
                await delivery.DeadLetterAsync(CancellationToken.None);
            }
        }

        var written = requests.WrittenSoFar;
        Assert.Equal("L1", written[1].Nack.LockId);
        Assert.Equal("L2", written[2].DeadLetter.LockId);
    }

    [Fact]
    public async Task ConsumeAsync_SettleFailedFrame_GoesToCallback_AndTheStreamCarriesOn()
    {
        var (client, _, responses) = CreateClientWithFakeStream();
        responses.Add(new ConsumeResponse
        {
            SettleFailed = new ConsumeSettleFailed { LockId = "L0", ErrorCode = "LOCK_NOT_FOUND", Message = "gone" }
        });
        responses.Add(Delivered("L1"));
        responses.Complete();

        var failures = new List<(string LockId, DaprMQException Error)>();
        var options = new ConsumeOptions { OnSettleFailed = (lockId, error) => failures.Add((lockId, error)) };
        var seen = new List<string>();
        await foreach (var delivery in client.ConsumeAsync("q", options))
        {
            seen.Add(delivery.LockId);
        }

        var (lockId, error) = Assert.Single(failures);
        Assert.Equal("L0", lockId);
        Assert.IsType<LockNotFoundException>(error);
        Assert.Equal(["L1"], seen);
    }

    [Fact]
    public async Task ConsumeAsync_SettleFailedFrame_WithoutCallback_IsIgnored()
    {
        var (client, _, responses) = CreateClientWithFakeStream();
        responses.Add(new ConsumeResponse { SettleFailed = new ConsumeSettleFailed { LockId = "L0", ErrorCode = "LOCK_NOT_FOUND" } });
        responses.Add(Delivered("L1"));
        responses.Complete();

        var seen = new List<string>();
        await foreach (var delivery in client.ConsumeAsync("q"))
        {
            seen.Add(delivery.LockId);
        }

        Assert.Equal(["L1"], seen);
    }

    [Fact]
    public async Task ConsumeAsync_ErrorFrame_ThrowsMappedException()
    {
        var (client, _, responses) = CreateClientWithFakeStream();
        responses.Add(new ConsumeResponse { Error = new ConsumeError { ErrorCode = "VALIDATION_ERROR", Message = "bad prefetch" } });
        responses.Complete();

        var ex = await Assert.ThrowsAsync<ValidationException>(async () =>
        {
            await foreach (var _ in client.ConsumeAsync("q")) { }
        });
        Assert.Equal("bad prefetch", ex.Message);
    }

    [Fact]
    public async Task ConsumeAsync_SettlingAfterTheStreamEnded_ThrowsStreamClosed()
    {
        var (client, _, responses) = CreateClientWithFakeStream();
        responses.Add(Delivered("L1"));
        responses.Complete();

        QueueDelivery? kept = null;
        await foreach (var delivery in client.ConsumeAsync("q"))
        {
            kept = delivery;
        }

        await Assert.ThrowsAsync<StreamClosedException>(() => kept!.AckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConsumeAsync_ConsumerStopsAfterAcking_HalfClosesAndWaitsForServerBeforeCancelling()
    {
        var requests = new HalfCloseAwareWriter();
        var responses = new FakeAsyncStreamReader<ConsumeResponse>();
        var serverFinished = false;
        var cancelledEarly = false;
        _ = requests.HalfClosed.ContinueWith(async _ =>
        {
            await Task.Delay(100);
            serverFinished = true;
            responses.Complete();
        });
        var call = new AsyncDuplexStreamingCall<ConsumeRequest, ConsumeResponse>(
            requests, responses, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(),
            () => cancelledEarly |= !serverFinished);
        var client = ClientFor(call);
        responses.Add(Delivered("L1"));

        await foreach (var delivery in client.ConsumeAsync("q"))
        {
            await delivery.AckAsync(CancellationToken.None);
            break;
        }

        Assert.True(requests.HalfClosed.IsCompleted);
        Assert.False(cancelledEarly, "The SDK cancelled the call before the server finished, so settlements could be lost");
    }

    [Fact]
    public async Task ConsumeAsync_TokenCancelled_DeliveriesAfterTheHalfCloseAreNotHandedOut_ThenThrowsCancelled()
    {
        var requests = new HalfCloseAwareWriter();
        var responses = new FakeAsyncStreamReader<ConsumeResponse>();
        var call = new AsyncDuplexStreamingCall<ConsumeRequest, ConsumeResponse>(
            requests, responses, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        var client = ClientFor(call);
        responses.Add(Delivered("L1"));
        _ = requests.HalfClosed.ContinueWith(_ =>
        {
            responses.Add(Delivered("L2"));
            responses.Complete();
        });
        using var stop = new CancellationTokenSource();
        var seen = new List<string>();

        var consuming = Task.Run(async () =>
        {
            await foreach (var delivery in client.ConsumeAsync("q", null, stop.Token))
            {
                seen.Add(delivery.LockId);
                stop.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consuming.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["L1"], seen);
    }

    [Fact]
    public async Task ConsumeAsync_ServerNeverEndsAfterHalfClose_CancelsTheCallOnceTheDrainRunsOut()
    {
        var requests = new HalfCloseAwareWriter();
        var responses = new FakeAsyncStreamReader<ConsumeResponse>(); // never completed
        var cancelled = false;
        var call = new AsyncDuplexStreamingCall<ConsumeRequest, ConsumeResponse>(
            requests, responses, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(),
            () => cancelled = true);
        var client = ClientFor(call);
        responses.Add(Delivered("L1"));

        var original = DaprMQClient.SessionDrainTimeout;
        DaprMQClient.SessionDrainTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            await Task.Run(async () =>
            {
                await foreach (var _ in client.ConsumeAsync("q"))
                {
                    break;
                }
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DaprMQClient.SessionDrainTimeout = original;
        }

        Assert.True(cancelled, "the call must be cancelled once the drain runs out");
    }

    private sealed class HalfCloseAwareWriter : IClientStreamWriter<ConsumeRequest>
    {
        private readonly TaskCompletionSource _halfClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HalfClosed => _halfClosed.Task;

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(ConsumeRequest message) => Task.CompletedTask;

        public Task CompleteAsync()
        {
            _halfClosed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
