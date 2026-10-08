using System.Collections.Concurrent;
using System.Diagnostics;
using DaprMQ.Client.IntegrationTests.Infrastructure;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.Client.IntegrationTests;

/// <summary>
/// K-01..K-11 from sdks/testing/INTEGRATION_TESTS.md - the managed SessionQueueConsumer loop
/// against a real Dapr sidecar. K-02 additionally proves the acceptance criterion from the
/// sessions plan's §8/§6.10.3: MaxConcurrentSessions >= 2 preserves per-session FIFO order and
/// gives genuine cross-session throughput isolation (a slow session's handler doesn't stall a
/// fast one).
/// </summary>
[Collection(DaprMQClientCollection.Name)]
public class SessionQueueConsumerTests(DaprTestFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>
    /// Ack/DeadLetter are fire-and-forget frames on the ConsumeSession request stream, so a
    /// consumer stopped the instant its last handler returns can tear the stream down before the
    /// server applies the final one. Every test that asserts on post-consumption queue state
    /// therefore lets the server settle first - long enough to cover its 200ms poll cadence plus
    /// the idle-drain window the consumer is configured with.
    /// </summary>
    private const int SettleMilliseconds = 3000;

    private const int IdleTimeoutSeconds = 2;

    private async Task EnqueueAsync(string queueId, string sessionId, int seq)
    {
        var client = CreateClient();
        var result = await client.EnqueueAsync(queueId, [new EnqueueItemDto(new { sessionId, seq }, SessionId: sessionId)]);
        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsEnqueued);
    }

    private async Task EnqueueSessionAsync(string queueId, string sessionId, int count)
    {
        var client = CreateClient();
        var items = Enumerable.Range(1, count)
            .Select(seq => new EnqueueItemDto(new { sessionId, seq }, SessionId: sessionId))
            .ToList();
        var result = await client.EnqueueAsync(queueId, items);
        Assert.Equal(count, result.ItemsEnqueued);
    }

    private static SessionQueueConsumerOptions Options(
        int maxConcurrentSessions = 1,
        int leaseSeconds = 30,
        int prefetchCount = 10,
        SessionHandlerFailureAction onHandlerException = SessionHandlerFailureAction.DeadLetterMessage,
        string? targetSessionId = null,
        int sessionIdleTimeoutSeconds = IdleTimeoutSeconds,
        int minBackoffSeconds = 1,
        int maxBackoffSeconds = 2,
        TimeSpan? drainTimeout = null) => new()
        {
            MaxConcurrentSessions = maxConcurrentSessions,
            TargetSessionId = targetSessionId,
            LeaseSeconds = leaseSeconds,
            PrefetchCount = prefetchCount,
            OnHandlerException = onHandlerException,
            SessionIdleTimeoutSeconds = sessionIdleTimeoutSeconds,
            MinBackoffSeconds = minBackoffSeconds,
            MaxBackoffSeconds = maxBackoffSeconds,
            DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30)
        };

    /// <summary>Everything still sitting on a session's queue, claimed fresh so nothing is missed.</summary>
    private async Task<List<DequeueLockedItemDto>> RemainingOnSessionAsync(string queueId, string sessionId)
    {
        var client = CreateClient();
        var lease = await ClaimWhenFreeAsync(client, queueId, sessionId);
        return await DrainAsync(client, SessionQueueId(queueId, sessionId), leaseId: lease.LeaseId);
    }

    [Fact]
    public async Task K01_HandlerSuccess_AutoAcks_AndQueueEndsEmpty()
    {
        var queueId = NewQueueId();
        const string sessionId = "auto-ack";
        const int itemCount = 4;
        await EnqueueSessionAsync(queueId, sessionId, itemCount);

        var handled = new ConcurrentQueue<int>();
        var allHandled = new TaskCompletionSource();

        await using var consumer = new SessionQueueConsumer(CreateClient(), queueId, Options(), (ctx, _) =>
        {
            handled.Enqueue(Seq(ctx.Item));
            if (handled.Count == itemCount)
            {
                allHandled.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await consumer.StartAsync();
        try
        {
            await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.Equal([1, 2, 3, 4], handled);

        // Returning normally acks - nothing is left behind, and nothing was dead-lettered.
        Assert.Empty(await RemainingOnSessionAsync(queueId, sessionId));
        Assert.Null(await CreateClient().DequeueLockedAsync(DeadLetterQueueId(SessionQueueId(queueId, sessionId))));
    }

    [Fact]
    public async Task K02_MultiSession_PreservesPerSessionOrder_AndIsolatesThroughput()
    {
        var queueId = NewQueueId();
        const int itemsPerSession = 5;

        for (var seq = 1; seq <= itemsPerSession; seq++)
        {
            await EnqueueAsync(queueId, "fast", seq);
            await EnqueueAsync(queueId, "slow", seq);
        }

        var observedSeqs = new ConcurrentDictionary<string, List<int>>();
        var fastDone = new TaskCompletionSource();
        var slowDone = new TaskCompletionSource();
        var stopwatch = Stopwatch.StartNew();
        double fastCompletedAtMs = -1;

        var client = CreateClient();
        var options = new SessionQueueConsumerOptions
        {
            MaxConcurrentSessions = 2,
            LeaseSeconds = 30,
            PrefetchCount = 10,
            MinBackoffSeconds = 1,
            MaxBackoffSeconds = 2
        };

        await using var consumer = new SessionQueueConsumer(client, queueId, options, async (ctx, ct) =>
        {
            if (ctx.SessionId == "slow")
            {
                await Task.Delay(300, ct);
            }

            var seq = ctx.Item.GetProperty("seq").GetInt32();
            var list = observedSeqs.GetOrAdd(ctx.SessionId, _ => []);
            lock (list)
            {
                list.Add(seq);
                if (list.Count == itemsPerSession)
                {
                    if (ctx.SessionId == "fast")
                    {
                        fastCompletedAtMs = stopwatch.Elapsed.TotalMilliseconds;
                        fastDone.TrySetResult();
                    }
                    else if (ctx.SessionId == "slow")
                    {
                        slowDone.TrySetResult();
                    }
                }
            }
        });

        await consumer.StartAsync();
        try
        {
            await Task.WhenAll(fastDone.Task, slowDone.Task).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.Equal(Enumerable.Range(1, itemsPerSession), observedSeqs["fast"]);
        Assert.Equal(Enumerable.Range(1, itemsPerSession), observedSeqs["slow"]);

        // Cross-session isolation: "slow" deliberately takes >= itemsPerSession * 300ms to fully
        // process (each item sleeps 300ms, processed sequentially within its own session stream).
        // "fast" must complete well within that window, proving it wasn't queued up behind "slow"
        // on a shared resource - each session runs on its own independent stream/slot.
        var slowMinimumDurationMs = itemsPerSession * 300;
        Assert.True(fastCompletedAtMs >= 0, "fast session never completed");
        Assert.True(fastCompletedAtMs < slowMinimumDurationMs,
            $"fast session took {fastCompletedAtMs}ms, expected well under the slow session's own {slowMinimumDurationMs}ms floor - cross-session isolation appears broken");
    }

    [Fact]
    public async Task K03_HandlerThrowsWithDeadLetterMessage_DeadLettersItem_AndSessionContinues()
    {
        var queueId = NewQueueId();
        const string sessionId = "poison-one";
        await EnqueueSessionAsync(queueId, sessionId, 3);

        var handled = new ConcurrentQueue<int>();
        var allHandled = new TaskCompletionSource();

        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId, Options(onHandlerException: SessionHandlerFailureAction.DeadLetterMessage),
            (ctx, _) =>
            {
                var seq = Seq(ctx.Item);
                handled.Enqueue(seq);
                if (handled.Count == 3)
                {
                    allHandled.TrySetResult();
                }

                return seq == 2
                    ? Task.FromException(new InvalidOperationException("poison item"))
                    : Task.CompletedTask;
            });

        await consumer.StartAsync();
        try
        {
            await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        // The session was not abandoned: item 3 was delivered after the failure, in order.
        Assert.Equal([1, 2, 3], handled);

        var dead = await DrainAsync(CreateClient(), DeadLetterQueueId(SessionQueueId(queueId, sessionId)));
        Assert.Equal(2, Seq(Assert.Single(dead).Item));

        // 1 and 3 were acked normally, 2 went to the DLQ - the session queue is empty either way.
        Assert.Empty(await RemainingOnSessionAsync(queueId, sessionId));
    }

    [Fact]
    public async Task K04_HandlerThrowsWithAbandonSession_ReleasesSession_AndItemIsRedelivered()
    {
        var queueId = NewQueueId();
        const string sessionId = "abandon-me";
        await EnqueueSessionAsync(queueId, sessionId, 1);

        var attempts = 0;
        var secondAttempt = new TaskCompletionSource();

        // LeaseSeconds doubles as the delivered item's lock TTL, so a short one is what makes the
        // abandoned item come back promptly rather than after the default 30s.
        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId,
            Options(leaseSeconds: 3, sessionIdleTimeoutSeconds: 30, onHandlerException: SessionHandlerFailureAction.AbandonSession),
            (ctx, _) =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    // Abandon: rethrown by the consumer, which unwinds this slot's stream and so
                    // releases the session without acking or dead-lettering the item.
                    return Task.FromException(new InvalidOperationException("abandon this session"));
                }

                secondAttempt.TrySetResult();
                return Task.CompletedTask;
            });

        await consumer.StartAsync();
        try
        {
            await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.True(attempts >= 2, $"expected the abandoned item to be redelivered, saw {attempts} attempt(s)");

        // Redelivered and then handled successfully - nothing dead-lettered, nothing left over.
        Assert.Null(await CreateClient().DequeueLockedAsync(DeadLetterQueueId(SessionQueueId(queueId, sessionId))));
        Assert.Empty(await RemainingOnSessionAsync(queueId, sessionId));
    }

    [Fact]
    public async Task K05_HandlerThrowsWithBoth_DeadLettersItem_AndAbandonsSession()
    {
        var queueId = NewQueueId();
        const string sessionId = "dead-and-abandoned";
        await EnqueueSessionAsync(queueId, sessionId, 2);

        var handled = new ConcurrentQueue<int>();
        var firstFailed = new TaskCompletionSource();
        var secondHandled = new TaskCompletionSource();

        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId,
            Options(leaseSeconds: 5, prefetchCount: 1, onHandlerException: SessionHandlerFailureAction.Both),
            (ctx, _) =>
            {
                var seq = Seq(ctx.Item);
                handled.Enqueue(seq);
                if (seq == 1)
                {
                    firstFailed.TrySetResult();
                    return Task.FromException(new InvalidOperationException("poison and abandon"));
                }

                secondHandled.TrySetResult();
                return Task.CompletedTask;
            });

        await consumer.StartAsync();
        try
        {
            await firstFailed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // The slot re-claims the same (only) session and carries on with item 2, which proves
            // the abandon half: the stream really was torn down and a fresh one took its place.
            await secondHandled.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        // The dead-letter half: item 1 is on the session's DLQ, not merely abandoned.
        var dead = await DrainAsync(CreateClient(), DeadLetterQueueId(SessionQueueId(queueId, sessionId)));
        Assert.Equal(1, Seq(Assert.Single(dead).Item));

        Assert.Empty(await RemainingOnSessionAsync(queueId, sessionId));
    }

    [Fact]
    public async Task K06_TargetSessionId_OnlyConsumesThatSession()
    {
        var queueId = NewQueueId();
        await EnqueueSessionAsync(queueId, "wanted", 3);
        await EnqueueSessionAsync(queueId, "unwanted", 2);

        // A targeted claim only ever occupies one slot, so any other pool size is rejected up front.
        var ex = Assert.Throws<ArgumentException>(() => new SessionQueueConsumer(
            CreateClient(), queueId, Options(maxConcurrentSessions: 2, targetSessionId: "wanted"), (_, _) => Task.CompletedTask));
        Assert.Contains(nameof(SessionQueueConsumerOptions.TargetSessionId), ex.Message);

        var handled = new ConcurrentBag<string>();
        var allHandled = new TaskCompletionSource();

        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId, Options(maxConcurrentSessions: 1, targetSessionId: "wanted"),
            (ctx, _) =>
            {
                handled.Add(ctx.SessionId);
                if (handled.Count == 3)
                {
                    allHandled.TrySetResult();
                }

                return Task.CompletedTask;
            });

        await consumer.StartAsync();
        try
        {
            await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Extra dwell time: if targeting leaked, this is when "unwanted" would show up.
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.All(handled, s => Assert.Equal("wanted", s));
        Assert.Equal(3, handled.Count);

        Assert.Empty(await RemainingOnSessionAsync(queueId, "wanted"));
        Assert.Equal(2, (await RemainingOnSessionAsync(queueId, "unwanted")).Count);
    }

    [Fact]
    public async Task K07_MaxConcurrentSessions_IsNeverExceeded()
    {
        var queueId = NewQueueId();
        const int sessionCount = 4;
        const int itemsPerSession = 10;
        const int maxConcurrentSessions = 2;
        const int totalItems = sessionCount * itemsPerSession;
        for (var i = 1; i <= sessionCount; i++)
        {
            await EnqueueSessionAsync(queueId, $"s{i}", itemsPerSession);
        }

        var active = new ConcurrentDictionary<string, byte>();
        var peakConcurrency = 0;
        var handledCount = 0;
        var allHandled = new TaskCompletionSource();

        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId, Options(maxConcurrentSessions: maxConcurrentSessions, prefetchCount: 1),
            async (ctx, ct) =>
            {
                active[ctx.SessionId] = 0;

                // Sampled while the handler is in flight, so concurrently-held sessions overlap here.
                var observed = active.Count;
                var previousPeak = Volatile.Read(ref peakConcurrency);
                while (observed > previousPeak &&
                       Interlocked.CompareExchange(ref peakConcurrency, observed, previousPeak) != previousPeak)
                {
                    previousPeak = Volatile.Read(ref peakConcurrency);
                }

                await Task.Delay(150, ct);
                active.TryRemove(ctx.SessionId, out _);
                if (Interlocked.Increment(ref handledCount) == totalItems)
                {
                    allHandled.TrySetResult();
                }
            });

        await consumer.StartAsync();
        try
        {
            // Waits for a full drain of every session, not a fixed observation window. With more
            // sessions than slots this used to hang - the later sessions were never reached at all
            // - so the cap could only be observed over a few seconds of steady delivery. Fixed by
            // least-recently-claimed claim ordering (see K-11), so the consumer now works through
            // all four sessions, and the cap is asserted over the whole run rather than a sample of
            // it.
            await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(60));
            // Extra dwell time: a breach of the cap after the last item would still be caught.
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.Equal(totalItems, Volatile.Read(ref handledCount));
        Assert.True(peakConcurrency <= maxConcurrentSessions,
            $"observed {peakConcurrency} sessions in flight at once, cap is {maxConcurrentSessions}");
        // Guards against the cap being trivially satisfied by never running two sessions at all.
        Assert.Equal(maxConcurrentSessions, peakConcurrency);

        // Every session was reached and emptied - the cap bounds concurrency, it does not strand
        // the sessions beyond it.
        for (var i = 1; i <= sessionCount; i++)
        {
            Assert.Empty(await RemainingOnSessionAsync(queueId, $"s{i}"));
        }
    }

    [Fact]
    public async Task K08_EmptyQueue_BacksOff_ThenPicksUpSessionsEnqueuedLater()
    {
        var queueId = NewQueueId();
        const string sessionId = "arrives-late";

        var handled = new TaskCompletionSource<int>();
        var startedAt = Stopwatch.StartNew();

        // Nothing to claim yet: every slot loop will miss and back off (1s -> 2s, capped).
        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId, Options(minBackoffSeconds: 1, maxBackoffSeconds: 2),
            (ctx, _) =>
            {
                handled.TrySetResult(Seq(ctx.Item));
                return Task.CompletedTask;
            });

        await consumer.StartAsync();
        try
        {
            // Long enough for several backoff rounds, so the pickup below is genuinely a recovery
            // from a backed-off state rather than a first-attempt hit.
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.False(handled.Task.IsCompleted, "nothing should have been handled from an empty queue");

            await EnqueueSessionAsync(queueId, sessionId, 1);

            // Must arrive within roughly one MaxBackoffSeconds window, not stay stuck backed off.
            var seq = await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, seq);
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.True(startedAt.Elapsed > TimeSpan.FromSeconds(5));
        Assert.Empty(await RemainingOnSessionAsync(queueId, sessionId));
    }

    [Fact]
    public async Task K09_Stop_DrainsInFlightHandlers_AndReleasesSessions()
    {
        var queueId = NewQueueId();
        const string sessionId = "drain-on-stop";
        await EnqueueSessionAsync(queueId, sessionId, 1);

        var handlerEntered = new TaskCompletionSource();
        var handlerCompleted = false;
        var handlerCancelled = false;

        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId, Options(drainTimeout: TimeSpan.FromSeconds(30)),
            async (_, ct) =>
            {
                handlerEntered.TrySetResult();
                // StopAsync lands while this runs; the token is cancelled only past DrainTimeout.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                handlerCancelled = ct.IsCancellationRequested;
                handlerCompleted = true;
            });

        await consumer.StartAsync();
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var stopWatch = Stopwatch.StartNew();
        await consumer.StopAsync();
        stopWatch.Stop();

        Assert.True(handlerCompleted, "StopAsync returned before the in-flight handler finished");
        Assert.False(handlerCancelled);
        Assert.True(stopWatch.Elapsed < TimeSpan.FromSeconds(30), $"StopAsync took {stopWatch.Elapsed}, past DrainTimeout");

        // Closing the streams is what releases the sessions, so the session is claimable at once,
        // and the handler's ack was applied before that.
        var reclaimed = await CreateClient().AcceptSessionAsync(queueId, sessionId, leaseSeconds: 60);
        Assert.NotNull(reclaimed);
        Assert.Equal(sessionId, reclaimed!.SessionId);
        Assert.Empty(await DrainAsync(CreateClient(), SessionQueueId(queueId, sessionId), leaseId: reclaimed.LeaseId));
    }

    [Fact]
    public async Task K10_ExternalCancellationToken_StopsTheConsumer()
    {
        var queueId = NewQueueId();
        const string sessionId = "external-cancel";
        await EnqueueSessionAsync(queueId, sessionId, 1);

        var handlerEntered = new TaskCompletionSource();
        var handledCount = 0;

        using var externalCts = new CancellationTokenSource();
        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId, Options(), (_, _) =>
            {
                Interlocked.Increment(ref handledCount);
                handlerEntered.TrySetResult();
                return Task.CompletedTask;
            });

        await consumer.StartAsync(externalCts.Token);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(SettleMilliseconds);

        await externalCts.CancelAsync();

        // Cancelling the token the consumer was started with stops its slots, so the session is
        // released and more work enqueued afterwards is never picked up.
        var released = await ClaimWhenFreeAsync(CreateClient(), queueId, sessionId);
        Assert.Equal(sessionId, released.SessionId);

        var countAtCancellation = Volatile.Read(ref handledCount);
        await CreateClient().ReleaseSessionAsync(queueId, sessionId, released.LeaseId);
        await EnqueueSessionAsync(queueId, sessionId, 1);
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(countAtCancellation, Volatile.Read(ref handledCount));
    }

    // K-11 asserts that SessionIdleTimeoutSeconds lets a consumer move on to another session.
    // Previously skipped: a drained session was re-claimed by the same slot forever, so a consumer
    // never reached any session beyond its first MaxConcurrentSessions. An untargeted AcceptSession
    // returned the first unleased directory entry in insertion order, and a drained session stays
    // in the directory long enough (eviction needs two confirmed-empty sweeps 300s apart) to keep
    // winning. Unblocked by SweepCandidate.LastClaimedAt, which orders the any-available branch
    // least-recently-claimed first, so a just-serviced session goes to the back of the line.
    [Fact]
    public async Task K11_SessionIdleTimeout_LetsTheConsumerMoveOnToAnotherSession()
    {
        var queueId = NewQueueId();
        await EnqueueSessionAsync(queueId, "first", 2);
        await EnqueueSessionAsync(queueId, "second", 2);

        var handledBySession = new ConcurrentDictionary<string, int>();
        var bothSessionsSeen = new TaskCompletionSource();

        // One slot for two sessions: the only way it reaches the second is by the first's stream
        // ending on its idle timeout once drained.
        await using var consumer = new SessionQueueConsumer(
            CreateClient(), queueId, Options(maxConcurrentSessions: 1, leaseSeconds: 30, sessionIdleTimeoutSeconds: 2),
            (ctx, _) =>
            {
                handledBySession.AddOrUpdate(ctx.SessionId, 1, (_, count) => count + 1);
                if (handledBySession.Count == 2 && handledBySession.Values.Sum() == 4)
                {
                    bothSessionsSeen.TrySetResult();
                }

                return Task.CompletedTask;
            });

        await consumer.StartAsync();
        try
        {
            await bothSessionsSeen.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(SettleMilliseconds);
        }
        finally
        {
            await consumer.StopAsync();
        }

        Assert.Equal(2, handledBySession["first"]);
        Assert.Equal(2, handledBySession["second"]);
        Assert.Empty(await RemainingOnSessionAsync(queueId, "first"));
        Assert.Empty(await RemainingOnSessionAsync(queueId, "second"));
    }
}
