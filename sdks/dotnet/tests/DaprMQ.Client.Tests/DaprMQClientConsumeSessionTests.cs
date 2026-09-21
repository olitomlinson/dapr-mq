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
}
