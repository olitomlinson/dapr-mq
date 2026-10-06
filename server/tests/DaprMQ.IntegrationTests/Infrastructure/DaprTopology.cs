using System.Text;

namespace DaprMQ.IntegrationTests.Infrastructure;

/// <summary>
/// Shape of the Testcontainers stack. <see cref="Default"/> is the single-everything integration
/// stack. <see cref="Perf"/> matches production: a 3-member scheduler HA cluster and, with more than
/// one API replica, an nginx load balancer in front of them (REST and gRPC).
/// See sdks/testing/PERFORMANCE_TESTS.md#scales-and-topology.
/// </summary>
public sealed record DaprTopology
{
    public const int SchedulerPort = 50006;
    public const string LoadBalancerAlias = "api-lb";

    public int ApiReplicas { get; }
    public int SchedulerReplicas { get; }

    public DaprTopology(int ApiReplicas = 1, int SchedulerReplicas = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ApiReplicas, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(SchedulerReplicas, 1);
        if (SchedulerReplicas % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SchedulerReplicas), SchedulerReplicas, "etcd needs an odd member count.");
        }

        this.ApiReplicas = ApiReplicas;
        this.SchedulerReplicas = SchedulerReplicas;
    }

    public static DaprTopology Default { get; } = new();

    public static DaprTopology Perf(int apiReplicas) => new(apiReplicas, SchedulerReplicas: 3);

    public bool LoadBalanced => ApiReplicas > 1;

    public bool SchedulerHa => SchedulerReplicas > 1;

    public static string ApiServerAlias(int replica) => $"api-server-{replica}";

    public static string SidecarAlias(int replica) => $"dapr-sidecar-{replica}";

    public string SchedulerAlias(int member) => SchedulerHa ? $"dapr-scheduler-{member}" : "dapr-scheduler";

    public string SchedulerHostAddress =>
        string.Join(",", Enumerable.Range(0, SchedulerReplicas).Select(i => $"{SchedulerAlias(i)}:{SchedulerPort}"));

    /// <summary>Command for one member of the HA cluster (embedded etcd, ephemeral data dir).</summary>
    public string[] SchedulerCommand(int member)
    {
        var initialCluster = string.Join(",", Enumerable.Range(0, SchedulerReplicas).Select(i => $"{SchedulerAlias(i)}=http://{SchedulerAlias(i)}:2380"));
        return
        [
            "./scheduler", "--port", $"{SchedulerPort}", "--log-level", "info",
            "--id", SchedulerAlias(member),
            "--etcd-initial-cluster", initialCluster,
            "--etcd-client-listen-address", "0.0.0.0",
            "--etcd-data-dir", "/tmp/etcd",
            "--override-broadcast-host-port", $"{SchedulerAlias(member)}:{SchedulerPort}",
        ];
    }

    /// <summary>nginx.conf for the load balancer: REST on 5000, gRPC (h2c) on 5001.</summary>
    public string NginxConfig()
    {
        string Servers(int port) => string.Concat(Enumerable.Range(0, ApiReplicas).Select(i => $"    server {ApiServerAlias(i)}:{port};\n"));

        return new StringBuilder()
            .Append("worker_processes auto;\n")
            .Append("events { worker_connections 8192; }\n")
            .Append("http {\n")
            .Append("  access_log off;\n")
            .Append("  upstream api_rest {\n").Append(Servers(5000)).Append("    keepalive 256;\n  }\n")
            .Append("  upstream api_grpc {\n").Append(Servers(5001)).Append("    keepalive 256;\n  }\n")
            .Append("  server {\n")
            .Append("    listen 5000;\n")
            .Append("    location / {\n")
            .Append("      proxy_pass http://api_rest;\n")
            .Append("      proxy_http_version 1.1;\n")
            .Append("      proxy_set_header Connection \"\";\n")
            .Append("      proxy_read_timeout 1h;\n")
            .Append("      client_max_body_size 0;\n")
            .Append("    }\n")
            .Append("  }\n")
            .Append("  server {\n")
            .Append("    listen 5001 http2;\n")
            .Append("    http2_max_concurrent_streams 10000;\n")
            .Append("    client_body_timeout 1h;\n")
            .Append("    client_max_body_size 0;\n")
            .Append("    location / {\n")
            .Append("      grpc_pass grpc://api_grpc;\n")
            .Append("      grpc_read_timeout 1h;\n")
            .Append("      grpc_send_timeout 1h;\n")
            .Append("    }\n")
            .Append("  }\n")
            .Append("}\n")
            .ToString();
    }
}
