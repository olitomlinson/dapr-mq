using DaprMQ.ApiServer.Services;

namespace DaprMQ.Tests;

public class ActorHostingTests
{
    private static readonly string[] DataTypes = ["QueueActor", "HttpSinkActor", "BlobReaperActor", "TopicActor"];
    private static readonly string[] ControlTypes = ["SessionCoordinatorActor", "CatalogActor", "ScalingRegistryActor"];

    private static ActorHosting Hosting(ActorSet set) => new(set, DataTypes, ControlTypes);

    [Theory]
    [InlineData(null, ActorSet.All)]
    [InlineData("", ActorSet.All)]
    [InlineData("all", ActorSet.All)]
    [InlineData("data", ActorSet.Data)]
    [InlineData("Control", ActorSet.Control)]
    public void ParseSet_AcceptsKnownValues(string? value, ActorSet expected)
    {
        Assert.Equal(expected, ActorHosting.ParseSet(value));
    }

    [Fact]
    public void ParseSet_UnknownValue_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ActorHosting.ParseSet("workers"));
    }

    [Fact]
    public void All_HostsEveryType()
    {
        var hosting = Hosting(ActorSet.All);

        Assert.All(DataTypes.Concat(ControlTypes), t => Assert.True(hosting.Hosts(t)));
        Assert.Equal("QueueActor", hosting.ReadinessActorType);
    }

    [Fact]
    public void Data_HostsOnlyDataTypes()
    {
        var hosting = Hosting(ActorSet.Data);

        Assert.All(DataTypes, t => Assert.True(hosting.Hosts(t)));
        Assert.All(ControlTypes, t => Assert.False(hosting.Hosts(t)));
        Assert.Equal("QueueActor", hosting.ReadinessActorType);
    }

    [Fact]
    public void Control_HostsOnlyControlTypes()
    {
        var hosting = Hosting(ActorSet.Control);

        Assert.All(ControlTypes, t => Assert.True(hosting.Hosts(t)));
        Assert.All(DataTypes, t => Assert.False(hosting.Hosts(t)));
        Assert.Equal("SessionCoordinatorActor", hosting.ReadinessActorType);
    }

    [Fact]
    public void All_ReadsEverythingLocally()
    {
        Assert.Empty(Hosting(ActorSet.All).RemoteAppIds(dataAppId: null, controlAppId: null));
    }

    [Fact]
    public void Data_ReadsControlTypesFromTheControlApp()
    {
        var remote = Hosting(ActorSet.Data).RemoteAppIds(dataAppId: null, controlAppId: "daprmq-control");

        Assert.Equal(ControlTypes.Order(), remote.Keys.Order());
        Assert.All(remote.Values, appId => Assert.Equal("daprmq-control", appId));
    }

    [Fact]
    public void Control_ReadsDataTypesFromTheDataApp()
    {
        var remote = Hosting(ActorSet.Control).RemoteAppIds(dataAppId: "daprmq-worker", controlAppId: null);

        Assert.Equal(DataTypes.Order(), remote.Keys.Order());
        Assert.All(remote.Values, appId => Assert.Equal("daprmq-worker", appId));
    }

    [Fact]
    public void Data_WithoutControlAppId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Hosting(ActorSet.Data).RemoteAppIds(dataAppId: "x", controlAppId: " "));
    }

    [Fact]
    public void Control_WithoutDataAppId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Hosting(ActorSet.Control).RemoteAppIds(dataAppId: null, controlAppId: "x"));
    }
}
