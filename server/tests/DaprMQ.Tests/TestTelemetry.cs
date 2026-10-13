namespace DaprMQ.Tests;

/// <summary>Shared telemetry for actor tests that don't assert on metrics.</summary>
internal static class TestTelemetry
{
    public static readonly QueueTelemetry Queue = new(new MetricLabelPolicy(), TimeProvider.System);
    public static readonly TopicTelemetry Topic = new(new MetricLabelPolicy());
}
