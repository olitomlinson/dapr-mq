using DaprMQ.Interfaces;
using DaprMQ.Operator.Console;
using DaprMQ.Operator.Scaling;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace DaprMQ.Tests;

public class OperatorScalingSamplerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IScalingRegistryClient> _registry = new();
    private readonly Mock<IWorkerAdminClient> _workers = new();
    private readonly Mock<IScalingStatusReader> _kubernetes = new();
    private readonly List<QueueDepthRequest> _depthRequests = [];
    private readonly Dictionary<(string, QueueDepthMode), QueueDepthResult> _depths = new();
    private readonly ScalingSamplerOptions _options = new() { Interval = TimeSpan.FromSeconds(15), History = TimeSpan.FromMinutes(1) };

    public OperatorScalingSamplerTests()
    {
        _workers.Setup(w => w.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueueDepthRequest request, CancellationToken _) =>
            {
                _depthRequests.Add(request);
                return new QueueDepthResponse
                {
                    Results = request.Queries
                        .Select(q => _depths.GetValueOrDefault((q.QueueId, q.Mode)) ?? new QueueDepthResult { QueueId = q.QueueId })
                        .ToList()
                };
            });
    }

    private ScalingSampler CreateSampler(bool kubernetesRead = false) =>
        new(_registry.Object, _workers.Object, kubernetesRead ? _kubernetes.Object : null, _options, _time, NullLogger<ScalingSampler>.Instance);

    private ScaledObjectRegistration Registration(
        string name = "consumer", string queueId = "orders", QueueDepthMode mode = QueueDepthMode.Messages,
        double target = 5, double activation = 0, TimeSpan age = default) => new()
    {
        Namespace = "apps",
        Name = name,
        QueueId = queueId,
        Mode = mode,
        TargetValue = target,
        ActivationValue = activation,
        LastSeenAt = _time.GetUtcNow() - age
    };

    private void Registered(params ScaledObjectRegistration[] registrations) =>
        _registry.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(registrations);

    private void Depth(string queueId, long ready = 0, long locked = 0, int sessions = 0, QueueDepthMode mode = QueueDepthMode.Messages, string? error = null) =>
        _depths[(queueId, mode)] = new QueueDepthResult { QueueId = queueId, Ready = ready, Locked = locked, NonEmptySessions = sessions, Error = error };

    [Fact]
    public async Task Sample_RecordsTheScalersMetric_AndTheReplicasItAsksFor()
    {
        Registered(Registration(target: 5));
        Depth("orders", ready: 4, locked: 3);
        var sampler = CreateSampler();

        await sampler.SampleAsync(CancellationToken.None);

        var target = Assert.Single(sampler.Snapshot(null).Targets);
        Assert.Equal(("apps", "consumer", "orders", 5.0), (target.Namespace, target.Name, target.QueueId, target.TargetValue));
        var sample = Assert.Single(target.Samples);
        Assert.Equal(_time.GetUtcNow().ToUnixTimeSeconds(), sample.T);
        Assert.Equal(7, sample.Metric);
        Assert.Equal(2, sample.DesiredByMetric); // ceil(7 / 5), the HPA's AverageValue formula
        Assert.Null(sample.CurrentReplicas);
        Assert.Null(target.Kubernetes);
    }

    [Theory]
    [InlineData(3, 3, 0)] // not active: KEDA scales to zero
    [InlineData(4, 3, 1)]
    public async Task Sample_AtOrBelowActivation_AsksForNoReplicas(long ready, double activation, int expected)
    {
        Registered(Registration(target: 10, activation: activation));
        Depth("orders", ready: ready);
        var sampler = CreateSampler();

        await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(expected, sampler.Snapshot(null).Targets[0].Samples[0].DesiredByMetric);
    }

    [Fact]
    public async Task Sample_SessionsMode_CountsNonEmptySessions()
    {
        Registered(Registration(mode: QueueDepthMode.Sessions, target: 2));
        Depth("orders", ready: 40, sessions: 3, mode: QueueDepthMode.Sessions);
        var sampler = CreateSampler();

        await sampler.SampleAsync(CancellationToken.None);

        var sample = sampler.Snapshot(null).Targets[0].Samples[0];
        Assert.Equal(3, sample.Metric);
        Assert.Equal(2, sample.DesiredByMetric);
    }

    [Fact]
    public async Task Sample_DepthReadFailed_RecordsNoMetric_NotZero()
    {
        Registered(Registration());
        Depth("orders", error: "HTTP 500");
        var sampler = CreateSampler();

        await sampler.SampleAsync(CancellationToken.None);

        var sample = sampler.Snapshot(null).Targets[0].Samples[0];
        Assert.Null(sample.Metric);
        Assert.Null(sample.DesiredByMetric);
    }

    [Fact]
    public async Task Sample_WorkersUnreachable_RecordsNoMetric()
    {
        Registered(Registration());
        _workers.Setup(w => w.GetDepthsAsync(It.IsAny<QueueDepthRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        var sampler = CreateSampler();

        await sampler.SampleAsync(CancellationToken.None);

        Assert.Null(sampler.Snapshot(null).Targets[0].Samples[0].Metric);
    }

    [Fact]
    public async Task Sample_KeepsOnlyTheHistoryWindow()
    {
        Registered(Registration());
        var sampler = CreateSampler();

        for (var i = 0; i < 6; i++)
        {
            Depth("orders", ready: i);
            await sampler.SampleAsync(CancellationToken.None);
            _time.Advance(_options.Interval);
        }

        // 1 minute at 15 s = 4 samples, newest last.
        Assert.Equal([2.0, 3.0, 4.0, 5.0], sampler.Snapshot(null).Targets[0].Samples.Select(s => s.Metric!.Value));
    }

    [Fact]
    public async Task Sample_ScaledObjectKedaStoppedAskingAbout_IsDropped()
    {
        Registered(Registration("old", age: _options.StaleAfter + TimeSpan.FromSeconds(1)), Registration("live"));
        var sampler = CreateSampler();

        await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(["live"], sampler.Snapshot(null).Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task Sample_RegistryUnreachable_KeepsWhatItHas()
    {
        Registered(Registration());
        var sampler = CreateSampler();
        await sampler.SampleAsync(CancellationToken.None);
        _registry.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("placement down"));

        await sampler.SampleAsync(CancellationToken.None);

        Assert.Single(Assert.Single(sampler.Snapshot(null).Targets).Samples);
    }

    [Fact]
    public async Task Sample_SplitsDepthReadsToTheWorkersLimit()
    {
        Registered(Enumerable.Range(0, 150).Select(i => Registration($"c{i}", $"q{i}")).ToArray());
        var sampler = CreateSampler();

        await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal([100, 50], _depthRequests.Select(r => r.Queries.Count));
        Assert.Equal(150, sampler.Snapshot(null).Targets.Count);
    }

    [Fact]
    public async Task Sample_WithKubernetesRead_RecordsReplicas_ReadingEachScaledObjectOnce()
    {
        // One ScaledObject with two DaprMQ triggers: one Kubernetes read.
        Registered(Registration(queueId: "orders"), Registration(queueId: "returns"));
        _kubernetes.Setup(k => k.ReadAsync("apps", "consumer", It.IsAny<CancellationToken>())).ReturnsAsync(new KubernetesScalingStatus
        {
            Kind = "ScaledObject",
            MinReplicas = 0,
            MaxReplicas = 20,
            CurrentReplicas = 3,
            DesiredReplicas = 4,
            Conditions = [new ScalingCondition { Type = "Ready", Status = "True" }]
        });
        var sampler = CreateSampler(kubernetesRead: true);

        await sampler.SampleAsync(CancellationToken.None);

        var snapshot = sampler.Snapshot(null);
        Assert.True(snapshot.KubernetesRead);
        Assert.All(snapshot.Targets, t =>
        {
            Assert.Equal(20, t.Kubernetes!.MaxReplicas);
            Assert.Equal((3, 4), (t.Samples[0].CurrentReplicas, t.Samples[0].DesiredReplicas));
        });
        _kubernetes.Verify(k => k.ReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sample_KubernetesReadThrows_ReportedOnTheTarget()
    {
        Registered(Registration());
        Depth("orders", ready: 1);
        _kubernetes.Setup(k => k.ReadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("forbidden"));
        var sampler = CreateSampler(kubernetesRead: true);

        await sampler.SampleAsync(CancellationToken.None);

        var target = sampler.Snapshot(null).Targets[0];
        Assert.Equal("forbidden", target.Kubernetes!.Error);
        Assert.Equal(1, target.Samples[0].Metric);
    }

    [Fact]
    public async Task Snapshot_FiltersByQueue()
    {
        Registered(Registration("a", "orders"), Registration("b", "returns"));
        var sampler = CreateSampler();
        await sampler.SampleAsync(CancellationToken.None);

        Assert.Equal(["b"], sampler.Snapshot("returns").Targets.Select(t => t.Name));
    }

    [Fact]
    public async Task Endpoint_ReturnsTheSnapshot()
    {
        Registered(Registration());
        var sampler = CreateSampler();
        await sampler.SampleAsync(CancellationToken.None);

        var result = ConsoleEndpoints.HandleScaling("orders", sampler);

        var ok = Assert.IsType<Ok<ConsoleScaling>>(result);
        Assert.Equal(15, ok.Value!.SampleIntervalSeconds);
        Assert.Equal(60, ok.Value.HistorySeconds);
        Assert.Single(ok.Value.Targets);
    }
}
