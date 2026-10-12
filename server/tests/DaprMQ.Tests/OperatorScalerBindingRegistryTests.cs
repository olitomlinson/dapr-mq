using DaprMQ.Interfaces;
using DaprMQ.Operator.Scaling;
using Microsoft.Extensions.Time.Testing;

namespace DaprMQ.Tests;

public class OperatorScalerBindingRegistryTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    private static ScalerMetadata Meta(string queue, QueueDepthMode mode = QueueDepthMode.Messages) =>
        new() { QueueId = queue, Mode = mode, TargetValue = 10, ActivationValue = 2 };

    [Fact]
    public void ExpiresFiveMinutesAfterTheLastCall()
    {
        using var registry = new ScalerBindingRegistry(_time);
        registry.Record("shop", "orders-consumer", Meta("orders"));

        _time.Advance(TimeSpan.FromMinutes(4));
        registry.Record("shop", "orders-consumer", Meta("orders"));
        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Single(registry.Active());

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(registry.Active());
    }

    [Fact]
    public void OneQueue_MayHaveSeveralScaledObjects()
    {
        using var registry = new ScalerBindingRegistry(_time);
        registry.Record("shop", "orders-consumer", Meta("orders"));
        registry.Record("shop", "orders-audit", Meta("orders"));
        registry.Record("shop", "orders-consumer", Meta("orders"));

        Assert.Equal(["orders-audit", "orders-consumer"], registry.Active().Select(b => b.ScaledObject).Order());
    }

    [Fact]
    public void KeepsTheLastMetricValue_WhenACallReportsNone()
    {
        using var registry = new ScalerBindingRegistry(_time);
        registry.Record("shop", "c", Meta("orders"), metricValue: 42);
        registry.Record("shop", "c", Meta("orders"));

        Assert.Equal(42, Assert.Single(registry.Active()).MetricValue);
    }

    [Fact]
    public void ExportsTheBindingAndMetricValueGauges()
    {
        using var registry = new ScalerBindingRegistry(_time);
        using var collector = new MetricCollector(registry.Meter);
        registry.Record("shop", "orders-consumer", Meta("orders", QueueDepthMode.Sessions), metricValue: 3);
        registry.Record("shop", "idle", Meta("emails"));

        collector.Observe();

        var binding = collector.Measurements.Single(m => m.Instrument == "daprmq.scaler.binding" && (string)m.Tags["queue"]! == "orders");
        Assert.Equal(1, binding.Value);
        Assert.Equal(new Dictionary<string, object?>
        {
            ["queue"] = "orders", ["mode"] = "sessions", ["scaled_object_namespace"] = "shop", ["scaled_object"] = "orders-consumer",
            ["hpa"] = "keda-hpa-orders-consumer", ["target_value"] = "10", ["activation_value"] = "2"
        }, binding.Tags);
        var value = Assert.Single(collector.Measurements, m => m.Instrument == "daprmq.scaler.metric_value");
        Assert.Equal(3, value.Value);
    }
}
