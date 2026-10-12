using DaprMQ.ApiServer.Endpoints;
using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace DaprMQ.Tests;

public class AdminReadEndpointsTests
{
    private readonly Mock<ICatalogReadService> _service = new();

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
}
