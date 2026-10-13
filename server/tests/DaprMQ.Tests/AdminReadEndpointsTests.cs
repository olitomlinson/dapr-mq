using DaprMQ.ApiServer.Endpoints;
using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace DaprMQ.Tests;

public class AdminReadEndpointsTests
{
    private readonly Mock<ICatalogReadService> _service = new();
    private readonly Mock<IAdminReadService> _admin = new();

    [Fact]
    public async Task Catalog_PassesFiltersThroughAndReturnsThePage()
    {
        var page = new CatalogPage { Entries = [new CatalogEntry { Id = "q", Kind = CatalogEntryKind.Queue }], NextCursor = "q" };
        _service.Setup(s => s.ListAsync(CatalogEntryKind.DeadLetter, "after", 5, It.IsAny<CancellationToken>())).ReturnsAsync(page);

        var result = await AdminReadEndpoints.HandleCatalogAsync("deadletter", "after", 5, _service.Object, CancellationToken.None);

        var ok = Assert.IsType<Ok<CatalogPage>>(result.Result);
        Assert.Same(page, ok.Value);
    }

    [Fact]
    public async Task Catalog_DefaultsToNoKindAndDefaultLimit()
    {
        _service.Setup(s => s.ListAsync(null, null, AdminReadEndpoints.DefaultCatalogLimit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogPage());

        var result = await AdminReadEndpoints.HandleCatalogAsync(null, null, null, _service.Object, CancellationToken.None);

        Assert.IsType<Ok<CatalogPage>>(result.Result);
    }

    [Theory]
    [InlineData("nope", null)]
    [InlineData("1", null)]
    [InlineData(null, 0)]
    [InlineData(null, AdminReadEndpoints.MaxCatalogLimit + 1)]
    public async Task Catalog_RejectsBadKindOrLimit(string? kind, int? limit)
    {
        var result = await AdminReadEndpoints.HandleCatalogAsync(kind, null, limit, _service.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Queue_MapsOkAndNotFound()
    {
        var detail = new QueueDetail { QueueId = "orders" };
        _admin.Setup(s => s.GetQueueAsync("orders", It.IsAny<CancellationToken>())).ReturnsAsync(AdminReadResult<QueueDetail>.Ok(detail));
        _admin.Setup(s => s.GetQueueAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync(AdminReadResult<QueueDetail>.NotFound());

        var ok = await AdminReadEndpoints.HandleQueueAsync("orders", _admin.Object, CancellationToken.None);
        var missing = await AdminReadEndpoints.HandleQueueAsync("missing", _admin.Object, CancellationToken.None);

        Assert.Same(detail, Assert.IsType<Ok<QueueDetail>>(ok.Result).Value);
        Assert.IsType<NotFound>(missing.Result);
    }

    [Fact]
    public async Task Messages_ParsesCursorAndDefaultsLimit()
    {
        _admin.Setup(s => s.ListMessagesAsync("orders", 1, new MessageCursor(1, -2, 40), AdminReadEndpoints.DefaultBrowseLimit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AdminReadResult<AdminMessagePage>.Ok(new AdminMessagePage()));

        var result = await AdminReadEndpoints.HandleMessagesAsync("orders", 1, "1:-2:40", null, _admin.Object, CancellationToken.None);

        Assert.IsType<Ok<AdminMessagePage>>(result.Result);
    }

    [Theory]
    [InlineData(null, "nope", null)]
    [InlineData(null, "1:2", null)]
    [InlineData(-1, null, null)]
    [InlineData(null, null, 0)]
    [InlineData(null, null, AdminReadEndpoints.MaxBrowseLimit + 1)]
    public async Task Messages_RejectsBadInput(int? priority, string? cursor, int? limit)
    {
        var result = await AdminReadEndpoints.HandleMessagesAsync("orders", priority, cursor, limit, _admin.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
        _admin.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Message_GoneMapsTo410()
    {
        _admin.Setup(s => s.GetMessageAsync("orders", new MessageLocation(1, 0, 3), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AdminReadResult<AdminMessage>.Gone());

        var result = await AdminReadEndpoints.HandleMessageAsync("orders", "1:0:3", 7, _admin.Object, CancellationToken.None);

        Assert.Equal(StatusCodes.Status410Gone, Assert.IsType<StatusCodeHttpResult>(result.Result).StatusCode);
    }

    [Theory]
    [InlineData("1:0", 7L)]
    [InlineData("1:0:-1", 7L)]
    [InlineData("1:0:3", null)]
    public async Task Message_RejectsBadLocationOrMissingSequence(string location, long? sequence)
    {
        var result = await AdminReadEndpoints.HandleMessageAsync("orders", location, sequence, _admin.Object, CancellationToken.None);
        var payload = await AdminReadEndpoints.HandleMessagePayloadAsync("orders", location, sequence, _admin.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
        Assert.IsType<BadRequest<string>>(payload.Result);
        _admin.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Locks_ParsesOffsetCursor()
    {
        _admin.Setup(s => s.ListLocksAsync("orders", 100, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AdminReadResult<AdminLockPage>.Ok(new AdminLockPage()));

        var result = await AdminReadEndpoints.HandleLocksAsync("orders", "100", 10, _admin.Object, CancellationToken.None);

        Assert.IsType<Ok<AdminLockPage>>(result.Result);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("x")]
    public async Task Locks_RejectsBadCursor(string cursor)
    {
        var result = await AdminReadEndpoints.HandleLocksAsync("orders", cursor, null, _admin.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
    }

    [Theory]
    [InlineData("../metadata")]
    [InlineData("abc-def")]
    public async Task Lock_RejectsNonAlphanumericId(string lockId)
    {
        var result = await AdminReadEndpoints.HandleLockAsync("orders", lockId, _admin.Object, CancellationToken.None);
        var payload = await AdminReadEndpoints.HandleLockPayloadAsync("orders", lockId, _admin.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
        Assert.IsType<BadRequest<string>>(payload.Result);
        _admin.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sessions_PassesCursorThrough()
    {
        _admin.Setup(s => s.ListSessionsAsync("orders", "a", AdminReadEndpoints.DefaultBrowseLimit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AdminReadResult<AdminSessionPage>.Ok(new AdminSessionPage()));

        var result = await AdminReadEndpoints.HandleSessionsAsync("orders", "a", null, _admin.Object, CancellationToken.None);

        Assert.IsType<Ok<AdminSessionPage>>(result.Result);
    }

    [Fact]
    public async Task Topic_NotFound()
    {
        _admin.Setup(s => s.GetTopicAsync("news", It.IsAny<CancellationToken>())).ReturnsAsync(AdminReadResult<TopicDetail>.NotFound());

        var result = await AdminReadEndpoints.HandleTopicAsync("news", _admin.Object, CancellationToken.None);

        Assert.IsType<NotFound>(result.Result);
    }
}
