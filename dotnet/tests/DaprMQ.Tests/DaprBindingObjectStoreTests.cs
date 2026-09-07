using Dapr.Client;
using DaprMQ.ApiServer.Services;
using Moq;

namespace DaprMQ.Tests;

public class DaprBindingObjectStoreTests
{
    private static DaprBindingObjectStore CreateStore(Mock<DaprClient> daprClient, string globalPrefix = "")
    {
        var config = new ObjectStoreConfig
        {
            GlobalPrefix = globalPrefix,
            BindingName = "daprmq-blobstore"
        };
        return new DaprBindingObjectStore(daprClient.Object, config);
    }

    private static BindingRequest? CapturedRequest;

    private static Mock<DaprClient> CreateDaprClientMock(byte[]? responseData = null)
    {
        var mock = new Mock<DaprClient>();
        mock.Setup(c => c.InvokeBindingAsync(It.IsAny<BindingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<BindingRequest, CancellationToken>((req, _) => CapturedRequest = req)
            .ReturnsAsync((BindingRequest req, CancellationToken _) =>
                new BindingResponse(req, responseData ?? Array.Empty<byte>(), new Dictionary<string, string>()));
        return mock;
    }

    [Fact]
    public async Task DownloadAsync_SetsBlobNameAndKeyMetadata_SoAzureAndS3BindingsWork()
    {
        var daprClient = CreateDaprClientMock();
        var store = CreateStore(daprClient);

        await store.DownloadAsync("prefix/object-id", CancellationToken.None);

        Assert.NotNull(CapturedRequest);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["fileName"]);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["blobName"]);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["key"]);
    }

    [Fact]
    public async Task DeleteAsync_SetsBlobNameAndKeyMetadata_SoAzureAndS3BindingsWork()
    {
        var daprClient = CreateDaprClientMock();
        var store = CreateStore(daprClient);

        await store.DeleteAsync("prefix/object-id", CancellationToken.None);

        Assert.NotNull(CapturedRequest);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["fileName"]);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["blobName"]);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["key"]);
    }

    [Fact]
    public async Task UploadAsync_SetsBlobNameAndKeyMetadata_SoAzureAndS3BindingsWork()
    {
        var daprClient = CreateDaprClientMock();
        var store = CreateStore(daprClient);

        var blobReference = await store.UploadAsync("prefix", "object-id", new MemoryStream(new byte[] { 1, 2, 3 }), "application/octet-stream", CancellationToken.None);

        Assert.Equal("prefix/object-id", blobReference);
        Assert.NotNull(CapturedRequest);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["fileName"]);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["blobName"]);
        Assert.Equal("prefix/object-id", CapturedRequest!.Metadata["key"]);
    }
}
