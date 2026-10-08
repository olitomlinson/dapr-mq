using System.Net;
using System.Net.Sockets;
using DaprMQ.Client.Exceptions;
using Grpc.Net.Client;

namespace DaprMQ.Client.Tests;

/// <summary>
/// The shared retry contract (sdks/testing/RETRIES_AND_READINESS.md): retry what certainly wasn't
/// delivered, retry an unknown outcome only for a fully keyed enqueue, within RetryTimeout.
/// </summary>
public class DaprMQClientRetryTests
{
    private const string Ok = """{"success":true,"message":"ok","itemsEnqueued":1,"itemsDeduplicated":0}""";

    private static readonly DaprMQRetryOptions Fast = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
        MinAttemptWindow = TimeSpan.FromMilliseconds(10),
        InitialBackoff = TimeSpan.FromMilliseconds(1),
        MaxBackoff = TimeSpan.FromMilliseconds(5),
    };

    private static DaprMQClient CreateClient(FakeHttpMessageHandler handler, DaprMQRetryOptions? retry = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") }, GrpcChannel.ForAddress("http://localhost:5001"), retry ?? Fast);

    private static HttpResponseMessage NotDelivered()
    {
        var r = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"message":"unavailable","success":false,"errorCode":"UNAVAILABLE"}""", System.Text.Encoding.UTF8, "application/json")
        };
        r.Headers.Add("daprmq-delivery", "not-delivered");
        return r;
    }

    private static HttpResponseMessage Unknown()
    {
        var r = new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
        {
            Content = new StringContent("""{"message":"unknown","success":false,"errorCode":"DELIVERY_UNKNOWN"}""", System.Text.Encoding.UTF8, "application/json")
        };
        r.Headers.Add("daprmq-delivery", "unknown");
        return r;
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Answers each attempt in turn; the last answer repeats.</summary>
    private static (FakeHttpMessageHandler Handler, List<HttpRequestMessage> Requests) Sequence(params Func<HttpResponseMessage>[] answers)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requests.Add(req);
            return Task.FromResult(answers[Math.Min(requests.Count - 1, answers.Length - 1)]());
        });
        return (handler, requests);
    }

    [Fact]
    public async Task NotDelivered_IsRetried_UntilItSucceeds_ForAnyOperation()
    {
        var (handler, requests) = Sequence(NotDelivered, NotDelivered, () => Json(HttpStatusCode.OK, "{}"));

        await CreateClient(handler).AcknowledgeAsync("q", "L1");

        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task Nack_NotDelivered_IsRetried_UntilItSucceeds()
    {
        var (handler, requests) = Sequence(NotDelivered, () => Json(HttpStatusCode.OK, """{"success":true,"deadLettered":false,"deliveryCount":1}"""));

        var result = await CreateClient(handler).NackAsync("q", "L1");

        Assert.Equal(2, requests.Count);
        Assert.False(result.DeadLettered);
    }

    [Fact]
    public async Task Nack_Unknown_IsNotRetried_AndNamesTheOperation()
    {
        var (handler, requests) = Sequence(Unknown);

        var ex = await Assert.ThrowsAsync<DeliveryUnknownException>(() => CreateClient(handler).NackAsync("q", "L1"));

        Assert.Single(requests);
        Assert.Equal("Nack", ex.Operation);
    }

    [Fact]
    public async Task AcknowledgeBatch_Unknown_IsRetried_BecauseResendingIsHarmless()
    {
        // The retry reports LOCK_NOT_FOUND for locks the first attempt settled: after a retry that
        // outcome means "already acknowledged", and the SDK passes it through unchanged.
        var (handler, requests) = Sequence(Unknown, () => Json(HttpStatusCode.OK,
            """{"success":true,"message":"ok","itemsAcknowledged":0,"results":[{"lockId":"L1","outcome":"LOCK_NOT_FOUND"}]}"""));

        var result = await CreateClient(handler).AcknowledgeBatchAsync("q", ["L1"]);

        Assert.Equal(2, requests.Count);
        Assert.Equal(AcknowledgeOutcomes.LockNotFound, Assert.Single(result.Results).Outcome);
    }

    [Fact]
    public async Task EveryAttempt_SendsTheRemainingRetryTime_NotACallDeadline()
    {
        var (handler, requests) = Sequence(() => Json(HttpStatusCode.OK, Ok));

        await CreateClient(handler).EnqueueAsync("q", [new EnqueueItemDto(new { n = 1 })]);

        var ms = int.Parse(Assert.Single(requests[0].Headers.GetValues("daprmq-retry-timeout")));
        Assert.InRange(ms, 4_000, 5_000);
        Assert.False(requests[0].Headers.Contains("daprmq-timeout"));
    }

    [Fact]
    public async Task ASlowResponse_OutlivesTheRetryTimeout()
    {
        // e.g. queued behind thousands of calls on one busy queue: slow, but progressing.
        var handler = new FakeHttpMessageHandler(async _ =>
        {
            await Task.Delay(300);
            return Json(HttpStatusCode.OK, Ok);
        });

        var result = await CreateClient(handler, Fast with { Timeout = TimeSpan.FromMilliseconds(50) })
            .EnqueueAsync("q", [new EnqueueItemDto(new { n = 1 })]);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task NotDelivered_UntilTimeRunsOut_ThrowsUnavailable()
    {
        var (handler, requests) = Sequence(NotDelivered);
        var client = CreateClient(handler, Fast with { Timeout = TimeSpan.FromMilliseconds(200) });

        var ex = await Assert.ThrowsAsync<DaprMQUnavailableException>(() => client.AcknowledgeAsync("q", "L1"));

        Assert.True(requests.Count > 1);
        Assert.Equal("UNAVAILABLE", ex.ErrorCode);
        Assert.Equal("Acknowledge", ex.Operation);
        Assert.Equal("q", ex.QueueId);
    }

    [Fact]
    public async Task NoAttemptStarts_WithLessThanTheMinimumWindowLeft()
    {
        var (handler, requests) = Sequence(NotDelivered);
        var client = CreateClient(handler, Fast with { Timeout = TimeSpan.FromSeconds(1), MinAttemptWindow = TimeSpan.FromSeconds(5) });

        await Assert.ThrowsAsync<DaprMQUnavailableException>(() => client.AcknowledgeAsync("q", "L1"));

        Assert.Single(requests);
    }

    [Fact]
    public async Task ConnectionRefused_IsNotDelivered_AndRetried()
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ => ++attempts < 3
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused))
            : Task.FromResult(Json(HttpStatusCode.OK, Ok)));

        await CreateClient(handler).EnqueueAsync("q", [new EnqueueItemDto(new { n = 1 })]);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Unknown_IsNotRetried_ForADequeue()
    {
        var (handler, requests) = Sequence(Unknown);

        var ex = await Assert.ThrowsAsync<DeliveryUnknownException>(() => CreateClient(handler).DequeueLockedAsync("q"));

        Assert.Single(requests);
        Assert.Equal("DELIVERY_UNKNOWN", ex.ErrorCode);
        Assert.Equal("DequeueLocked", ex.Operation);
    }

    [Fact]
    public async Task Unknown_IsNotRetried_ForAnEnqueueWithUnkeyedItems_AndReportsTheKeys()
    {
        var (handler, requests) = Sequence(Unknown);

        var ex = await Assert.ThrowsAsync<DeliveryUnknownException>(() => CreateClient(handler)
            .EnqueueAsync("q", [new EnqueueItemDto(new { n = 1 }, IdempotencyKey: "k1"), new EnqueueItemDto(new { n = 2 })]));

        Assert.Single(requests);
        Assert.Equal(["k1", null], ex.IdempotencyKeys);
    }

    [Fact]
    public async Task Unknown_IsRetried_ForAFullyKeyedEnqueue()
    {
        var (handler, requests) = Sequence(Unknown, () => Json(HttpStatusCode.OK, Ok));

        await CreateClient(handler).EnqueueAsync("q", [new EnqueueItemDto(new { n = 1 }, IdempotencyKey: "k1")]);

        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task AutoIdempotencyKeys_FillsMissingKeys_KeepsGivenOnes_AndMakesUnknownRetryable()
    {
        var bodies = new List<string>();
        var handler = new FakeHttpMessageHandler(async req =>
        {
            bodies.Add(await req.Content!.ReadAsStringAsync());
            return bodies.Count == 1 ? Unknown() : Json(HttpStatusCode.OK, Ok);
        });
        var client = CreateClient(handler, Fast with { AutoIdempotencyKeys = true });

        await client.EnqueueAsync("q", [new EnqueueItemDto(new { n = 1 }, IdempotencyKey: "mine"), new EnqueueItemDto(new { n = 2 })]);

        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]); // the retry re-sends the same generated key
        Assert.Contains("\"idempotencyKey\":\"mine\"", bodies[0]);
        Assert.Matches("\"idempotencyKey\":\"[0-9a-f]{32}\"", bodies[0]);
    }

    [Fact]
    public async Task A503WithoutTheMarker_IsNotADeliveryFailure()
    {
        var (handler, requests) = Sequence(() => Json(HttpStatusCode.ServiceUnavailable, """{"message":"proxy says no"}"""));

        var ex = await Assert.ThrowsAsync<DaprMQException>(() => CreateClient(handler).AcknowledgeAsync("q", "L1"));

        Assert.Single(requests);
        Assert.IsNotType<DaprMQUnavailableException>(ex);
    }

    [Fact]
    public async Task RetriesOff_SendsNoDeadline_AndMakesOneAttempt()
    {
        var (handler, requests) = Sequence(NotDelivered);
        var client = CreateClient(handler, new DaprMQRetryOptions { Timeout = TimeSpan.Zero });

        await Assert.ThrowsAsync<DaprMQUnavailableException>(() => client.AcknowledgeAsync("q", "L1"));

        Assert.Single(requests);
        Assert.False(requests[0].Headers.Contains("daprmq-retry-timeout"));
    }

    [Fact]
    public async Task CallerCancellation_StopsRetrying_AsCancellation()
    {
        var (handler, _) = Sequence(NotDelivered);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var client = CreateClient(handler, Fast with { Timeout = TimeSpan.FromSeconds(30), InitialBackoff = TimeSpan.FromMilliseconds(20) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AcknowledgeAsync("q", "L1", ct: cts.Token));
    }
}
