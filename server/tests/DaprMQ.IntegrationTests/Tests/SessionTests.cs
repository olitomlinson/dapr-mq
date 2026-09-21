using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using DaprMQ.IntegrationTests.Fixtures;
using DaprMQ.ApiServer.Models;
using DaprMQ.ApiServer.Grpc;
using GrpcService = DaprMQ.ApiServer.Grpc.DaprMQ;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// Full HTTP (and, for ConsumeSession, gRPC) contract tests for the sessions feature - exercises
/// the whole stack (QueueController/DaprMQGrpcService -> SessionCoordinatorActor -> per-session
/// QueueActor instances) against a real Dapr sidecar, mirroring LockAndAcknowledgementTests.cs/
/// TopicTests.cs's shape. See the sessions plan, §7 "Testing Strategy" and Acceptance Criteria.
/// </summary>
[Collection("Dapr Collection")]
public class SessionTests(DaprTestFixture fixture)
{
    // Deliberately avoids the literal substring "-session-" in the generated id - QueueActor
    // recognizes a per-session actor purely from its own id containing that marker (accepted,
    // documented risk in QueueActor.cs, same precedent as "-deadletter"/"-sink"), so a base
    // queueId that happens to contain it would itself be misparsed as already session-scoped.
    private string NewQueueId() => $"{fixture.QueueId}-sq-{Guid.NewGuid():N}";

