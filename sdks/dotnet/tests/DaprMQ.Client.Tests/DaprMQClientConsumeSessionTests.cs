using DaprMQ.ApiServer.Grpc;
using DaprMQ.Client.Exceptions;
using Grpc.Core;
using Moq;

namespace DaprMQ.Client.Tests;

public class DaprMQClientConsumeSessionTests
{
    private static (DaprMQClient client, FakeClientStreamWriter<ConsumeSessionRequest> requests, FakeAsyncStreamReader<ConsumeSessionResponse> responses)
        CreateClientWithFakeStream()
    {
        var requestWriter = new FakeClientStreamWriter<ConsumeSessionRequest>();
        var responseReader = new FakeAsyncStreamReader<ConsumeSessionResponse>();

        var call = new AsyncDuplexStreamingCall<ConsumeSessionRequest, ConsumeSessionResponse>(
            requestWriter,
            responseReader,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

        var mockInvoker = new Mock<CallInvoker>();
        mockInvoker
            .Setup(i => i.AsyncDuplexStreamingCall(
                It.IsAny<Method<ConsumeSessionRequest, ConsumeSessionResponse>>(),
                It.IsAny<string>(),
                It.IsAny<CallOptions>()))
            .Returns(call);

        var grpcClient = new global::DaprMQ.ApiServer.Grpc.DaprMQ.DaprMQClient(mockInvoker.Object);
        var client = new DaprMQClient(new HttpClient(), grpcClient);

        return (client, requestWriter, responseReader);
    }

    [Fact]
    public async Task ConsumeSessionAsync_YieldsDeliveredItem_AndAckWritesAckFrame()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();

        responses.Add(new ConsumeSessionResponse
        {
            SessionAssigned = new SessionAssigned { SessionId = "order-42", LeaseExpiresAt = 1780000200.0 }
        });
        responses.Add(new ConsumeSessionResponse
        {
            Delivered = new SessionDelivered { LockId = "L1", ItemJson = "{\"task\":\"x\"}", Priority = 0, LockExpiresAt = 123.0 }
        });
        responses.Complete();

        var deliveries = new List<SessionDelivery>();
        await foreach (var delivery in client.ConsumeSessionAsync("q", null, 30, 10))
        {
            deliveries.Add(delivery);
            await delivery.AckAsync(CancellationToken.None);
        }

        Assert.Single(deliveries);
        Assert.Equal("order-42", deliveries[0].SessionId);
        Assert.Equal("L1", deliveries[0].LockId);

        var written = requests.WrittenSoFar;
        Assert.Equal(2, written.Count); // Start, then Ack
        Assert.NotNull(written[0].Start);
        Assert.Equal("L1", written[1].Ack.LockId);
    }

    [Fact]
    public async Task ConsumeSessionAsync_ErrorFrame_ThrowsMappedException()
    {
        var (client, _, responses) = CreateClientWithFakeStream();

        responses.Add(new ConsumeSessionResponse
        {
            Error = new SessionError { ErrorCode = "NO_SESSIONS_AVAILABLE", Message = "none free" }
        });
        responses.Complete();

        await Assert.ThrowsAsync<NoSessionsAvailableException>(async () =>
        {
            await foreach (var _ in client.ConsumeSessionAsync("q", null, 30, 10)) { }
        });
    }

    [Fact]
    public async Task ConsumeSessionAsync_SessionLostFrame_ThrowsSessionLostException()
    {
        var (client, _, responses) = CreateClientWithFakeStream();

        responses.Add(new ConsumeSessionResponse
        {
            SessionAssigned = new SessionAssigned { SessionId = "order-42", LeaseExpiresAt = 1780000200.0 }
        });
        responses.Add(new ConsumeSessionResponse
        {
            SessionLost = new SessionLost { Message = "lease lost" }
        });
        responses.Complete();

        await Assert.ThrowsAsync<SessionLostException>(async () =>
        {
            await foreach (var _ in client.ConsumeSessionAsync("q", null, 30, 10)) { }
        });
    }

    [Fact]
    public async Task ConsumeSessionAsync_SessionDrainedFrame_EndsEnumerationWithoutException()
    {
        var (client, _, responses) = CreateClientWithFakeStream();

        responses.Add(new ConsumeSessionResponse
        {
            SessionAssigned = new SessionAssigned { SessionId = "order-42", LeaseExpiresAt = 1780000200.0 }
        });
        responses.Add(new ConsumeSessionResponse
        {
            SessionDrained = new SessionDrained { SessionId = "order-42" }
        });
        responses.Complete();

        var deliveries = new List<SessionDelivery>();
        await foreach (var delivery in client.ConsumeSessionAsync("q", null, 30, 10))
        {
            deliveries.Add(delivery);
        }

        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task ConsumeSessionAsync_ForwardsIdleTimeoutOnStartFrame()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();
        responses.Complete();

        await foreach (var _ in client.ConsumeSessionAsync("q", null, 30, 10, sessionIdleTimeoutSeconds: 5)) { }

        var start = requests.WrittenSoFar[0].Start;
        Assert.Equal(5, start.SessionIdleTimeoutSeconds);
    }

    [Fact]
    public async Task ConsumeSessionAsync_DeadLetterAsync_WritesDeadLetterFrame()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();

