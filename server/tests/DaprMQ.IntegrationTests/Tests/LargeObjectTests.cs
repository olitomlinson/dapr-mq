using System.Net;
using Azure.Storage.Blobs.Models;
using System.Net.Http.Json;
using System.Text.Json;
using DaprMQ.ApiServer.Models;
using DaprMQ.IntegrationTests.Fixtures;

namespace DaprMQ.IntegrationTests.Tests;

[Collection("Dapr Collection")]
public class LargeObjectTests(DaprTestFixture fixture)
{
    private static HttpRequestMessage BuildEnqueueObjectRequest(string queueId, byte[] content, string? prefix = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/enqueue-object")
        {
            Content = new ByteArrayContent(content)
        };
        if (prefix != null)
        {
            request.Headers.Add("prefix", prefix);
        }
        return request;
    }

    private static HttpRequestMessage BuildDequeueRequest(string queueId, bool requireAck)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/dequeue");
        request.Headers.Add("require-ack", requireAck ? "true" : "false");
        return request;
    }

    /// <summary>
    /// Sizes of the blobs stored for <paramref name="queueId"/> (keyed "{queueId}/{objectId}" when no
    /// prefix is given), so other tests' blobs in the shared container don't count.
    /// </summary>
    private async Task<long[]> BlobSizesAsync(string queueId)
    {
        var sizes = new List<long>();
        await foreach (var blob in fixture.BlobContainer.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{queueId}/", CancellationToken.None))
        {
            sizes.Add(blob.Properties.ContentLength ?? 0);
        }

        return sizes.ToArray();
    }

    private async Task<(string Token, string LockId)> EnqueueAndDequeueLockedAsync(string queueId, byte[] content)
    {
        var enqueueResponse = await fixture.ApiClient.SendAsync(BuildEnqueueObjectRequest(queueId, content));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        var dequeueResponse = await fixture.ApiClient.SendAsync(BuildDequeueRequest(queueId, requireAck: true));
        Assert.Equal(HttpStatusCode.OK, dequeueResponse.StatusCode);
        using var dequeueBody = JsonDocument.Parse(await dequeueResponse.Content.ReadAsStringAsync());
        var item = dequeueBody.RootElement.GetProperty("items")[0];
        return (item.GetProperty("item").GetProperty("objectClaimToken").GetString()!, item.GetProperty("lockId").GetString()!);
    }

    /// <summary>A read-only stream of unknown length, so HttpClient sends it chunked.</summary>
    private sealed class UnknownLengthStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task EnqueueObject_DequeueLocked_Acknowledge_RoundTripsBytesThenReapsBlob()
    {
        var queueId = $"{fixture.QueueId}-largeobj-ack-{Guid.NewGuid():N}";
        var content = new byte[64 * 1024];
        new Random(42).NextBytes(content);

        var enqueueResponse = await fixture.ApiClient.SendAsync(BuildEnqueueObjectRequest(queueId, content));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        var dequeueResponse = await fixture.ApiClient.SendAsync(BuildDequeueRequest(queueId, requireAck: true));
        Assert.Equal(HttpStatusCode.OK, dequeueResponse.StatusCode);

        // Dequeue is always JSON now - the blob item carries an objectClaimToken and lockId, never
        // raw bytes or a raw blob reference.
        using var dequeueBody = JsonDocument.Parse(await dequeueResponse.Content.ReadAsStringAsync());
        var item = dequeueBody.RootElement.GetProperty("items")[0];
        var token = item.GetProperty("item").GetProperty("objectClaimToken").GetString();
        var lockId = item.GetProperty("lockId").GetString();
        Assert.NotNull(token);
        Assert.NotNull(lockId);

        var downloadResponse = await fixture.ApiClient.GetAsync($"/object/{token}");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, downloadedBytes);

        // Recoverable until ack - the blob must still exist right after Dequeue and download.
        Assert.Contains(content.LongLength, await BlobSizesAsync(queueId));

        var ackResponse = await fixture.ApiClient.PostAsJsonAsync($"/queue/{queueId}/acknowledge", new ApiAcknowledgeRequest(lockId!));
        Assert.Equal(HttpStatusCode.OK, ackResponse.StatusCode);

        // No reap is scheduled until the item is finalized, so the download above (which
        // happened pre-acknowledge) had nothing to postpone. Acknowledge is what schedules the
        // backstop reap; expect the blob to disappear within that window.
        var deadline = DateTime.UtcNow.AddSeconds(75);
        var stillPresent = true;
        while (DateTime.UtcNow < deadline)
        {
            stillPresent = (await BlobSizesAsync(queueId)).Contains(content.LongLength);
            if (!stillPresent)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.False(stillPresent, "Expected the reaper to eventually delete the blob after acknowledge/download.");
    }

    [Fact]
    public async Task EnqueueObject_PlainDequeue_IssuesTokenAndDownloadRoundTripsBytes()
    {
        var queueId = $"{fixture.QueueId}-largeobj-noack-{Guid.NewGuid():N}";
        var content = new byte[32 * 1024];
        new Random(7).NextBytes(content);

        var enqueueResponse = await fixture.ApiClient.SendAsync(BuildEnqueueObjectRequest(queueId, content));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        Assert.Contains(content.LongLength, await BlobSizesAsync(queueId));

        var dequeueResponse = await fixture.ApiClient.SendAsync(BuildDequeueRequest(queueId, requireAck: false));
        Assert.Equal(HttpStatusCode.OK, dequeueResponse.StatusCode);

        // Plain Dequeue is also always JSON with an objectClaimToken - never raw bytes.
        using var dequeueBody = JsonDocument.Parse(await dequeueResponse.Content.ReadAsStringAsync());
        var token = dequeueBody.RootElement.GetProperty("items")[0].GetProperty("item").GetProperty("objectClaimToken").GetString();
        Assert.NotNull(token);

        var downloadResponse = await fixture.ApiClient.GetAsync($"/object/{token}");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(content, downloadedBytes);

        // Plain Dequeue finalizes the item immediately (no ack step), so the backstop reap is
        // scheduled right away. The download above should postpone deletion via the
        // post-download TTL, so the blob is expected to eventually disappear.
        var deadline = DateTime.UtcNow.AddSeconds(75);
        var stillPresent = true;
        while (DateTime.UtcNow < deadline)
        {
            stillPresent = (await BlobSizesAsync(queueId)).Contains(content.LongLength);
            if (!stillPresent)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.False(stillPresent, "Expected the reaper to eventually delete the blob after plain Dequeue and download.");
    }

    [Fact]
    public async Task EnqueueObject_CustomPrefix_UsesPrefixInBlobPath()
    {
        var queueId = $"{fixture.QueueId}-largeobj-prefix-{Guid.NewGuid():N}";
        var content = System.Text.Encoding.UTF8.GetBytes("prefix-test-content");
        const string customPrefix = "store-b";

        var enqueueResponse = await fixture.ApiClient.SendAsync(BuildEnqueueObjectRequest(queueId, content, prefix: customPrefix));
        Assert.Equal(HttpStatusCode.OK, enqueueResponse.StatusCode);

        var stored = new List<string>();
        await foreach (var blob in fixture.BlobContainer.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{customPrefix}/", CancellationToken.None))
        {
            stored.Add(blob.Name);
        }

        Assert.NotEmpty(stored);
    }

    [Fact]
    public async Task EnqueueObject_LargerThanSidecarMaxBodySize_RoundTripsBytes()
    {
        var queueId = $"{fixture.QueueId}-largeobj-big-{Guid.NewGuid():N}";
        var content = new byte[20 * 1024 * 1024];
        new Random(11).NextBytes(content);

        var (token, _) = await EnqueueAndDequeueLockedAsync(queueId, content);

        var downloadResponse = await fixture.ApiClient.GetAsync($"/object/{token}");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal(content, await downloadResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task EnqueueObject_OverMaxObjectBytes_Returns413_AndStoresNothing()
    {
        var queueId = $"{fixture.QueueId}-largeobj-cap-{Guid.NewGuid():N}";
        var request = new HttpRequestMessage(HttpMethod.Post, $"/queue/{queueId}/enqueue-object")
        {
            Content = new StreamContent(new UnknownLengthStream(new byte[33 * 1024 * 1024]))
        };

        var response = await fixture.ApiClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await BlobSizesAsync(queueId));
        var dequeueResponse = await fixture.ApiClient.SendAsync(BuildDequeueRequest(queueId, requireAck: false));
        Assert.Equal(HttpStatusCode.NoContent, dequeueResponse.StatusCode);
    }

    [Fact]
    public async Task GetObject_BlobGoneFromStore_Returns404()
    {
        var queueId = $"{fixture.QueueId}-largeobj-gone-{Guid.NewGuid():N}";
        var (token, _) = await EnqueueAndDequeueLockedAsync(queueId, [1, 2, 3]);
        await foreach (var blob in fixture.BlobContainer.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{queueId}/", CancellationToken.None))
        {
            await fixture.BlobContainer.DeleteBlobAsync(blob.Name);
        }

        var response = await fixture.ApiClient.GetAsync($"/object/{token}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetObject_InvalidToken_Returns400()
    {
        var response = await fixture.ApiClient.GetAsync("/object/not-a-valid-token");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
