using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.IntegrationTests.Tests;

/// <summary>
/// Exercises the specific reentrancy hazard documented at QueueActor.cs's
/// RegisterAsSessionActorIfNeededAsync: SessionCoordinatorActor calling SetSessionLease on a
/// session actor that Dapr has actually deactivated (gone cold) reactivates it mid-turn, and that
/// activation calls back into the very same SessionCoordinatorActor instance (A -> B -> A) before
/// the coordinator's own outbound call returns. Runs against a dedicated fixture with a short
/// ActorIdleTimeout/ActorScanInterval so the session actor goes cold within test time instead of
/// the production 60s default.
/// </summary>
[Collection("Dapr Reentrancy Collection")]
public class SessionReentrancyTests(ReentrancyTestFixture fixture)
{
    private string NewQueueId() => $"{fixture.QueueId}-reentrancy-{Guid.NewGuid():N}";

    private async Task EnqueueAsync(string queueId, string sessionId, object payload)
    {
        var itemElement = JsonSerializer.SerializeToElement(payload);
        var request = new ApiEnqueueRequest(new List<ApiEnqueueItem> { new ApiEnqueueItem(itemElement, Priority: 1, SessionId: sessionId) });
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/enqueue", request);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Enqueue failed: {response.StatusCode} - {content}");
    }

    private async Task<ApiAcceptSessionResponse> AcceptSessionAsync(string queueId, string sessionId, int leaseSeconds)
    {
        var response = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/sessions/accept", new ApiAcceptSessionRequest(sessionId, leaseSeconds));
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"AcceptSession failed: {response.StatusCode} - {content}");
        var result = await response.Content.ReadFromJsonAsync<ApiAcceptSessionResponse>();
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public async Task RenewSessionLease_TargetsColdSessionActor_CompletesInsteadOfDeadlocking()
    {
        var queueId = NewQueueId();
        var sessionId = "cold-reactivation";

        // Activates the session actor for the first time - a plain top-level call, not nested,
        // so its self-registration with SessionCoordinatorActor is not itself reentrant.
        await EnqueueAsync(queueId, sessionId, new { seq = 1 });

        // Long logical lease so the session-lock state doesn't expire while we wait for the actor
        // to go cold below - only the Dapr-level actor instance needs to deactivate.
        var lease = await AcceptSessionAsync(queueId, sessionId, leaseSeconds: 120);

        // Let the session actor's Dapr instance actually deactivate: fixture's ActorIdleTimeout=3s,
        // ActorScanInterval=1s.
        await Task.Delay(TimeSpan.FromSeconds(7));

        // RenewSessionLease -> SessionCoordinatorActor.TrySyncSessionLeaseAsync -> SetSessionLease
        // on the now-cold session actor. Reactivating it runs OnActivateAsync ->
        // RegisterAsSessionActorIfNeededAsync, which calls back into this same
        // SessionCoordinatorActor instance (RegisterSession) while the coordinator's own
        // RenewSessionLease call is still awaiting that SetSessionLease response - the A -> B -> A
        // chain. Without a working reentrancy fix this hangs; guard with an explicit timeout so a
        // regression fails fast instead of hanging the test run.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        HttpResponseMessage renewResponse;
        try
        {
            renewResponse = await fixture.ApiClient.PostAsJsonAsync(
                $"/queue/{queueId}/sessions/{sessionId}/renew",
                new ApiRenewSessionLeaseRequest(lease.LeaseId, AdditionalSeconds: 30),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("RenewSessionLease against a cold session actor did not complete within 20s - reentrancy deadlock likely regressed.");
            return;
        }

        var renewContent = await renewResponse.Content.ReadAsStringAsync();
        Assert.True(renewResponse.StatusCode == HttpStatusCode.OK, $"RenewSessionLease failed: {renewResponse.StatusCode} - {renewContent}");

        var renewResult = await renewResponse.Content.ReadFromJsonAsync<ApiRenewSessionLeaseResponse>();
        Assert.NotNull(renewResult);
        Assert.True(renewResult!.NewExpiresAt > lease.LeaseExpiresAt);
    }
}
