using DaprMQ.Interfaces;
using DaprMQ.Operator.Scaling;
using Externalscaler;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class OperatorScaledObjectReporterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IScalingRegistryClient> _registry = new();
    private readonly List<List<ScaledObjectRegistration>> _sent = [];
    private readonly ScaledObjectReporter _reporter;

    public OperatorScaledObjectReporterTests()
    {
        _registry.Setup(r => r.RegisterAsync(It.IsAny<IReadOnlyList<ScaledObjectRegistration>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<ScaledObjectRegistration> registrations, CancellationToken _) => _sent.Add([.. registrations]))
            .Returns(Task.CompletedTask);
        _reporter = new ScaledObjectReporter(_registry.Object, new ScaledObjectReporterOptions { Heartbeat = TimeSpan.FromSeconds(60) },
            _time, NullLogger<ScaledObjectReporter>.Instance);
    }

    private void Observe(string name = "consumer", string targetValue = "10")
    {
        var scaledObject = new ScaledObjectRef { Name = name, Namespace = "apps" };
        scaledObject.ScalerMetadata["queueId"] = "orders";
        scaledObject.ScalerMetadata["targetValue"] = targetValue;
        _reporter.Observe(scaledObject, ScalerMetadata.Parse(scaledObject.ScalerMetadata));
    }

    [Fact]
    public async Task Flush_SendsANewScaledObject_WithItsTrigger()
    {
        Observe();

        await _reporter.FlushAsync(CancellationToken.None);

        var sent = Assert.Single(Assert.Single(_sent));
        Assert.Equal(("apps", "consumer", "orders", QueueDepthMode.Messages, 10.0), (sent.Namespace, sent.Name, sent.QueueId, sent.Mode, sent.TargetValue));
        Assert.Equal(_time.GetUtcNow(), sent.LastSeenAt);
    }

    [Fact]
    public async Task Flush_NothingNew_SendsNothing()
    {
        // KEDA calls every second on a busy queue; the registry sees a heartbeat, not every call.
        Observe();
        await _reporter.FlushAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(10));
        Observe();

        await _reporter.FlushAsync(CancellationToken.None);
        await _reporter.FlushAsync(CancellationToken.None);

        Assert.Single(_sent);
    }

    [Fact]
    public async Task Flush_AfterHeartbeat_ResendsWithTheLatestCallTime()
    {
        Observe();
        await _reporter.FlushAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(50));
        Observe();
        var lastCall = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(10));

        await _reporter.FlushAsync(CancellationToken.None);

        Assert.Equal(2, _sent.Count);
        Assert.Equal(lastCall, Assert.Single(_sent[1]).LastSeenAt);
    }

    [Fact]
    public async Task Flush_NotCalledSinceLastSend_NoHeartbeat()
    {
        // A deleted ScaledObject stops being reported, so the registry can let it expire.
        Observe();
        await _reporter.FlushAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(5));

        await _reporter.FlushAsync(CancellationToken.None);

        Assert.Single(_sent);
    }

    [Fact]
    public async Task Flush_ChangedTrigger_SentWithoutWaitingForTheHeartbeat()
    {
        Observe(targetValue: "10");
        await _reporter.FlushAsync(CancellationToken.None);
        Observe(targetValue: "20");

        await _reporter.FlushAsync(CancellationToken.None);

        Assert.Equal(20, Assert.Single(_sent[1]).TargetValue);
    }

    [Fact]
    public async Task Flush_Failure_RetriedOnTheNextFlush()
    {
        _registry.SetupSequence(r => r.RegisterAsync(It.IsAny<IReadOnlyList<ScaledObjectRegistration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("placement down"))
            .Returns(Task.CompletedTask);
        Observe();

        await _reporter.FlushAsync(CancellationToken.None);
        await _reporter.FlushAsync(CancellationToken.None);

        _registry.Verify(r => r.RegisterAsync(It.Is<IReadOnlyList<ScaledObjectRegistration>>(l => l.Count == 1), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Flush_SeveralScaledObjects_OneCall()
    {
        Observe("a");
        Observe("b");

        await _reporter.FlushAsync(CancellationToken.None);

        Assert.Equal(["a", "b"], Assert.Single(_sent).Select(r => r.Name).Order());
    }
}