    private async Task EnqueueAsync(string queueId, string? sessionId, object payload, int priority = 1)
    {
        var itemElement = JsonSerializer.SerializeToElement(payload);
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, priority, SessionId: sessionId) });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", request);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Enqueue failed: {response.StatusCode} - {content}");
    }

    private async Task<HttpResponseMessage> AcceptSessionAsync(string queueId, string? sessionId = null, int leaseSeconds = 30) =>
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/accept", new ApiAcceptSessionRequest(sessionId, leaseSeconds));

    private async Task<ApiAcceptSessionResponse> AcceptSessionSuccessfullyAsync(string queueId, string? sessionId = null, int leaseSeconds = 30)
    {
        var response = await AcceptSessionAsync(queueId, sessionId, leaseSeconds);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"AcceptSession failed: {response.StatusCode} - {content}");
        var result = await response.Content.ReadFromJsonAsync<ApiAcceptSessionResponse>();
        Assert.NotNull(result);
        return result!;
    }

    private async Task<HttpResponseMessage> RenewAsync(string queueId, string sessionId, string leaseId, int additionalSeconds = 30) =>
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/{sessionId}/renew", new ApiRenewSessionLeaseRequest(leaseId, additionalSeconds));

    private async Task<HttpResponseMessage> ReleaseAsync(string queueId, string sessionId, string leaseId) =>
        await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/{sessionId}/release", new ApiReleaseSessionRequest(leaseId));

    private async Task<HttpResponseMessage> DequeueAsync(string queueId, string sessionId, string? leaseId, bool requireAck = false, int ttlSeconds = 30)
    {
        var sessionActorId = $"{queueId}-session-{sessionId}";
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{sessionActorId}/dequeue");
        request.Headers.Add("require-ack", requireAck ? "true" : "false");
        request.Headers.Add("ttl-seconds", ttlSeconds.ToString());
        if (leaseId != null)
        {
            request.Headers.Add("lease-id", leaseId);
        }

        return await fixture.ApiClient.SendAsync(request);
    }

    private GrpcService.DaprMQClient CreateGrpcClient()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        var channel = GrpcChannel.ForAddress(fixture.GrpcUrl, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });
        return new GrpcService.DaprMQClient(channel);
    }

    [Fact]
    public async Task Enqueue_WithSessionId_ThenAcceptSessionAndDequeue_ReturnsItemsInFifoOrder()
    {
        var queueId = NewQueueId();
        var sessionId = "order-1";

        await EnqueueAsync(queueId, sessionId, new { seq = 1 });
        await EnqueueAsync(queueId, sessionId, new { seq = 2 });
        await EnqueueAsync(queueId, sessionId, new { seq = 3 });

        var lease = await AcceptSessionSuccessfullyAsync(queueId, sessionId);
        Assert.Equal(sessionId, lease.SessionId);
        Assert.NotEmpty(lease.LeaseId);

        foreach (var expectedSeq in new[] { 1, 2, 3 })
        {
            var dequeueResponse = await DequeueAsync(queueId, sessionId, lease.LeaseId);
            Assert.Equal(HttpStatusCode.OK, dequeueResponse.StatusCode);
            var dequeueResult = await dequeueResponse.Content.ReadFromJsonAsync<ApiDequeueResponse>();
            Assert.NotNull(dequeueResult);
            var item = Assert.Single(dequeueResult!.Items);
            Assert.Equal(expectedSeq, ((JsonElement)item.Item).GetProperty("seq").GetInt32());
        }
    }

    [Fact]
    public async Task TwoSessions_IndependentOrderingAndCrossSessionParallelism()
    {
        var queueId = NewQueueId();

        // Interleave enqueues across two sessions to prove they don't share ordering.
        await EnqueueAsync(queueId, "s1", new { session = "s1", seq = 1 });
        await EnqueueAsync(queueId, "s2", new { session = "s2", seq = 1 });
        await EnqueueAsync(queueId, "s1", new { session = "s1", seq = 2 });
        await EnqueueAsync(queueId, "s2", new { session = "s2", seq = 2 });

        var leaseA = await AcceptSessionSuccessfullyAsync(queueId, "s1");
        var leaseB = await AcceptSessionSuccessfullyAsync(queueId, "s2");

        // Dequeue from both sessions concurrently - independent actor instances, no cross-session
        // interference expected (plan §2.1's whole rationale for per-session actors).
        var dequeueTaskA = DequeueAsync(queueId, "s1", leaseA.LeaseId);
        var dequeueTaskB = DequeueAsync(queueId, "s2", leaseB.LeaseId);
        await Task.WhenAll(dequeueTaskA, dequeueTaskB);

        var resultA = await (await dequeueTaskA).Content.ReadFromJsonAsync<ApiDequeueResponse>();
        var resultB = await (await dequeueTaskB).Content.ReadFromJsonAsync<ApiDequeueResponse>();

        Assert.Equal("s1", ((JsonElement)resultA!.Items[0].Item).GetProperty("session").GetString());
        Assert.Equal(1, ((JsonElement)resultA.Items[0].Item).GetProperty("seq").GetInt32());
        Assert.Equal("s2", ((JsonElement)resultB!.Items[0].Item).GetProperty("session").GetString());
        Assert.Equal(1, ((JsonElement)resultB.Items[0].Item).GetProperty("seq").GetInt32());

        // Second dequeue on each session continues its own strict FIFO order, unaffected by the other.
        var secondA = await (await DequeueAsync(queueId, "s1", leaseA.LeaseId)).Content.ReadFromJsonAsync<ApiDequeueResponse>();
        var secondB = await (await DequeueAsync(queueId, "s2", leaseB.LeaseId)).Content.ReadFromJsonAsync<ApiDequeueResponse>();
        Assert.Equal(2, ((JsonElement)secondA!.Items[0].Item).GetProperty("seq").GetInt32());
        Assert.Equal(2, ((JsonElement)secondB!.Items[0].Item).GetProperty("seq").GetInt32());
    }

    [Fact]
    public async Task AcceptSession_TargetedAlreadyLeased_Returns423()
    {
        var queueId = NewQueueId();
        var sessionId = "contended";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        await AcceptSessionSuccessfullyAsync(queueId, sessionId);

        var secondAttempt = await AcceptSessionAsync(queueId, sessionId);
        Assert.Equal(HttpStatusCode.Locked, secondAttempt.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_UnknownSessionId_Returns404()
    {
        var queueId = NewQueueId();
        var response = await AcceptSessionAsync(queueId, "never-enqueued-to");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_NoSessionsKnown_Returns204()
    {
        var queueId = NewQueueId();
        var response = await AcceptSessionAsync(queueId, sessionId: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AcceptSession_AnyAvailable_ClaimsAKnownSession()
    {
        var queueId = NewQueueId();
        var sessionId = "any-available-target";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        var lease = await AcceptSessionSuccessfullyAsync(queueId, sessionId: null);
        Assert.Equal(sessionId, lease.SessionId);
    }

    [Fact]
    public async Task Dequeue_OnLeasedSession_WithoutLeaseId_Returns400()
    {
        var queueId = NewQueueId();
        var sessionId = "guard-no-lease";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });
        await AcceptSessionSuccessfullyAsync(queueId, sessionId);

        var dequeueResponse = await DequeueAsync(queueId, sessionId, leaseId: null);
        Assert.Equal(HttpStatusCode.BadRequest, dequeueResponse.StatusCode);
    }

    [Fact]
    public async Task Dequeue_OnLeasedSession_WithWrongLeaseId_Returns400()
    {
        var queueId = NewQueueId();
        var sessionId = "guard-wrong-lease";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });
        await AcceptSessionSuccessfullyAsync(queueId, sessionId);

        var dequeueResponse = await DequeueAsync(queueId, sessionId, leaseId: "not-the-real-lease-id");
        Assert.Equal(HttpStatusCode.BadRequest, dequeueResponse.StatusCode);
    }

    [Fact]
    public async Task RenewSessionLease_ExtendsExpiry_KeepsSessionUsablePastOriginalExpiry()
    {
        var queueId = NewQueueId();
        var sessionId = "renew-me";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        var lease = await AcceptSessionSuccessfullyAsync(queueId, sessionId, leaseSeconds: 3);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var renewResponse = await RenewAsync(queueId, sessionId, lease.LeaseId, additionalSeconds: 6);
        Assert.Equal(HttpStatusCode.OK, renewResponse.StatusCode);
        var renewResult = await renewResponse.Content.ReadFromJsonAsync<ApiRenewSessionLeaseResponse>();
        Assert.NotNull(renewResult);

        // Original 3s lease would have expired by now (~2s in + 2.5s more), but the renewal
        // added 6s on top of the original expiry, so the session should still be usable.
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        var dequeueResponse = await DequeueAsync(queueId, sessionId, lease.LeaseId);
        Assert.Equal(HttpStatusCode.OK, dequeueResponse.StatusCode);
    }

    [Fact]
    public async Task SessionLease_Expires_IsReclaimableByAnotherAcceptSession()
    {
        var queueId = NewQueueId();
        var sessionId = "expires-and-reclaimed";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        var firstLease = await AcceptSessionSuccessfullyAsync(queueId, sessionId, leaseSeconds: 2);

        // Wait past the lease TTL without renewing (2s TTL + buffer, mirroring
        // LockAndAcknowledgementTests.cs's item-lock expiry test timing convention).
        await Task.Delay(TimeSpan.FromSeconds(5));

        var secondLease = await AcceptSessionSuccessfullyAsync(queueId, sessionId);
        Assert.Equal(sessionId, secondLease.SessionId);
        Assert.NotEqual(firstLease.LeaseId, secondLease.LeaseId);

        // The old lease no longer authorizes Dequeue against the session actor.
        var dequeueWithOldLease = await DequeueAsync(queueId, sessionId, firstLease.LeaseId);
        Assert.Equal(HttpStatusCode.BadRequest, dequeueWithOldLease.StatusCode);

        // The new lease does.
        var dequeueWithNewLease = await DequeueAsync(queueId, sessionId, secondLease.LeaseId);
        Assert.Equal(HttpStatusCode.OK, dequeueWithNewLease.StatusCode);
    }

    [Fact]
    public async Task ReleaseSession_FreesImmediately_WithoutWaitingForLeaseExpiry()
    {
        var queueId = NewQueueId();
        var sessionId = "release-me";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        // A long lease that would not naturally expire for the duration of this test.
        var lease = await AcceptSessionSuccessfullyAsync(queueId, sessionId, leaseSeconds: 300);

        var releaseResponse = await ReleaseAsync(queueId, sessionId, lease.LeaseId);
        Assert.Equal(HttpStatusCode.OK, releaseResponse.StatusCode);
        var releaseResult = await releaseResponse.Content.ReadFromJsonAsync<ApiReleaseSessionResponse>();
        Assert.NotNull(releaseResult);
        Assert.True(releaseResult!.Success);

        // Immediately claimable again - proves release doesn't wait out the 300s TTL.
        var reclaimed = await AcceptSessionSuccessfullyAsync(queueId, sessionId);
        Assert.Equal(sessionId, reclaimed.SessionId);
    }

    [Fact]
    public async Task ReleaseSession_CalledTwiceWithSameLeaseId_IsIdempotent()
    {
        var queueId = NewQueueId();
        var sessionId = "release-twice";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        var lease = await AcceptSessionSuccessfullyAsync(queueId, sessionId);

        var firstRelease = await ReleaseAsync(queueId, sessionId, lease.LeaseId);
        Assert.Equal(HttpStatusCode.OK, firstRelease.StatusCode);

        var secondRelease = await ReleaseAsync(queueId, sessionId, lease.LeaseId);
        Assert.Equal(HttpStatusCode.OK, secondRelease.StatusCode);
        var secondResult = await secondRelease.Content.ReadFromJsonAsync<ApiReleaseSessionResponse>();
        Assert.True(secondResult!.Success);
    }

    [Fact]
    public async Task ReleaseSession_WrongLeaseId_Returns400()
    {
        var queueId = NewQueueId();
        var sessionId = "release-wrong-lease";
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        await AcceptSessionSuccessfullyAsync(queueId, sessionId);

        var releaseResponse = await ReleaseAsync(queueId, sessionId, "not-the-real-lease-id");
        Assert.Equal(HttpStatusCode.BadRequest, releaseResponse.StatusCode);
    }

    [Fact]
    public async Task ConsumeSession_AssignsDeliversAcksAndReleasesOnDisconnect()
    {
        var queueId = NewQueueId();
        var sessionId = "grpc-consume";

        await EnqueueAsync(queueId, sessionId, new { seq = 1 });
        await EnqueueAsync(queueId, sessionId, new { seq = 2 });

        var client = CreateGrpcClient();
        using var call = client.ConsumeSession();

        await call.RequestStream.WriteAsync(new ConsumeSessionRequest
        {
            Start = new ConsumeSessionStart { QueueId = queueId, SessionId = sessionId, LeaseSeconds = 30, PrefetchCount = 5 }
        });

        var delivered = new List<SessionDelivered>();
        string? assignedSessionId = null;

        var readTask = Task.Run(async () =>
        {
            await foreach (var response in call.ResponseStream.ReadAllAsync())
            {
                if (response.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionAssigned)
                {
                    assignedSessionId = response.SessionAssigned.SessionId;
                }
                else if (response.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Delivered)
                {
                    delivered.Add(response.Delivered);
                    await call.RequestStream.WriteAsync(new ConsumeSessionRequest
                    {
                        Ack = new ConsumeSessionAck { LockId = response.Delivered.LockId }
                    });

                    if (delivered.Count >= 2)
                    {
                        await call.RequestStream.CompleteAsync();
                    }
                }
            }
        });

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(15))) == readTask;
        Assert.True(completed, "ConsumeSession stream did not deliver and drain both items within timeout");
        await readTask;

        Assert.Equal(sessionId, assignedSessionId);
        Assert.Equal(2, delivered.Count);

        // Disconnect should have released the session immediately (well under the 30s lease) -
        // a fresh targeted AcceptSession should succeed right away.
        var reclaimed = await AcceptSessionSuccessfullyAsync(queueId, sessionId);
        Assert.Equal(sessionId, reclaimed.SessionId);
    }

    [Fact]
    public async Task ConsumeSession_IdleTimeoutElapsed_EndsStreamWithSessionDrained_AndReleasesImmediately()
    {
        var queueId = NewQueueId();
        var sessionId = "idle-drain-me";

        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        var client = CreateGrpcClient();
        using var call = client.ConsumeSession();

        // Long lease (so renewal never interferes) but a short idle timeout - the item is
        // consumed and acked immediately, then the session sits empty until the idle timeout
        // fires on its own. No explicit disconnect from the client side.
        await call.RequestStream.WriteAsync(new ConsumeSessionRequest
        {
            Start = new ConsumeSessionStart { QueueId = queueId, SessionId = sessionId, LeaseSeconds = 30, PrefetchCount = 5, SessionIdleTimeoutSeconds = 2 }
        });

        SessionDrained? drained = null;
        var delivered = new List<SessionDelivered>();

        var readTask = Task.Run(async () =>
        {
            await foreach (var response in call.ResponseStream.ReadAllAsync())
            {
                if (response.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.Delivered)
                {
                    delivered.Add(response.Delivered);
                    await call.RequestStream.WriteAsync(new ConsumeSessionRequest
                    {
                        Ack = new ConsumeSessionAck { LockId = response.Delivered.LockId }
                    });
                }
                else if (response.PayloadCase == ConsumeSessionResponse.PayloadOneofCase.SessionDrained)
                {
                    drained = response.SessionDrained;
                }
            }
        });

        var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(15))) == readTask;
        Assert.True(completed, "ConsumeSession stream did not end with SessionDrained within timeout");
        await readTask;

        Assert.Single(delivered);
        Assert.NotNull(drained);
        Assert.Equal(sessionId, drained!.SessionId);

        // Released immediately once drained - no need to wait out the 30s lease.
        var reclaimed = await AcceptSessionSuccessfullyAsync(queueId, sessionId);
        Assert.Equal(sessionId, reclaimed.SessionId);
    }
}
