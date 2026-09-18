using System.Net;
using DaprMQ.Client.Exceptions;
using Grpc.Net.Client;

namespace DaprMQ.Client.Tests;

public class DaprMQClientTests
{
    private static DaprMQClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var grpcChannel = GrpcChannel.ForAddress("http://localhost:5001");
        return new DaprMQClient(httpClient, grpcChannel);
    }

    // ---- Enqueue ----

    [Fact]
    public async Task EnqueueAsync_Success_MapsResponse()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"success":true,"message":"Enqueued 1 items to queue my-queue","itemsEnqueued":1,"itemsDeduplicated":0}""");
        var client = CreateClient(handler);

        var result = await client.EnqueueAsync("my-queue", [new EnqueueItemDto(new { task = "send_email" }, Priority: 0, SessionId: "order-42")]);

        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsEnqueued);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://localhost:5000/queue/my-queue/enqueue", handler.LastRequest.RequestUri!.ToString());
        Assert.Contains("\"sessionId\":\"order-42\"", handler.LastRequestBody);
        Assert.Contains("\"priority\":0", handler.LastRequestBody);
    }

    [Fact]
    public async Task EnqueueAsync_400_ThrowsValidationException()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, """{"message":"bad item","success":false}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ValidationException>(() => client.EnqueueAsync("q", [new EnqueueItemDto(new { })]));
    }

    // ---- DequeueLocked ----

    [Fact]
    public async Task DequeueLockedAsync_Success_SendsHeadersAndMapsItems()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"items":[{"item":{"task":"x"},"priority":0,"lockId":"L1","lockExpiresAt":123.0}],"locked":false}""");
        var client = CreateClient(handler);

        var result = await client.DequeueLockedAsync("q", count: 5, ttlSeconds: 60, leaseId: "lease-1");

        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.Equal("L1", result.Items[0].LockId);
        Assert.Equal("true", handler.LastRequest!.Headers.GetValues("require-ack").First());
        Assert.Equal("5", handler.LastRequest.Headers.GetValues("count").First());
        Assert.Equal("60", handler.LastRequest.Headers.GetValues("ttl-seconds").First());
        Assert.Equal("lease-1", handler.LastRequest.Headers.GetValues("lease-id").First());
    }

    [Fact]
    public async Task DequeueLockedAsync_204_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        var result = await client.DequeueLockedAsync("q");

        Assert.Null(result);
    }

    [Fact]
    public async Task DequeueLockedAsync_423_ReturnsLockedResult()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Locked, """{"message":"locked","lockExpiresAt":999.0}""");
        var client = CreateClient(handler);

        var result = await client.DequeueLockedAsync("q");

        Assert.NotNull(result);
        Assert.True(result!.Locked);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task DequeueLockedAsync_410_ThrowsSessionLeaseExpired()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Gone, """{"message":"lease expired","success":false}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<SessionLeaseExpiredException>(() => client.DequeueLockedAsync("q", leaseId: "stale"));
    }

    // ---- Acknowledge ----

    [Fact]
    public async Task AcknowledgeAsync_Success_SendsLeaseIdHeader()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"success":true,"message":"ok","itemsAcknowledged":1}""");
        var client = CreateClient(handler);

        await client.AcknowledgeAsync("q", "L1", leaseId: "lease-1");

        Assert.Equal("lease-1", handler.LastRequest!.Headers.GetValues("lease-id").First());
        Assert.Contains("\"lockId\":\"L1\"", handler.LastRequestBody);
    }

    [Theory]
    [InlineData("LOCK_NOT_FOUND", typeof(LockNotFoundException))]
    [InlineData("LOCK_EXPIRED", typeof(LockExpiredException))]
    [InlineData("SESSION_LEASE_EXPIRED", typeof(SessionLeaseExpiredException))]
    [InlineData("INVALID_LEASE_ID", typeof(InvalidLeaseIdException))]
    public async Task AcknowledgeAsync_MapsErrorCodeToTypedException(string errorCode, Type expectedException)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest,
            $$"""{"success":false,"message":"failed","errorCode":"{{errorCode}}"}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync(expectedException, () => client.AcknowledgeAsync("q", "L1"));
    }

    // ---- ExtendLock ----

    [Fact]
    public async Task ExtendLockAsync_Success_SendsBody()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"newExpiresAt":123,"lockId":"L1"}""");
        var client = CreateClient(handler);

        await client.ExtendLockAsync("q", "L1", 30);

        Assert.Contains("\"additionalTtlSeconds\":30", handler.LastRequestBody);
    }

    [Fact]
    public async Task ExtendLockAsync_410_ThrowsLockExpired()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Gone, """{"message":"expired","success":false}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<LockExpiredException>(() => client.ExtendLockAsync("q", "L1", 30));
    }

    // ---- DeadLetter ----

    [Fact]
    public async Task DeadLetterAsync_Success()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"success":true,"message":"moved","dlqId":"q-deadletter"}""");
        var client = CreateClient(handler);

        await client.DeadLetterAsync("q", "L1");
    }

    [Fact]
    public async Task DeadLetterAsync_ErrorCode_ThrowsTypedException()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound,
            """{"success":false,"message":"not found","errorCode":"LOCK_NOT_FOUND"}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<LockNotFoundException>(() => client.DeadLetterAsync("q", "L1"));
    }

    // ---- Sessions ----

    [Fact]
    public async Task AcceptSessionAsync_Success_MapsResponse()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK,
            """{"sessionId":"order-42","leaseId":"lease-1","leaseExpiresAt":1780000200.0}""");
        var client = CreateClient(handler);

        var lease = await client.AcceptSessionAsync("q", "order-42", 30);

        Assert.NotNull(lease);
        Assert.Equal("order-42", lease!.SessionId);
        Assert.Equal("lease-1", lease.LeaseId);
        Assert.Equal("http://localhost:5000/queue/q/sessions/accept", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task AcceptSessionAsync_204_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        Assert.Null(await client.AcceptSessionAsync("q"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, typeof(SessionNotFoundException))]
    [InlineData(HttpStatusCode.Locked, typeof(SessionLockedException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(SessionActorUnavailableException))]
    [InlineData(HttpStatusCode.BadRequest, typeof(ValidationException))]
    public async Task AcceptSessionAsync_MapsStatusToTypedException(HttpStatusCode status, Type expectedException)
    {
        var handler = new FakeHttpMessageHandler(status, """{"message":"failed","success":false}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync(expectedException, () => client.AcceptSessionAsync("q", "order-42"));
    }

    [Fact]
    public async Task RenewSessionLeaseAsync_Success()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"newExpiresAt":1780000230.0}""");
        var client = CreateClient(handler);

        var lease = await client.RenewSessionLeaseAsync("q", "order-42", "lease-1", 30);

        Assert.Equal(1780000230.0, lease.LeaseExpiresAt);
        Assert.Equal("http://localhost:5000/queue/q/sessions/order-42/renew", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task RenewSessionLeaseAsync_410_ThrowsSessionLeaseExpired()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Gone, """{"message":"expired","success":false}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<SessionLeaseExpiredException>(() => client.RenewSessionLeaseAsync("q", "order-42", "lease-1"));
    }

    [Fact]
    public async Task ReleaseSessionAsync_Success()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"success":true}""");
        var client = CreateClient(handler);

        await client.ReleaseSessionAsync("q", "order-42", "lease-1");

        Assert.Equal("http://localhost:5000/queue/q/sessions/order-42/release", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReleaseSessionAsync_400_ThrowsInvalidLeaseId()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, """{"message":"bad lease","success":false}""");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidLeaseIdException>(() => client.ReleaseSessionAsync("q", "order-42", "wrong"));
    }
}
