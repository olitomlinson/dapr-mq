using Dapr;
using Dapr.BinaryStore;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using Grpc.Core;
using Moq;

namespace DaprMQ.Tests;

public class DaprBinaryStoreObjectStoreTests
{
    private const string StoreName = "daprmq-blobstore";

    private readonly Mock<DaprBinaryStoreClient> _client = new();

    private DaprBinaryStoreObjectStore CreateStore() =>
        new(_client.Object, new ObjectStoreConfig { StoreName = StoreName, MaxObjectBytes = 1024 });

    private static DaprException RpcFailure(StatusCode code, string detail = "failure") =>
        new("Binary store operation failed", new RpcException(new Status(code, detail)));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> FailsWith(Exception ex)
    {
        await Task.Yield();
        if (ex != null)
        {
            throw ex;
        }

        yield break;
    }

    private void SetupUpload(Func<Stream, CancellationToken, Task> upload) =>
        _client.Setup(c => c.SetBinaryFileAsync(StoreName, It.IsAny<string>(), It.IsAny<Stream>(), false, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, Stream s, bool _, int _, CancellationToken ct) => upload(s, ct));

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // --- upload ---

    [Fact]
    public async Task UploadAsync_StreamsContentUnderPrefixedKey_WithoutOverwrite()
    {
        var content = new MemoryStream([1, 2, 3]);
        long positionWhenStoreCalled = -1;
        byte[]? uploaded = null;
        SetupUpload(async (s, _) =>
        {
            positionWhenStoreCalled = content.Position;
            uploaded = await ReadAllAsync(s);
        });

        var key = await CreateStore().UploadAsync("store-a", "object-1", content, "application/pdf");

        Assert.Equal("store-a/object-1", key);
        Assert.Equal(0, positionWhenStoreCalled); // not buffered before the call
        Assert.Equal(new byte[] { 1, 2, 3 }, uploaded);
        _client.Verify(c => c.SetBinaryFileAsync(StoreName, "store-a/object-1", It.IsAny<Stream>(), false, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("bad*char")]
    public async Task UploadAsync_InvalidPrefix_ThrowsArgumentException_WithoutCallingStore(string prefix)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateStore().UploadAsync(prefix, "object-1", new MemoryStream([1]), null));

        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadAsync_SourceReadFails_CancelsUploadAndRethrowsReadFailure()
    {
        var readFailure = new IOException("request body too large");
        var source = new Mock<Stream>();
        source.SetupGet(s => s.CanRead).Returns(true);
        source.Setup(s => s.ReadAsync(It.IsAny<Memory<byte>>(), It.IsAny<CancellationToken>())).ThrowsAsync(readFailure);
        CancellationToken uploadToken = default;
        SetupUpload(async (s, ct) =>
        {
            uploadToken = ct;
            try
            {
                await s.ReadAsync(new byte[16], ct);
            }
            catch (Exception ex)
            {
                // As the SDK does once the call is cancelled
                throw new DaprException("Binary store operation failed", ex);
            }
        });

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            CreateStore().UploadAsync("store-a", "object-1", source.Object, null));

        Assert.Same(readFailure, thrown);
        Assert.True(uploadToken.IsCancellationRequested);
    }

    [Fact]
    public async Task UploadAsync_FileTooLarge_ThrowsObjectTooLargeException()
    {
        SetupUpload((_, _) => throw RpcFailure(StatusCode.ResourceExhausted,
            "binary file \"store-a/object-1\" exceeds the maximum size allowed by component daprmq-blobstore"));

        await Assert.ThrowsAsync<ObjectTooLargeException>(() =>
            CreateStore().UploadAsync("store-a", "object-1", new MemoryStream([1]), null));
    }

    [Fact]
    public async Task UploadAsync_StoreFull_ThrowsObjectStoreFullException()
    {
        SetupUpload((_, _) => throw RpcFailure(StatusCode.ResourceExhausted,
            "component daprmq-blobstore cannot store binary file \"store-a/object-1\": store is full"));

        await Assert.ThrowsAsync<ObjectStoreFullException>(() =>
            CreateStore().UploadAsync("store-a", "object-1", new MemoryStream([1]), null));
    }

