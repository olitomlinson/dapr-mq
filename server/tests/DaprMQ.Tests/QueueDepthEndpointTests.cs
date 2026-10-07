using DaprMQ.ApiServer.Endpoints;
using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace DaprMQ.Tests;

public class QueueDepthEndpointTests
{
    private readonly Mock<IQueueDepthService> _service = new();

    [Fact]
    public async Task ReturnsResultPerQueryInOrder()
    {
        _service.Setup(s => s.GetDepthAsync(It.IsAny<QueueDepthQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueueDepthQuery q, CancellationToken _) => new QueueDepthResult { QueueId = q.QueueId, Ready = q.QueueId.Length });
        var request = new QueueDepthRequest { Queries = [new() { QueueId = "a" }, new() { QueueId = "bbb" }] };

        var result = await QueueDepthEndpoint.HandleAsync(request, _service.Object, CancellationToken.None);

        var ok = Assert.IsType<Ok<QueueDepthResponse>>(result.Result);
        Assert.Equal(["a", "bbb"], ok.Value!.Results.Select(r => r.QueueId));
        Assert.Equal([1L, 3L], ok.Value.Results.Select(r => r.Ready));
    }

    [Fact]
    public async Task FailedQuery_ReportsErrorWithoutFailingOthers()
    {
        _service.Setup(s => s.GetDepthAsync(It.Is<QueueDepthQuery>(q => q.QueueId == "bad"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("HTTP 500"));
        _service.Setup(s => s.GetDepthAsync(It.Is<QueueDepthQuery>(q => q.QueueId == "good"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDepthResult { QueueId = "good", Ready = 5 });
        var request = new QueueDepthRequest { Queries = [new() { QueueId = "bad" }, new() { QueueId = "good" }] };

        var result = await QueueDepthEndpoint.HandleAsync(request, _service.Object, CancellationToken.None);

        var ok = Assert.IsType<Ok<QueueDepthResponse>>(result.Result);
        Assert.Equal("bad", ok.Value!.Results[0].QueueId);
        Assert.Contains("HTTP 500", ok.Value.Results[0].Error);
        Assert.Null(ok.Value.Results[1].Error);
        Assert.Equal(5, ok.Value.Results[1].Ready);
    }

    [Fact]
    public async Task EmptyQueries_IsBadRequest()
    {
        var result = await QueueDepthEndpoint.HandleAsync(new QueueDepthRequest(), _service.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
    }

    [Fact]
    public async Task BlankQueueId_IsBadRequest()
    {
        var request = new QueueDepthRequest { Queries = [new() { QueueId = " " }] };

        var result = await QueueDepthEndpoint.HandleAsync(request, _service.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
    }

    [Fact]
    public async Task TooManyQueries_IsBadRequest()
    {
        var request = new QueueDepthRequest
        {
            Queries = Enumerable.Range(0, QueueDepthEndpoint.MaxQueries + 1).Select(i => new QueueDepthQuery { QueueId = $"q{i}" }).ToList()
        };

        var result = await QueueDepthEndpoint.HandleAsync(request, _service.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
    }
}
