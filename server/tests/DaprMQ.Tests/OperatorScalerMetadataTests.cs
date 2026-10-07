using DaprMQ.Interfaces;
using DaprMQ.Operator.Scaling;
using Grpc.Core;

namespace DaprMQ.Tests;

public class OperatorScalerMetadataTests
{
    private static ScalerMetadata Parse(params (string Key, string Value)[] entries) =>
        ScalerMetadata.Parse(entries.ToDictionary(e => e.Key, e => e.Value));

    private static void AssertInvalid(params (string Key, string Value)[] entries)
    {
        var ex = Assert.Throws<RpcException>(() => Parse(entries));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public void QueueIdOnly_UsesDefaults()
    {
        var metadata = Parse(("queueId", "orders"));

        Assert.Equal("orders", metadata.QueueId);
        Assert.Equal(QueueDepthMode.Messages, metadata.Mode);
        Assert.Equal(10, metadata.TargetValue);
        Assert.Equal(0, metadata.ActivationValue);
        Assert.False(metadata.IncludeDeadLetter);
    }

    [Fact]
    public void AllFields_Parsed()
    {
        var metadata = Parse(("queueId", "orders"), ("mode", "Sessions"), ("targetValue", "2.5"),
            ("activationValue", "1"));

        Assert.Equal(QueueDepthMode.Sessions, metadata.Mode);
        Assert.Equal(2.5, metadata.TargetValue);
        Assert.Equal(1, metadata.ActivationValue);
    }

    [Fact]
    public void ToQuery_CarriesQueueModeAndDeadLetter()
    {
        var query = Parse(("queueId", "orders"), ("includeDeadLetter", "true")).ToQuery();

        Assert.Equal(new QueueDepthQuery { QueueId = "orders", Mode = QueueDepthMode.Messages, IncludeDeadLetter = true }, query);
    }

    [Fact]
    public void MetricName_IsStableAndKubernetesSafe()
    {
        Assert.Equal("daprmq-messages-orders-eu-1", Parse(("queueId", "Orders_EU.1")).MetricName);
        Assert.Equal("daprmq-sessions-orders", Parse(("queueId", "orders"), ("mode", "sessions")).MetricName);
    }

    [Fact] public void MissingQueueId_Invalid() => AssertInvalid(("mode", "messages"));
    [Fact] public void BlankQueueId_Invalid() => AssertInvalid(("queueId", " "));
    [Fact] public void UnknownMode_Invalid() => AssertInvalid(("queueId", "orders"), ("mode", "bytes"));
    [Fact] public void NonPositiveTarget_Invalid() => AssertInvalid(("queueId", "orders"), ("targetValue", "0"));
    [Fact] public void NonNumericTarget_Invalid() => AssertInvalid(("queueId", "orders"), ("targetValue", "ten"));
    [Fact] public void NegativeActivation_Invalid() => AssertInvalid(("queueId", "orders"), ("activationValue", "-1"));
    [Fact] public void NonBooleanIncludeDeadLetter_Invalid() => AssertInvalid(("queueId", "orders"), ("includeDeadLetter", "yes"));
    [Fact] public void SessionsWithDeadLetter_Invalid() => AssertInvalid(("queueId", "orders"), ("mode", "sessions"), ("includeDeadLetter", "true"));
}
