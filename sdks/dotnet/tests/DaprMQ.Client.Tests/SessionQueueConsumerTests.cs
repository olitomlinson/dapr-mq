using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        public bool Nacked;
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
                DeadLetterAsync = _ => { DeadLettered = true; return Task.CompletedTask; },
                NackAsync = _ => { Nacked = true; return Task.CompletedTask; }
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
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
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
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
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
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
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
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
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
    public async Task HandlerException_NackMessage_NacksInsteadOfDeadLettering()
    {
        var recording = new RecordingDelivery("s1", "L1");
        var handled = new TaskCompletionSource();

        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .Returns(SingleItemThenComplete(recording.Delivery));

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q",
            new SessionQueueConsumerOptions { MaxConcurrentSessions = 1, OnHandlerException = SessionHandlerFailureAction.NackMessage },
            (_, _) =>
            {
                handled.TrySetResult();
                throw new InvalidOperationException("handler blew up");
            });

        await consumer.StartAsync();
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50); // let the NackAsync call inside HandleDeliveryAsync land
        await consumer.StopAsync();

        Assert.True(recording.Nacked);
        Assert.False(recording.DeadLettered);
        Assert.False(recording.Acked);
    }

    [Fact]
    public async Task HandlerException_AbandonSession_DoesNotDeadLetter_AndKeepsSlotAlive()
    {
        var recording = new RecordingDelivery("s1", "L1");
        var handlerRan = new TaskCompletionSource();

        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
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
        mockClient.Verify(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()),
            Times.AtLeast(2));
    }

    /// <summary>
    /// Like the real stream: hands out the given deliveries, then waits for more; stopping (the
    /// token) hands out nothing further and ends with cancellation.
    /// </summary>
    private static async IAsyncEnumerable<SessionDelivery> DeliverThenWait(
        IEnumerable<SessionDelivery> items, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
        await Task.Delay(Timeout.Infinite, ct);
    }

    private static Mock<IDaprMQClient> ClientStreaming(params SessionDelivery[] items)
    {
        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, It.IsAny<int>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .Returns((string _, string? _, int _, int _, CancellationToken ct, int _) => DeliverThenWait(items, ct));
        return mockClient;
    }

    [Fact]
    public async Task StopAsync_DuringAHandler_LetsItFinishAndAck_BeforeClosingTheStream()
    {
        var recording = new RecordingDelivery("s1", "L1");
        var entered = new TaskCompletionSource();
        var streamClosed = false; // stopped by its token, or by the consumer leaving the loop
        async IAsyncEnumerable<SessionDelivery> Stream([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                await foreach (var d in DeliverThenWait([recording.Delivery], ct))
                {
                    yield return d;
                }
            }
            finally
            {
                streamClosed = true;
            }
        }
        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .Returns((string _, string? _, int _, int _, CancellationToken ct, int _) => Stream(ct));
        bool handlerCancelled = false, streamClosedUnderHandler = false;

        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q", new SessionQueueConsumerOptions { MaxConcurrentSessions = 1 },
            async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(200);
                handlerCancelled = ct.IsCancellationRequested;
                streamClosedUnderHandler = streamClosed;
            });

        await consumer.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await consumer.StopAsync();

        Assert.False(handlerCancelled);
        Assert.False(streamClosedUnderHandler);
        Assert.True(recording.Acked, "StopAsync returned before the in-flight message was acked");
        Assert.True(streamClosed, "StopAsync returned before the stream closed");
    }

    [Fact]
    public async Task StopAsync_PastTheDrainTimeout_CancelsTheHandler()
    {
        var recording = new RecordingDelivery("s1", "L1");
        var entered = new TaskCompletionSource();
        var handlerCancelled = false;
        var consumer = new SessionQueueConsumer(
            ClientStreaming(recording.Delivery).Object, "q",
            new SessionQueueConsumerOptions { MaxConcurrentSessions = 1, DrainTimeout = TimeSpan.FromMilliseconds(100) },
            async (_, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                finally
                {
                    handlerCancelled = ct.IsCancellationRequested;
                }
            });

        await consumer.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        await consumer.StopAsync();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync took {sw.Elapsed}");
        Assert.True(handlerCancelled);
        Assert.False(recording.Acked);
        Assert.False(recording.DeadLettered);
    }

    [Fact]
    public async Task StopAsync_WhileIdle_ClosesTheStreamPromptly()
    {
        var opened = new TaskCompletionSource<CancellationToken>();
        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .Returns((string _, string? _, int _, int _, CancellationToken ct, int _) =>
            {
                opened.TrySetResult(ct);
                return DeliverThenWait([], ct);
            });
        var consumer = new SessionQueueConsumer(
            mockClient.Object, "q", new SessionQueueConsumerOptions { MaxConcurrentSessions = 1 }, (_, _) => Task.CompletedTask);

        await consumer.StartAsync();
        var streamToken = await opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        await consumer.StopAsync();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync took {sw.Elapsed}");
        Assert.True(streamToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StopAsync_DuringAHandler_DoesNotStartAPrefetchedMessage()
    {
        var first = new RecordingDelivery("s1", "L1");
        var second = new RecordingDelivery("s1", "L2");
        var entered = new TaskCompletionSource();
        var handled = new List<string>();
        var consumer = new SessionQueueConsumer(
            ClientStreaming(first.Delivery, second.Delivery).Object, "q",
            new SessionQueueConsumerOptions { MaxConcurrentSessions = 1, PrefetchCount = 2 },
            async (ctx, _) =>
            {
                handled.Add(ctx.LockId);
                entered.TrySetResult();
                await Task.Delay(100);
            });

        await consumer.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await consumer.StopAsync();

        Assert.Equal(["L1"], handled);
        Assert.True(first.Acked);
        Assert.False(second.Acked);
    }

    [Fact]
    public async Task StopAsync_DrainsGracefully_NoException()
    {
        var mockClient = new Mock<IDaprMQClient>();
        mockClient
            .Setup(c => c.ConsumeSessionAsync("q", null, 30, 1, It.IsAny<CancellationToken>(), It.IsAny<int>()))
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
