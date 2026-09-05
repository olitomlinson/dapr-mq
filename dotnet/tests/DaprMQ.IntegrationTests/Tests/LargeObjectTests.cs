using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class LargeObjectTests(DaprTestFixture fixture)
{
    private static HttpRequestMessage BuildPushObjectRequest(string queueId, byte[] content, string? prefix = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/push-object")
        {
            Content = new ByteArrayContent(content)
        };
        if (prefix != null)
        {
            request.Headers.Add("prefix", prefix);
        }
        return request;
    }

    private static HttpRequestMessage BuildPopRequest(string queueId, bool requireAck)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/pop");
        request.Headers.Add("require-ack", requireAck ? "true" : "false");
        return request;
    }

    [Fact]
    public async Task PushObject_PopWithAck_Acknowledge_RoundTripsBytesThenReapsBlob()
    {
        var queueId = $"{fixture.QueueId}-largeobj-ack-{Guid.NewGuid():N}";
        var content = new byte[64 * 1024];
        new Random(42).NextBytes(content);

        var pushResponse = await fixture.ApiClient.SendAsync(BuildPushObjectRequest(queueId, content));
        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);

        var popResponse = await fixture.ApiClient.SendAsync(BuildPopRequest(queueId, requireAck: true));
        Assert.Equal(HttpStatusCode.OK, popResponse.StatusCode);

        // Pop is always JSON now - the blob item carries an objectClaimToken and lockId, never
        // raw bytes or a raw blob reference.
        using var popBody = JsonDocument.Parse(await popResponse.Content.ReadAsStringAsync());
        var item = popBody.RootElement.GetProperty("items")[0];
        var token = item.GetProperty("item").GetProperty("objectClaimToken").GetString();
        var lockId = item.GetProperty("lockId").GetString();
        Assert.NotNull(token);
        Assert.NotNull(lockId);

        var downloadResponse = await fixture.ApiClient.GetAsync($"/object/{token}");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, downloadedBytes);

        // Recoverable until ack - the blob must still exist right after Pop and download.
        var blobFilesAfterDownload = Directory.GetFiles(fixture.BlobStoreDirectory, "*", SearchOption.AllDirectories);
        Assert.Contains(blobFilesAfterDownload, f => new FileInfo(f).Length == content.Length);

        var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", new ApiAcknowledgeRequest(lockId!));
        Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);

        // No reap is scheduled until the item is finalized, so the download above (which
        // happened pre-acknowledge) had nothing to postpone. Acknowledge is what schedules the
        // backstop reap; expect the blob to disappear within that window.
        var deadline = DateTime.UtcNow.AddSeconds(75);
        var stillPresent = true;
        while (DateTime.UtcNow < deadline)
        {
            var remainingFiles = Directory.Exists(fixture.BlobStoreDirectory)
                ? Directory.GetFiles(fixture.BlobStoreDirectory, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            stillPresent = remainingFiles.Any(f => new FileInfo(f).Length == content.Length);
            if (!stillPresent)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.False(stillPresent, "Expected the reaper to eventually delete the blob after acknowledge/download.");
    }

    [Fact]
    public async Task PushObject_PlainPop_IssuesTokenAndDownloadRoundTripsBytes()
    {
        var queueId = $"{fixture.QueueId}-largeobj-noack-{Guid.NewGuid():N}";
        var content = new byte[32 * 1024];
        new Random(7).NextBytes(content);

        var pushResponse = await fixture.ApiClient.SendAsync(BuildPushObjectRequest(queueId, content));
        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);

        var blobFilesBeforePop = Directory.Exists(fixture.BlobStoreDirectory)
            ? Directory.GetFiles(fixture.BlobStoreDirectory, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.Contains(blobFilesBeforePop, f => new FileInfo(f).Length == content.Length);

        var popResponse = await fixture.ApiClient.SendAsync(BuildPopRequest(queueId, requireAck: false));
        Assert.Equal(HttpStatusCode.OK, popResponse.StatusCode);

        // Plain Pop is also always JSON with an objectClaimToken - never raw bytes.
        using var popBody = JsonDocument.Parse(await popResponse.Content.ReadAsStringAsync());
        var token = popBody.RootElement.GetProperty("items")[0].GetProperty("item").GetProperty("objectClaimToken").GetString();
        Assert.NotNull(token);

        var downloadResponse = await fixture.ApiClient.GetAsync($"/object/{token}");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, downloadedBytes);

        // Plain Pop finalizes the item immediately (no ack step), so the backstop reap is
        // scheduled right away. The download above should postpone deletion via the
        // post-download TTL, so the blob is expected to eventually disappear.
        var deadline = DateTime.UtcNow.AddSeconds(75);
        var stillPresent = true;
        while (DateTime.UtcNow < deadline)
        {
            var remainingFiles = Directory.Exists(fixture.BlobStoreDirectory)
                ? Directory.GetFiles(fixture.BlobStoreDirectory, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            stillPresent = remainingFiles.Any(f => new FileInfo(f).Length == content.Length);
            if (!stillPresent)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.False(stillPresent, "Expected the reaper to eventually delete the blob after plain Pop and download.");
    }

    [Fact]
    public async Task PushObject_CustomPrefix_UsesPrefixInBlobPath()
    {
        var queueId = $"{fixture.QueueId}-largeobj-prefix-{Guid.NewGuid():N}";
        var content = System.Text.Encoding.UTF8.GetBytes("prefix-test-content");
        const string customPrefix = "store-b";

        var pushResponse = await fixture.ApiClient.SendAsync(BuildPushObjectRequest(queueId, content, prefix: customPrefix));
        Assert.Equal(HttpStatusCode.OK, pushResponse.StatusCode);

        var prefixDir = Path.Combine(fixture.BlobStoreDirectory, customPrefix);
        Assert.True(Directory.Exists(prefixDir), $"Expected blob to land under prefix directory {prefixDir}");
    }

    [Fact]
    public async Task GetObject_InvalidToken_Returns400()
    {
        var response = await fixture.ApiClient.GetAsync("/object/not-a-valid-token");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
