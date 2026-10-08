using System.Runtime.CompilerServices;
using System.Text.Json;
using DaprMQ.Client.Exceptions;
using DaprMQ.Client.Perf;

namespace DaprMQ.Client.Perf.Tests;

public class RecordingDaprMQClientTests
{
    private sealed class ManualClock
    {
        public double Now { get; set; }
    }

    /// <summary>Yields the given session's deliveries, advancing the clock before each, then optionally throws.</summary>
    private sealed class FakeClient(ManualClock clock, string sessionId, int deliveries, Exception? throwAfter = null) : IDaprMQClient
    {
        public async IAsyncEnumerable<SessionDelivery> ConsumeSessionAsync(
            string queueId, string? sessionId1, int leaseSeconds, int prefetchCount,
            [EnumeratorCancellation] CancellationToken ct = default, int sessionIdleTimeoutSeconds = 0)
        {
            for (var i = 0; i < deliveries; i++)
            {
                clock.Now += 100;
                await Task.Yield();
                yield return new SessionDelivery
                {
                    SessionId = sessionId,
                    LockId = $"lock-{i}",
                    Item = JsonDocument.Parse("{}").RootElement,
                    Priority = 1,
                    LockExpiresAt = 0,
                    AckAsync = _ => Task.CompletedTask,
                    DeadLetterAsync = _ => Task.CompletedTask,
                    NackAsync = _ => Task.CompletedTask,
                };
            }

            clock.Now += 50;
            if (throwAfter != null)
            {
                throw throwAfter;
            }
        }

        public Task<EnqueueResult> EnqueueAsync(string queueId, IEnumerable<EnqueueItemDto> items, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DequeueLockedResult?> DequeueLockedAsync(string queueId, int count = 1, int ttlSeconds = 30, string? leaseId = null, bool allowCompetingConsumers = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AcknowledgeAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ExtendLockAsync(string queueId, string lockId, int additionalTtlSeconds, string? leaseId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeadLetterAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AcknowledgeBatchResult> AcknowledgeBatchAsync(string queueId, IReadOnlyList<string> lockIds, string? leaseId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<NackResult> NackAsync(string queueId, string lockId, string? leaseId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionLease?> AcceptSessionAsync(string queueId, string? sessionId = null, int leaseSeconds = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionLease> RenewSessionLeaseAsync(string queueId, string sessionId, string leaseId, int additionalSeconds = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReleaseSessionAsync(string queueId, string sessionId, string leaseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task WaitForReadyAsync(string service = DaprMQClient.OperationsHealthService, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StreamThatDrains_RecordsOpenFirstDeliveryAndEnd()
    {
        var clock = new ManualClock { Now = 1000 };
        var recorder = new RecordingDaprMQClient(new FakeClient(clock, "s1", deliveries: 2), () => clock.Now);

        await foreach (var _ in recorder.ConsumeSessionAsync("q", null, 30, 10))
        {
            clock.Now += 1000; // handler
        }

        var record = Assert.Single(recorder.Streams);
        Assert.Equal(1000, record.OpenMs);
        Assert.Equal(1100, record.FirstDeliveryMs);
        Assert.Equal(3250, record.EndMs);
        Assert.Equal("s1", record.SessionId);
        Assert.Equal(StreamEndReasons.Completed, record.EndReason);
    }

    [Fact]
    public async Task StreamThatThrows_RecordsExceptionTypeAndRethrows()
    {
        var clock = new ManualClock();
        var recorder = new RecordingDaprMQClient(new FakeClient(clock, "s1", deliveries: 0, new NoSessionsAvailableException("none")), () => clock.Now);

        await Assert.ThrowsAsync<NoSessionsAvailableException>(async () =>
        {
            await foreach (var _ in recorder.ConsumeSessionAsync("q", null, 30, 10)) { }
        });

        var record = Assert.Single(recorder.Streams);
        Assert.Null(record.FirstDeliveryMs);
        Assert.Null(record.SessionId);
        Assert.Equal(50, record.EndMs);
        Assert.Equal(nameof(NoSessionsAvailableException), record.EndReason);
    }

    [Fact]
    public async Task StreamAbandonedByConsumer_RecordsAbandoned()
    {
        var clock = new ManualClock();
        var recorder = new RecordingDaprMQClient(new FakeClient(clock, "s1", deliveries: 3), () => clock.Now);

        await foreach (var _ in recorder.ConsumeSessionAsync("q", null, 30, 10))
        {
            break;
        }

        var record = Assert.Single(recorder.Streams);
        Assert.Equal(100, record.EndMs);
        Assert.Equal(StreamEndReasons.Abandoned, record.EndReason);
    }
}
