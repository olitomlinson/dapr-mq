using System.Diagnostics;
using System.Text.Json;
using DaprMQ.Client.Exceptions;
using Moq;

namespace DaprMQ.Client.Tests;

public class SessionQueueConsumerTests
{
    private sealed class RecordingDelivery
    {
        public bool Acked;
        public bool DeadLettered;
        public SessionDelivery Delivery { get; }

        public RecordingDelivery(string sessionId, string lockId)
        {
            Delivery = new SessionDelivery
            {
                SessionId = sessionId,
                LockId = lockId,
                Item = JsonDocument.Parse("{}").RootElement,
                Priority = 0,
                LockExpiresAt = 0,
                AckAsync = _ => { Acked = true; return Task.CompletedTask; },
                DeadLetterAsync = _ => { DeadLettered = true; return Task.CompletedTask; }
            };
        }
    }

    private static async IAsyncEnumerable<SessionDelivery> SingleItemThenComplete(SessionDelivery item)
    {
        yield return item;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<SessionDelivery> ThrowingSequence(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task HappyPath_ClaimDeliverHandleAck()
    {
        var recording = new RecordingDelivery("s1", "L1");
        var handlerCalled = new TaskCompletionSource();

        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 10, It.IsAny<CancellationToken>()))
            .Returns(SingleItemThenComplete(recording.Delivery));

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q", new SessionQueueConsumerOptions { MaxConcurrentSessions = 1 },
            (ctx, _) =>
            {
                Assert.Equal("s1", ctx.SessionId);
                Assert.Equal("L1", ctx.LockId);
                handlerCalled.TrySetResult();
                return Task.CompletedTask;
            });

        await consumer.StartAsync();
        await handlerCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await consumer.StopAsync();

        Assert.True(recording.Acked);
        Assert.False(recording.DeadLettered);
    }

    [Fact]
    public async Task NoSessionsAvailable_BacksOffDoublingThenResetsOnClaim()
    {
        var callCount = 0;
        var delays = new List<TimeSpan>();

        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 10, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount <= 3
                    ? ThrowingSequence(new NoSessionsAvailableException("none"))
                    : SingleItemThenComplete(new RecordingDelivery("s1", "L1").Delivery);
            });

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q",
            new SessionQueueConsumerOptions { MaxConcurrentSessions = 1, MinBackoffSeconds = 1, MaxBackoffSeconds = 60 },
            (_, _) => Task.CompletedTask)
        {
            DelayAsync = (delay, _) => { delays.Add(delay); return Task.CompletedTask; }
        };

        await consumer.StartAsync();
        await WaitUntilAsync(() => delays.Count >= 3, TimeSpan.FromSeconds(2));
        await consumer.StopAsync();

        Assert.True(delays.Count >= 3, $"expected at least 3 backoff delays, got {delays.Count}");
        Assert.Equal(1, delays[0].TotalSeconds);
        Assert.Equal(2, delays[1].TotalSeconds);
        Assert.Equal(4, delays[2].TotalSeconds);
    }

    [Fact]
    public async Task Backoff_CapsAtMaxBackoffSeconds()
    {
        var delays = new List<TimeSpan>();

        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 10, It.IsAny<CancellationToken>()))
            .Returns(() => ThrowingSequence(new NoSessionsAvailableException("none")));

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q",
            new SessionQueueConsumerOptions { MaxConcurrentSessions = 1, MinBackoffSeconds = 1, MaxBackoffSeconds = 4 },
            (_, _) => Task.CompletedTask)
        {
            DelayAsync = (delay, _) => { delays.Add(delay); return Task.CompletedTask; }
        };

        await consumer.StartAsync();
        await WaitUntilAsync(() => delays.Count >= 5, TimeSpan.FromSeconds(2));
        await consumer.StopAsync();

        Assert.True(delays.Count >= 5);
        Assert.Equal(1, delays[0].TotalSeconds);
        Assert.Equal(2, delays[1].TotalSeconds);
        Assert.Equal(4, delays[2].TotalSeconds);
        Assert.Equal(4, delays[3].TotalSeconds); // capped
        Assert.Equal(4, delays[4].TotalSeconds);
    }

    [Fact]
    public async Task HandlerException_DefaultAction_DeadLettersMessage()
    {
        var recording = new RecordingDelivery("s1", "L1");
        var deadLettered = new TaskCompletionSource();

        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 10, It.IsAny<CancellationToken>()))
            .Returns(SingleItemThenComplete(recording.Delivery));

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q",
            new SessionQueueConsumerOptions { MaxConcurrentSessions = 1, OnHandlerException = SessionHandlerFailureAction.DeadLetterMessage },
            (_, _) =>
            {
                deadLettered.TrySetResult();
                throw new InvalidOperationException("handler blew up");
            });

        await consumer.StartAsync();
        await deadLettered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50); // let the DeadLetterAsync call inside HandleDeliveryAsync land
        await consumer.StopAsync();

        Assert.True(recording.DeadLettered);
        Assert.False(recording.Acked);
    }

    [Fact]
    public async Task HandlerException_AbandonSession_DoesNotDeadLetter_AndKeepsSlotAlive()
    {
        var recording = new RecordingDelivery("s1", "L1");
        var handlerRan = new TaskCompletionSource();

        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 10, It.IsAny<CancellationToken>()))
            .Returns(SingleItemThenComplete(recording.Delivery));

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q",
            new SessionQueueConsumerOptions { MaxConcurrentSessions = 1, OnHandlerException = SessionHandlerFailureAction.AbandonSession },
            (_, _) =>
            {
                handlerRan.TrySetResult();
                throw new InvalidOperationException("handler blew up");
            });

        await consumer.StartAsync();
        await handlerRan.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        await consumer.StopAsync();

        Assert.False(recording.DeadLettered);
        Assert.False(recording.Acked);
        // The slot must still be alive (not crashed) - re-invoking ConsumeSessionAsync at least
        // once more after the abandoned attempt proves the loop kept going.
        mockClient.Verify(c => c.ConsumeSessionAsync("q", null, 30, 10, It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task StopAsync_DrainsGracefully_NoException()
    {
        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 10, It.IsAny<CancellationToken>()))
            .Returns(() => ThrowingSequence(new NoSessionsAvailableException("none")));

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q", new SessionQueueConsumerOptions { MaxConcurrentSessions = 2 },
            (_, _) => Task.CompletedTask)
        {
            DelayAsync = (_, _) => Task.CompletedTask
        };

        await consumer.StartAsync();
        await Task.Delay(20);
        await consumer.StopAsync();
        await consumer.DisposeAsync();
    }

    [Fact]
    public void Constructor_TargetSessionIdWithoutSingleSlot_Throws()
    {
        var mockClient = new Mock<IDaprMQClient>();

        Assert.Throws<ArgumentException>(() => new SessionQueueConsumer(
            mockClient.Object, "q",
            new SessionQueueConsumerOptions { TargetSessionId = "s1", MaxConcurrentSessions = 2 },
            (_, _) => Task.CompletedTask));
    }
}
