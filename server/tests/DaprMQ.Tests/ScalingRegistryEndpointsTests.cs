using Dapr.Actors;
using DaprMQ.ApiServer.Constants;
using DaprMQ.ApiServer.Endpoints;
using DaprMQ.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace DaprMQ.Tests;

public class ScalingRegistryEndpointsTests
{
    private readonly Mock<IScalingRegistryActorInvoker> _invoker = new();

    private static ScaledObjectRegistration Registration(string name = "consumer", string queueId = "orders") => new()
    {
        Namespace = "apps",
        Name = name,
        QueueId = queueId,
        TargetValue = 10
    };

    private static RegisterScaledObjectsRequest Request(params ScaledObjectRegistration[] registrations) => new() { Registrations = [.. registrations] };

    [Fact]
    public async Task Register_HandsTheBatchToTheRegistryActor()
    {
        var request = Request(Registration("a"), Registration("b"));

        var result = await ScalingRegistryEndpoints.HandleRegisterAsync(request, _invoker.Object, CancellationToken.None);

        Assert.IsType<NoContent>(result.Result);
        _invoker.Verify(i => i.InvokeMethodAsync(
            It.Is<ActorId>(id => id.GetId() == ScalingRegistry.ActorId), ActorMethodNames.RegisterScaledObjects, request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ScalingRegistryEndpoints.MaxRegistrations + 1)]
    public async Task Register_EmptyOrTooMany_Is400(int count)
    {
        var request = Request(Enumerable.Range(0, count).Select(i => Registration($"c{i}")).ToArray());

        var result = await ScalingRegistryEndpoints.HandleRegisterAsync(request, _invoker.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
        _invoker.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("", "orders")]
    [InlineData("consumer", " ")]
    public async Task Register_MissingNameOrQueue_Is400(string name, string queueId)
    {
        var result = await ScalingRegistryEndpoints.HandleRegisterAsync(Request(Registration(name, queueId)), _invoker.Object, CancellationToken.None);

        Assert.IsType<BadRequest<string>>(result.Result);
    }

    [Fact]
    public async Task Register_ActorUnreachable_Is503()
    {
        _invoker.Setup(i => i.InvokeMethodAsync(It.IsAny<ActorId>(), It.IsAny<string>(), It.IsAny<RegisterScaledObjectsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("placement down"));

        var result = await ScalingRegistryEndpoints.HandleRegisterAsync(Request(Registration()), _invoker.Object, CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (result.Result as IStatusCodeHttpResult)?.StatusCode);
    }

    [Fact]
    public async Task List_ReturnsTheReadServicesRegistrations()
    {
        var service = new Mock<IScalingRegistryReadService>();
        service.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ScaledObjectRegistrations { Registrations = [Registration()] });

        var result = await ScalingRegistryEndpoints.HandleListAsync(service.Object, CancellationToken.None);

        Assert.Equal("consumer", Assert.Single(result.Value!.Registrations).Name);
    }
}
