using System.Text.Json;
using DaprMQ.Operator.Console;

namespace DaprMQ.Tests;

public class OperatorKubernetesScalingStatusTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParseScaledObject_ReadsBoundsHpaAndConditions()
    {
        var (status, hpaName) = KubernetesScalingStatusReader.ParseScaledObject(Json("""
            {
              "metadata": { "name": "orders-consumer" },
              "spec": { "minReplicaCount": 1, "maxReplicaCount": 20 },
              "status": {
                "hpaName": "custom-hpa",
                "conditions": [
                  { "type": "Ready", "status": "True", "reason": "ScaledObjectReady", "message": "ok" },
                  { "type": "Active", "status": "False" },
                  { "type": "Fallback", "status": "False" }
                ]
              }
            }
            """));

        Assert.Equal("custom-hpa", hpaName);
        Assert.Equal(("ScaledObject", 1, 20), (status.Kind, status.MinReplicas, status.MaxReplicas));
        Assert.Equal(["Ready=True", "Active=False", "Fallback=False"], status.Conditions.Select(c => $"{c.Type}={c.Status}"));
        Assert.Equal("ScaledObjectReady", status.Conditions[0].Reason);
    }

    [Fact]
    public void ParseScaledObject_Defaults_MatchKeda()
    {
        // Unset bounds are KEDA's defaults (0 and 100); no hpaName yet means keda-hpa-{name}.
        var (status, hpaName) = KubernetesScalingStatusReader.ParseScaledObject(Json("""{ "metadata": { "name": "c" }, "spec": {} }"""));

        Assert.Equal("keda-hpa-c", hpaName);
        Assert.Equal((0, 100), (status.MinReplicas, status.MaxReplicas));
        Assert.Empty(status.Conditions);
    }

    [Fact]
    public void ParseScaledJob_ReadsBounds()
    {
        var status = KubernetesScalingStatusReader.ParseScaledJob(Json("""
            { "metadata": { "name": "j" }, "spec": { "maxReplicaCount": 5 }, "status": { "conditions": [{ "type": "Ready", "status": "True" }] } }
            """));

        Assert.Equal(("ScaledJob", 0, 5), (status.Kind, status.MinReplicas, status.MaxReplicas));
        Assert.Single(status.Conditions);
    }
}