    [Fact]
    public async Task UploadAsync_OtherFailure_Propagates()
    {
        SetupUpload((_, _) => throw RpcFailure(StatusCode.Internal));

        await Assert.ThrowsAsync<DaprException>(() =>
            CreateStore().UploadAsync("store-a", "object-1", new MemoryStream([1]), null));
    }

    // --- download ---

    [Fact]
    public async Task DownloadAsync_YieldsChunksInOrder()
    {
        _client.Setup(c => c.GetBinaryFileChunksAsync(StoreName, "store-a/object-1", It.IsAny<CancellationToken>()))
            .Returns(Chunks([1, 2], [3], [4, 5, 6]));

        await using var stream = await CreateStore().DownloadAsync("store-a/object-1");

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, await ReadAllAsync(stream));
    }

    [Fact]
    public async Task DownloadAsync_ReadsSmallerThanAChunk_SpanChunks()
    {
        _client.Setup(c => c.GetBinaryFileChunksAsync(StoreName, "store-a/object-1", It.IsAny<CancellationToken>()))
            .Returns(Chunks([1, 2, 3], [4, 5]));

        await using var stream = await CreateStore().DownloadAsync("store-a/object-1");
        var buffer = new byte[2];
        var read = new List<byte>();
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            read.AddRange(buffer.Take(n));
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, read);
    }

    [Fact]
    public async Task DownloadAsync_EmptyFile_ReturnsEmptyStream()
    {
        _client.Setup(c => c.GetBinaryFileChunksAsync(StoreName, "store-a/empty", It.IsAny<CancellationToken>()))
            .Returns(Chunks());

        await using var stream = await CreateStore().DownloadAsync("store-a/empty");

        Assert.Empty(await ReadAllAsync(stream));
    }

    [Fact]
    public async Task DownloadAsync_NotFound_ThrowsObjectNotFoundException_BeforeReturningStream()
    {
        _client.Setup(c => c.GetBinaryFileChunksAsync(StoreName, "store-a/missing", It.IsAny<CancellationToken>()))
            .Returns(FailsWith(RpcFailure(StatusCode.NotFound)));

        await Assert.ThrowsAsync<ObjectNotFoundException>(() => CreateStore().DownloadAsync("store-a/missing"));
    }

    [Fact]
    public async Task DownloadAsync_OtherFailure_Propagates()
    {
        _client.Setup(c => c.GetBinaryFileChunksAsync(StoreName, "store-a/object-1", It.IsAny<CancellationToken>()))
            .Returns(FailsWith(RpcFailure(StatusCode.Internal)));

        await Assert.ThrowsAsync<DaprException>(() => CreateStore().DownloadAsync("store-a/object-1"));
    }

    // --- delete ---

    [Fact]
    public async Task DeleteAsync_DeletesKey()
    {
        _client.Setup(c => c.DeleteBinaryFileAsync(StoreName, "store-a/object-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await CreateStore().DeleteAsync("store-a/object-1");

        _client.Verify(c => c.DeleteBinaryFileAsync(StoreName, "store-a/object-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_NotFound_IsSuccess()
    {
        _client.Setup(c => c.DeleteBinaryFileAsync(StoreName, "store-a/missing", It.IsAny<CancellationToken>()))
            .ThrowsAsync(RpcFailure(StatusCode.NotFound));

        await CreateStore().DeleteAsync("store-a/missing");
    }

    [Fact]
    public async Task DeleteAsync_OtherFailure_Propagates()
    {
        _client.Setup(c => c.DeleteBinaryFileAsync(StoreName, "store-a/object-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(RpcFailure(StatusCode.Internal));

        await Assert.ThrowsAsync<DaprException>(() => CreateStore().DeleteAsync("store-a/object-1"));
    }
}
