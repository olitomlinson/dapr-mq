using DaprMQ.Interfaces;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class ScalingRegistryReadServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IQueueActorStateReader> _reader = new();

    private ScalingRegistryReadService CreateService() => new(_reader.Object, "ScalingRegistryActor", _time);

    private void Stored(params ScaledObjectRegistration[] registrations) =>
        _reader.Setup(r => r.ReadStateAsync<Dictionary<string, ScaledObjectRegistration>>(
                "ScalingRegistryActor", ScalingRegistry.ActorId, ScalingRegistryActor.RegistrationsKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(registrations.Length == 0 ? null : registrations.ToDictionary(r => r.Key));

    private ScaledObjectRegistration Registration(string name, TimeSpan age = default) => new()
    {
        Namespace = "apps",
        Name = name,
        QueueId = "orders",
        LastSeenAt = _time.GetUtcNow() - age
    };

    [Fact]
    public async Task List_ReadsCommittedState_SortedByKey()
    {
        // Through the actor-state API: the registry actor is never called or activated.
        Stored(Registration("b"), Registration("a"));

        var list = await CreateService().ListAsync();

        Assert.Equal(["a", "b"], list.Registrations.Select(r => r.Name));
    }

    [Fact]
    public async Task List_SkipsRegistrationsPastRetention()
    {
        Stored(Registration("gone", ScalingRegistry.RetainFor + TimeSpan.FromMinutes(1)), Registration("kept"));

        var list = await CreateService().ListAsync();

        Assert.Equal(["kept"], list.Registrations.Select(r => r.Name));
    }

    [Fact]
    public async Task List_NothingRegistered_IsEmpty()
    {
        Stored();

        Assert.Empty((await CreateService().ListAsync()).Registrations);
    }
}
