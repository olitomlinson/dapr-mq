using Dapr.Actors;
using Dapr.Actors.Runtime;
using DaprMQ.Interfaces;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class ScalingRegistryActorTests
{
    private readonly Dictionary<string, object> _state = new();
    private readonly Mock<IActorStateManager> _stateManager = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    public ScalingRegistryActorTests()
    {
        _stateManager.Setup(m => m.TryGetStateAsync<Dictionary<string, ScaledObjectRegistration>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => _state.TryGetValue(key, out var value)
                ? new ConditionalValue<Dictionary<string, ScaledObjectRegistration>>(true, (Dictionary<string, ScaledObjectRegistration>)value)
                : new ConditionalValue<Dictionary<string, ScaledObjectRegistration>>(false, null!));
        _stateManager.Setup(m => m.SetStateAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, ScaledObjectRegistration>>(), It.IsAny<CancellationToken>()))
            .Returns((string key, Dictionary<string, ScaledObjectRegistration> value, CancellationToken _) =>
            {
                _state[key] = new Dictionary<string, ScaledObjectRegistration>(value);
                return Task.CompletedTask;
            });
        _stateManager.Setup(m => m.SaveStateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private async Task<ScalingRegistryActor> ActivateAsync()
    {
        var host = ActorHost.CreateForTest<ScalingRegistryActor>(new ActorTestOptions { ActorId = new ActorId(ScalingRegistry.ActorId) });
        var actor = new ScalingRegistryActor(host, _time);
        typeof(Actor).GetProperty("StateManager")!.SetValue(actor, _stateManager.Object);
        var onActivate = typeof(ScalingRegistryActor).GetMethod("OnActivateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)onActivate.Invoke(actor, null)!;
        return actor;
    }

    private ScaledObjectRegistration Registration(string name, TimeSpan age = default, double target = 10) => new()
    {
        Namespace = "apps",
        Name = name,
        QueueId = "orders",
        TargetValue = target,
        LastSeenAt = _time.GetUtcNow() - age
    };

    private static RegisterScaledObjectsRequest Request(params ScaledObjectRegistration[] registrations) => new() { Registrations = [.. registrations] };

    private Dictionary<string, ScaledObjectRegistration> Stored => (Dictionary<string, ScaledObjectRegistration>)_state[ScalingRegistryActor.RegistrationsKey];

    [Fact]
    public async Task Register_SavesThemInOneWrite()
    {
        var actor = await ActivateAsync();

        await actor.RegisterAsync(Request(Registration("b"), Registration("a")));

        Assert.Equal(["a", "b"], Stored.Values.Select(r => r.Name).Order());
        _stateManager.Verify(m => m.SaveStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Register_Newer_ReplacesConfigAndLastSeen()
    {
        var actor = await ActivateAsync();
        await actor.RegisterAsync(Request(Registration("a", TimeSpan.FromMinutes(1), target: 10)));

        await actor.RegisterAsync(Request(Registration("a", target: 20)));

        var stored = Assert.Single(Stored.Values);
        Assert.Equal(20, stored.TargetValue);
        Assert.Equal(_time.GetUtcNow(), stored.LastSeenAt);
    }

    [Fact]
    public async Task Register_Older_DoesNotReplaceANewerOne()
    {
        // Two operator pods can report the same ScaledObject; the stale one mustn't win.
        var actor = await ActivateAsync();
        await actor.RegisterAsync(Request(Registration("a", target: 20)));

        await actor.RegisterAsync(Request(Registration("a", TimeSpan.FromMinutes(1), target: 10)));

        Assert.Equal(20, Assert.Single(Stored.Values).TargetValue);
    }

    [Fact]
    public async Task Register_DropsRegistrationsNotSeenWithinRetention()
    {
        var actor = await ActivateAsync();
        await actor.RegisterAsync(Request(Registration("gone"), Registration("kept")));

        _time.Advance(ScalingRegistry.RetainFor + TimeSpan.FromMinutes(1));
        await actor.RegisterAsync(Request(Registration("kept")));

        Assert.Equal(["kept"], Stored.Values.Select(r => r.Name));
    }

    [Fact]
    public async Task Activate_LoadsStoredRegistrations()
    {
        var first = await ActivateAsync();
        await first.RegisterAsync(Request(Registration("a", TimeSpan.FromMinutes(1), target: 20)));

        var second = await ActivateAsync();
        await second.RegisterAsync(Request(Registration("b")));

        Assert.Equal(["a", "b"], Stored.Values.Select(r => r.Name).Order());
    }
}
