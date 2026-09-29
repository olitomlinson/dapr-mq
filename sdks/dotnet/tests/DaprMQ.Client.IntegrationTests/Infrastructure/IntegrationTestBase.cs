using System.Text.Json;
using DaprMQ.Client.Exceptions;
using DaprMQ.IntegrationTests.Fixtures;
using Grpc.Net.Client;

namespace DaprMQ.Client.IntegrationTests.Infrastructure;

/// <summary>
/// Shared plumbing for the SDK integration suite: a unique queue id per test, a DaprMQClient
/// pointed at the shared stack, and polling helpers for the scenarios that hang off a TTL
/// expiring (see INTEGRATION_TESTS.md's note - poll with a timeout, never a fixed sleep).
/// </summary>
public abstract class IntegrationTestBase(DaprTestFixture fixture)
{
    protected DaprTestFixture Fixture { get; } = fixture;

    /// <summary>
    /// Deliberately avoids the literal substring "-session-": QueueActor infers "I am a
    /// per-session actor" purely from its own id containing that marker, so a base queue id
    /// carrying it would be misparsed as already session-scoped.
    /// </summary>
    protected string NewQueueId() => $"{Fixture.QueueId}-sdk-{Guid.NewGuid():N}";

    /// <summary>Actor-id convention the API server derives for a session's own QueueActor.</summary>
    protected static string SessionQueueId(string queueId, string sessionId) => $"{queueId}-session-{sessionId}";

    protected static string DeadLetterQueueId(string queueId) => $"{queueId}-deadletter";

    /// <summary>
    /// A client over the shared HttpClient and the one shared GrpcChannel - neither is owned by
    /// the returned instance, so callers must not dispose it. Tests that need to assert on
    /// disposal (X-02) build their own owning client instead.
    /// </summary>
    protected DaprMQClient CreateClient() =>
        new(Fixture.ApiClient, SharedGrpcChannel.For(Fixture.GrpcUrl));

    protected static JsonElement Json(string rawJson) => JsonDocument.Parse(rawJson).RootElement.Clone();

    protected async Task EnqueueSeqAsync(DaprMQClient client, string queueId, int firstSeq, int count, int priority = 1, string? sessionId = null)
    {
        var items = Enumerable.Range(firstSeq, count)
            .Select(seq => new EnqueueItemDto(new { seq }, priority, SessionId: sessionId))
            .ToList();
        var result = await client.EnqueueAsync(queueId, items);
        Assert.True(result.Success);
        Assert.Equal(count, result.ItemsEnqueued);
    }

    protected static int Seq(JsonElement item) => item.GetProperty("seq").GetInt32();

    /// <summary>
    /// Repeatedly runs <paramref name="probe"/> until it yields a non-null value or the timeout
    /// elapses. Used for every TTL-driven scenario (lock expiry, lease expiry, dead-letter
    /// routing) so they key off observed state rather than a guessed sleep duration.
    /// </summary>
    protected static async Task<T> PollUntilAsync<T>(
        Func<Task<T?>> probe,
        string because,
        int timeoutSeconds = 30,
        int intervalMs = 250) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var result = await probe();
            if (result != null)
            {
                return result;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException($"Timed out after {timeoutSeconds}s waiting for: {because}");
            }

            await Task.Delay(intervalMs);
        }
    }

    protected static async Task PollUntilTrueAsync(
        Func<Task<bool>> probe,
        string because,
        int timeoutSeconds = 30,
        int intervalMs = 250)
    {
        await PollUntilAsync<object>(
            async () => await probe() ? new object() : null,
            because,
            timeoutSeconds,
            intervalMs);
    }

    /// <summary>
    /// Claims a session, waiting out a consumer that is still letting go of it.
    ///
    /// A stream's session is released by the server as part of tearing the call down, which lands
    /// slightly after the client-side disconnect (or StopAsync) returns. A claim racing that
    /// teardown therefore sees SessionLocked - a timing artefact of the handover, not a result
    /// worth asserting on, so it is polled through rather than propagated.
    /// </summary>
    protected static Task<SessionLease> ClaimWhenFreeAsync(
        DaprMQClient client, string queueId, string sessionId, int timeoutSeconds = 30) =>
        PollUntilAsync(
            async () =>
            {
                try
                {
                    return await client.AcceptSessionAsync(queueId, sessionId, leaseSeconds: 60);
                }
                catch (SessionLockedException)
                {
                    return null;
                }
            },
            because: $"session '{sessionId}' to be released by its previous consumer",
            timeoutSeconds: timeoutSeconds);

    /// <summary>
    /// Drains a queue with repeated single-item locked dequeues, acking each one, until it runs
    /// dry. The default (non-competing-consumer) dequeue path refuses to hand out a second lock
    /// while one is outstanding, so ack-as-you-go is the only way to walk a queue in order.
    /// </summary>
    protected static async Task<List<DequeueLockedItemDto>> DrainAsync(DaprMQClient client, string queueId, int max = 100, string? leaseId = null)
    {
        var drained = new List<DequeueLockedItemDto>();
        for (var i = 0; i < max; i++)
        {
            var result = await client.DequeueLockedAsync(queueId, leaseId: leaseId);
            if (result == null || result.Items.Count == 0)
            {
                break;
            }

            foreach (var item in result.Items)
            {
                drained.Add(item);
                await client.AcknowledgeAsync(queueId, item.LockId, leaseId);
            }
        }

        return drained;
    }
}

/// <summary>
/// One GrpcChannel for the whole assembly. A channel multiplexes every RPC - including long-lived
/// ConsumeSession streams - over a single HTTP/2 connection, which is exactly the property the
/// SDK's own docs tell callers to rely on, so the tests model the same usage rather than opening a
/// connection per test class.
/// </summary>
internal static class SharedGrpcChannel
{
    private static readonly Lock Gate = new();
    private static GrpcChannel? _channel;

    public static GrpcChannel For(string address)
    {
        lock (Gate)
        {
            return _channel ??= Create(address);
        }
    }

    public static GrpcChannel Create(string address)
    {
        // The test stack speaks h2c (prior knowledge, no TLS) - without this switch the handler
        // refuses to negotiate HTTP/2 over a plain http:// address.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });
    }
}
