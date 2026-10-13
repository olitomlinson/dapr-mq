using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;

namespace DaprMQ.Tests;

/// <summary>Collects measurements from one telemetry instance's meter.</summary>
internal sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

    public MetricCollector(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == meter)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        lock (Measurements)
        {
            Measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        }
    }

    public void Observe() => _listener.RecordObservableInstruments();

    public double Sum(string instrument, params (string Key, object? Value)[] tags) =>
        Measurements.Where(m => m.Instrument == instrument && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
            .Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}

public class QueueTelemetryTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    [Theory]
    [InlineData("orders", "orders", "queue", "false")]
    [InlineData("orders-deadletter", "orders", "deadletter", "false")]
    [InlineData("orders-session-42", "orders", "queue", "true")]
    [InlineData("orders-session-42-deadletter", "orders", "deadletter", "true")]
    public void QueueTags_LabelTheParentQueue(string actorId, string queue, string kind, string session)
    {
        var tags = new MetricLabelPolicy().QueueTags(actorId).ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal(new Dictionary<string, object?> { ["queue"] = queue, ["kind"] = kind, ["session"] = session }, tags);
    }

    [Fact]
    public void QueueTags_NoneMode_DropsTheQueueLabel()
    {
        var tags = new MetricLabelPolicy(QueueLabelMode.None).QueueTags("orders");

        Assert.DoesNotContain(tags, t => t.Key == "queue");
    }

    [Fact]
    public void QueueTags_Allowlist_LabelsOthersAsOther()
    {
        var policy = new MetricLabelPolicy(QueueLabelMode.Allowlist, ["orders"]);

        Assert.Contains(policy.QueueTags("orders-deadletter"), t => t.Key == "queue" && (string)t.Value! == "orders");
        Assert.Contains(policy.QueueTags("emails"), t => t.Key == "queue" && (string)t.Value! == MetricLabelPolicy.OtherQueue);
    }

    [Fact]
    public void Depth_SumsSessionsUnderTheirParent_AndKeepsTheLatestUpdate()
    {
        var labels = new MetricLabelPolicy();
        using var telemetry = new QueueTelemetry(labels, _time);
        using var collector = new MetricCollector(telemetry.Meter);

        telemetry.RecordDepth("orders", labels.QueueTags("orders"), ready: 5, locked: 1);
        _time.Advance(TimeSpan.FromSeconds(10));
        telemetry.RecordDepth("orders-session-a", labels.QueueTags("orders-session-a"), ready: 2, locked: 3);
        telemetry.RecordDepth("orders-deadletter", labels.QueueTags("orders-deadletter"), ready: 7, locked: 0);
        collector.Observe();

        Assert.Equal(7, collector.Sum("daprmq.queue.depth", ("queue", "orders"), ("kind", "queue"), ("state", "ready")));
        Assert.Equal(4, collector.Sum("daprmq.queue.depth", ("queue", "orders"), ("kind", "queue"), ("state", "locked")));
        Assert.Equal(7, collector.Sum("daprmq.queue.depth", ("queue", "orders"), ("kind", "deadletter"), ("state", "ready")));
        Assert.Equal(1_700_000_010, collector.Sum("daprmq.queue.depth_updated", ("queue", "orders"), ("kind", "queue")));
        Assert.DoesNotContain(collector.Measurements, m => m.Tags.ContainsKey("session"));
    }

    [Fact]
    public void Depth_LastValueReplacesThePrevious()
    {
        var labels = new MetricLabelPolicy();
        using var telemetry = new QueueTelemetry(labels, _time);
        using var collector = new MetricCollector(telemetry.Meter);

        telemetry.RecordDepth("orders", labels.QueueTags("orders"), ready: 5, locked: 0);
        telemetry.RecordDepth("orders", labels.QueueTags("orders"), ready: 2, locked: 0);
        collector.Observe();

        Assert.Equal(2, collector.Sum("daprmq.queue.depth", ("state", "ready")));
    }

    [Fact]
    public void Counters_IgnoreZero()
    {
        var labels = new MetricLabelPolicy();
        using var telemetry = new QueueTelemetry(labels, _time);
        using var collector = new MetricCollector(telemetry.Meter);

        telemetry.Add(QueueCounter.Enqueued, 0, labels.QueueTags("orders"));
        telemetry.Add(QueueCounter.Enqueued, 3, labels.QueueTags("orders"));

        Assert.Single(collector.Measurements);
        Assert.Equal(3, collector.Sum("daprmq.queue.enqueued", ("queue", "orders")));
    }

    [Fact]
    public void Topic_DispatchedIsLabelledBySubscriber()
    {
        using var telemetry = new TopicTelemetry(new MetricLabelPolicy());
        using var collector = new MetricCollector(telemetry.Meter);

        telemetry.Published("news", 4);
        telemetry.Dispatched("news", "audit", 4);

        Assert.Equal(4, collector.Sum("daprmq.topic.published", ("topic", "news")));
        Assert.Equal(4, collector.Sum("daprmq.topic.dispatched", ("topic", "news"), ("subscriber", "audit")));
    }
}