        responses.Add(new ConsumeSessionResponse { SessionAssigned = new SessionAssigned { SessionId = "s1", LeaseExpiresAt = 1.0 } });
        responses.Add(new ConsumeSessionResponse
        {
            Delivered = new SessionDelivered { LockId = "L2", ItemJson = "{}", Priority = 1, LockExpiresAt = 1.0 }
        });
        responses.Complete();

        await foreach (var delivery in client.ConsumeSessionAsync("q", "s1", 30, 10))
        {
            await delivery.DeadLetterAsync(CancellationToken.None);
        }

        var written = requests.WrittenSoFar;
        Assert.Equal("L2", written[1].DeadLetter.LockId);
    }

    [Fact]
    public async Task ConsumeSessionAsync_NackAsync_WritesNackFrame()
    {
        var (client, requests, responses) = CreateClientWithFakeStream();

        responses.Add(new ConsumeSessionResponse { SessionAssigned = new SessionAssigned { SessionId = "s1", LeaseExpiresAt = 1.0 } });
        responses.Add(new ConsumeSessionResponse
        {
            Delivered = new SessionDelivered { LockId = "L3", ItemJson = "{}", Priority = 1, LockExpiresAt = 1.0 }
        });
        responses.Complete();

        await foreach (var delivery in client.ConsumeSessionAsync("q", "s1", 30, 10))
        {
            await delivery.NackAsync(CancellationToken.None);
        }

        var written = requests.WrittenSoFar;
        Assert.Equal("L3", written[1].Nack.LockId);
    }

    /// <summary>
    /// A fake ConsumeSession call whose server, like the real one, ends its response stream only
    /// after it sees the client half-close - and takes a little while to do it (applying the acks it
    /// was sent, releasing the session). Records whether the SDK disposed (= cancelled) the call
    /// before that.
    /// </summary>
    private sealed class FinishingServerHarness
    {
        public readonly HalfCloseAwareWriter Requests = new();
        public readonly FakeAsyncStreamReader<ConsumeSessionResponse> Responses = new();
        public bool CancelledBeforeServerFinished;
        private bool _serverFinished;

        public DaprMQClient CreateClient()
        {
            _ = Task.Run(async () =>
            {
                await Requests.HalfClosed;
                await Task.Delay(100);
                _serverFinished = true;
                Responses.Complete();
            });

            var call = new AsyncDuplexStreamingCall<ConsumeSessionRequest, ConsumeSessionResponse>(
                Requests,
                Responses,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => CancelledBeforeServerFinished |= !_serverFinished); // disposing a live call cancels it

            var mockInvoker = new Mock<CallInvoker>();
            mockInvoker
                .Setup(i => i.AsyncDuplexStreamingCall(
                    It.IsAny<Method<ConsumeSessionRequest, ConsumeSessionResponse>>(),
                    It.IsAny<string>(),
                    It.IsAny<CallOptions>()))
                .Returns(call);
            return new DaprMQClient(new HttpClient(), new global::DaprMQ.ApiServer.Grpc.DaprMQ.DaprMQClient(mockInvoker.Object));
        }

        public void Deliver(string lockId) => Responses.Add(new ConsumeSessionResponse
        {
            Delivered = new SessionDelivered { LockId = lockId, ItemJson = "{}", Priority = 0, LockExpiresAt = 123.0 }
        });
    }

    /// <summary>
    /// docs/issues/resolved/session-stream-acks-lost-on-disconnect.md: when a consumer stops reading, any Ack
    /// frame it already sent must reach the server. Disposing the call cancels it on the server,
    /// which drops acks it hasn't read yet, so the SDK has to let the server finish first.
    /// </summary>
    [Fact]
    public async Task ConsumeSessionAsync_ConsumerStopsAfterAcking_WaitsForServerToFinishBeforeCancellingTheCall()
    {
        var harness = new FinishingServerHarness();
        var client = harness.CreateClient();
        harness.Deliver("L1");

        await foreach (var delivery in client.ConsumeSessionAsync("q", "s1", 30, 10))
        {
            await delivery.AckAsync(CancellationToken.None);
            break; // consumer shutting down
        }

        Assert.False(harness.CancelledBeforeServerFinished,
            "The SDK cancelled the call before the server finished, so acks the server hadn't read yet are lost");
    }

    /// <summary>
    /// The same, stopped the way SessionQueueConsumer.StopAsync stops: by cancelling the token passed
    /// to ConsumeSessionAsync. The caller still sees cancellation, but only after the server finishes.
    /// </summary>
    [Fact]
    public async Task ConsumeSessionAsync_TokenCancelledAfterAcking_WaitsForServerToFinish_ThenThrowsCancelled()
    {
        var harness = new FinishingServerHarness();
        var client = harness.CreateClient();
        harness.Deliver("L1");
        using var stop = new CancellationTokenSource();

        var consuming = Task.Run(async () =>
        {
            await foreach (var delivery in client.ConsumeSessionAsync("q", "s1", 30, 10, stop.Token))
            {
                await delivery.AckAsync(CancellationToken.None);
                stop.Cancel(); // consumer shutting down
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consuming.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(harness.CancelledBeforeServerFinished,
            "The SDK cancelled the call before the server finished, so acks the server hadn't read yet are lost");
    }

    private sealed class HalfCloseAwareWriter : IClientStreamWriter<ConsumeSessionRequest>
    {
        private readonly TaskCompletionSource _halfClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HalfClosed => _halfClosed.Task;

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(ConsumeSessionRequest message) => Task.CompletedTask;

        public Task CompleteAsync()
        {
            _halfClosed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
