using System.Text;

namespace DaprMQ.IntegrationTests.Infrastructure;

/// <summary>
/// Shape of the Testcontainers stack. <see cref="Default"/> is the single-everything integration
/// stack. <see cref="Perf"/> matches production: a 3-member scheduler HA cluster and, with more than
/// one API replica, an nginx load balancer in front of them (REST and gRPC).
/// With <see cref="Workers"/> > 0 the stack is split like Helm: the API replicas become gateways
/// (REGISTER_ACTORS=false) and that many workers (ENABLE_API=false) host the actors. With 0 every
/// API replica also hosts actors ("combined").
/// See sdks/testing/PERFORMANCE_TESTS.md#scales-and-topology.
/// </summary>
public sealed record DaprTopology
{
    public const int SchedulerPort = 50006;
    public const string LoadBalancerAlias = "api-lb";

    public const string EnvironmentVariable = "DAPRMQ_TEST_TOPOLOGY";

    public int ApiReplicas { get; }
    public int SchedulerReplicas { get; }

    /// <summary>Actor-hosting workers behind the API replicas; 0 = the API replicas host the actors.</summary>
    public int Workers { get; }

    public DaprTopology(int ApiReplicas = 1, int SchedulerReplicas = 1, int Workers = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ApiReplicas, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(SchedulerReplicas, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(Workers);
        if (SchedulerReplicas % 2 == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SchedulerReplicas), SchedulerReplicas, "etcd needs an odd member count.");
        }

        this.ApiReplicas = ApiReplicas;
        this.SchedulerReplicas = SchedulerReplicas;
        this.Workers = Workers;
    }

    public static DaprTopology Default { get; } = new();

    /// <summary>One gateway in front of one worker: the smallest production-shaped stack.</summary>
    public static DaprTopology Split { get; } = new(Workers: 1);

    public static DaprTopology Perf(int apiReplicas, int workers = 0) => new(apiReplicas, SchedulerReplicas: 3, workers);

    /// <summary>
    /// The integration stack named by <see cref="EnvironmentVariable"/> ("combined", the default, or
    /// "split"), so CI can run the same suites against both shapes.
    /// </summary>
    public static DaprTopology FromEnvironment(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(EnvironmentVariable);
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "combined" => Default,
            "split" => Split,
            _ => throw new ArgumentException($"{EnvironmentVariable} must be 'combined' or 'split', got '{value}'.")
        };
    }

    public bool IsSplit => Workers > 0;

    public bool LoadBalanced => ApiReplicas > 1;

    public bool SchedulerHa => SchedulerReplicas > 1;

    public static string ApiServerAlias(int replica) => $"api-server-{replica}";

    public static string SidecarAlias(int replica) => $"dapr-sidecar-{replica}";

    public static string WorkerAlias(int worker) => $"daprmq-worker-{worker}";

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
