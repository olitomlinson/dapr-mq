using DaprMQ.IntegrationTests.Infrastructure;

namespace DaprMQ.Client.Perf.Tests;

public class DaprTopologyTests
{
    [Fact]
    public void DefaultIsTheIntegrationStack()
    {
        var topology = DaprTopology.Default;

        Assert.Equal(1, topology.ApiReplicas);
        Assert.Equal(1, topology.SchedulerReplicas);
        Assert.False(topology.LoadBalanced);
        Assert.Equal("dapr-scheduler:50006", topology.SchedulerHostAddress);
    }

    [Fact]
    public void PerfStackHasAnHaSchedulerAndBalancesOnlyWithMoreThanOneReplica()
    {
        Assert.False(DaprTopology.Perf(apiReplicas: 1).LoadBalanced);
        Assert.Equal(3, DaprTopology.Perf(apiReplicas: 1).SchedulerReplicas);
        Assert.True(DaprTopology.Perf(apiReplicas: 3).LoadBalanced);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)] // etcd needs an odd member count for a useful quorum
    public void RejectsInvalidReplicaCounts(int api, int scheduler)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DaprTopology(api, scheduler));
    }

    [Fact]
    public void HaSchedulerMembersShareOneInitialClusterAndBroadcastTheirOwnAlias()
    {
        var topology = new DaprTopology(ApiReplicas: 1, SchedulerReplicas: 3);

        Assert.Equal(
            [
                "./scheduler", "--port", "50006", "--log-level", "info",
                "--id", "dapr-scheduler-1",
                "--etcd-initial-cluster", "dapr-scheduler-0=http://dapr-scheduler-0:2380,dapr-scheduler-1=http://dapr-scheduler-1:2380,dapr-scheduler-2=http://dapr-scheduler-2:2380",
                "--etcd-client-listen-address", "0.0.0.0",
                "--etcd-data-dir", "/tmp/etcd",
                "--override-broadcast-host-port", "dapr-scheduler-1:50006",
            ],
            topology.SchedulerCommand(1));
        Assert.Equal("dapr-scheduler-0:50006,dapr-scheduler-1:50006,dapr-scheduler-2:50006", topology.SchedulerHostAddress);
    }

    [Fact]
    public void NginxConfigBalancesRestAndGrpcAcrossEveryReplica()
    {
        var config = new DaprTopology(ApiReplicas: 2, SchedulerReplicas: 3).NginxConfig();

        Assert.Contains("server api-server-0:5000;", config);
        Assert.Contains("server api-server-1:5000;", config);
        Assert.Contains("server api-server-0:5001;", config);
        Assert.Contains("server api-server-1:5001;", config);
        Assert.Contains("listen 5001 http2;", config);
        Assert.Contains("grpc_pass grpc://api_grpc;", config);
        // ConsumeSession streams stay open for minutes.
        Assert.Contains("grpc_read_timeout 1h;", config);
    }
}
