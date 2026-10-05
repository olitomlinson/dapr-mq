using System.Collections.Concurrent;
using DaprMQ.Interfaces;
using DaprMQ.Operator.Scaling;
using Externalscaler;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace DaprMQ.Tests;

public class OperatorExternalScalerTests
{
    private readonly Mock<IWorkerDepthClient> _client = new();
    private readonly Mock<ServerCallContext> _context = new();

    private DaprMQExternalScaler CreateScaler(TimeSpan? pollInterval = null) =>
        new(_client.Object, new ScalerOptions { StreamPollInterval = pollInterval ?? TimeSpan.FromMilliseconds(10) },
            NullLogger<DaprMQExternalScaler>.Instance);

    private static ScaledObjectRef Ref(params (string Key, string Value)[] metadata)
    {
        var scaledObject = new ScaledObjectRef { Name = "consumer", Namespace = "default" };
        foreach (var (key, value) in metadata)
        {
            scaledObject.ScalerMetadata[key] = value;
        }

        return scaledObject;
    }

    private void SetupDepth(QueueDepthResult result) =>
        _client.Setup(c => c.GetDepthAsync(It.IsAny<QueueDepthQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);

    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly ConcurrentQueue<T> _written = new();
        public IReadOnlyCollection<T> Written => _written;
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            _written.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task GetMetricSpec_ReportsTargetUnderMetricName()
    {
        var response = await CreateScaler().GetMetricSpec(Ref(("queueId", "orders"), ("targetValue", "25")), _context.Object);

        var spec = Assert.Single(response.MetricSpecs);
        Assert.Equal("daprmq-messages-orders", spec.MetricName);
        Assert.Equal(25, spec.TargetSizeFloat);
        Assert.Equal(25, spec.TargetSize);
    }

    [Fact]
    public async Task GetMetrics_Messages_IncludesLockedByDefault()
    {
        SetupDepth(new QueueDepthResult { QueueId = "orders", Ready = 7, Locked = 3 });

        var response = await CreateScaler().GetMetrics(
            new GetMetricsRequest { ScaledObjectRef = Ref(("queueId", "orders")), MetricName = "s0-daprmq-messages-orders" }, _context.Object);

        var value = Assert.Single(response.MetricValues);
        Assert.Equal("s0-daprmq-messages-orders", value.MetricName);
        Assert.Equal(10, value.MetricValueFloat);
        Assert.Equal(10, value.MetricValue_);
    }

    [Fact]
    public async Task GetMetrics_Messages_ExcludesLockedWhenDisabled()
    {
        SetupDepth(new QueueDepthResult { QueueId = "orders", Ready = 7, Locked = 3 });

        var response = await CreateScaler().GetMetrics(
            new GetMetricsRequest { ScaledObjectRef = Ref(("queueId", "orders"), ("includeLocked", "false")) }, _context.Object);

        Assert.Equal(7, response.MetricValues[0].MetricValueFloat);
    }

    [Fact]
    public async Task GetMetrics_Sessions_ReportsNonEmptySessions()
    {
        SetupDepth(new QueueDepthResult { QueueId = "orders", Ready = 40, Locked = 2, NonEmptySessions = 4 });

        var response = await CreateScaler().GetMetrics(
            new GetMetricsRequest { ScaledObjectRef = Ref(("queueId", "orders"), ("mode", "sessions")) }, _context.Object);

        Assert.Equal(4, response.MetricValues[0].MetricValueFloat);
        _client.Verify(c => c.GetDepthAsync(It.Is<QueueDepthQuery>(q => q.Mode == QueueDepthMode.Sessions), It.IsAny<CancellationToken>()));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(5, 5, false)]
    [InlineData(6, 5, true)]
    public async Task IsActive_ComparesAgainstActivationValue(long ready, int activation, bool expected)
    {
        SetupDepth(new QueueDepthResult { QueueId = "orders", Ready = ready });

        var response = await CreateScaler().IsActive(Ref(("queueId", "orders"), ("activationValue", activation.ToString())), _context.Object);

        Assert.Equal(expected, response.Result);
    }

    [Fact]
    public async Task WorkerErrorResult_IsUnavailableNotZero()
    {
        // Reporting 0 here would scale every consumer to zero during a DaprMQ outage.
        SetupDepth(new QueueDepthResult { QueueId = "orders", Error = "HTTP 500" });

        var ex = await Assert.ThrowsAsync<RpcException>(() => CreateScaler().IsActive(Ref(("queueId", "orders")), _context.Object));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public async Task WorkerUnreachable_IsUnavailable()
    {
        _client.Setup(c => c.GetDepthAsync(It.IsAny<QueueDepthQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => CreateScaler().GetMetrics(
            new GetMetricsRequest { ScaledObjectRef = Ref(("queueId", "orders")) }, _context.Object));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
    }

    [Fact]
    public async Task InvalidMetadata_IsInvalidArgument()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => CreateScaler().GetMetricSpec(Ref(), _context.Object));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task StreamIsActive_PushesInitialStateThenOnlyChanges()
    {
        var depths = new Queue<long>([0, 0, 3, 3, 0]);
        var cts = new CancellationTokenSource();
        _client.Setup(c => c.GetDepthAsync(It.IsAny<QueueDepthQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (depths.Count == 1)
                {
                    cts.Cancel(); // last poll: end the stream once it's been reported
                }

                return new QueueDepthResult { QueueId = "orders", Ready = depths.Count > 0 ? depths.Dequeue() : 0 };
            });
        _context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(cts.Token);
        var writer = new FakeServerStreamWriter<IsActiveResponse>();

        await CreateScaler().StreamIsActive(Ref(("queueId", "orders")), writer, _context.Object);

        Assert.Equal([false, true, false], writer.Written.Select(w => w.Result));
    }

    [Fact]
    public async Task StreamIsActive_TransientErrorDoesNotEndStream()
    {
        var cts = new CancellationTokenSource();
        var calls = 0;
        _client.Setup(c => c.GetDepthAsync(It.IsAny<QueueDepthQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new HttpRequestException("blip");
                }

                cts.Cancel();
                return new QueueDepthResult { QueueId = "orders", Ready = 2 };
            });
        _context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(cts.Token);
        var writer = new FakeServerStreamWriter<IsActiveResponse>();

        await CreateScaler().StreamIsActive(Ref(("queueId", "orders")), writer, _context.Object);

        Assert.Equal([true], writer.Written.Select(w => w.Result));
    }
}
