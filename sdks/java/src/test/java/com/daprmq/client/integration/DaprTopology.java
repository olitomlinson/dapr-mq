package com.daprmq.client.integration;

import java.util.ArrayList;
import java.util.List;
import java.util.stream.Collectors;
import java.util.stream.IntStream;

/**
 * Shape of the Testcontainers stack. The default is the single-everything integration stack;
 * {@link #perf(int)} scales it out as sdks/testing/PERFORMANCE_TESTS.md#scales-and-topology
 * describes: a 3-member scheduler HA cluster and n API replicas (each with its own sidecar), behind
 * an nginx load balancer when n > 1.
 */
public record DaprTopology(int apiReplicas, int schedulerReplicas) {
    public static final int SCHEDULER_PORT = 50006;
    public static final String LOAD_BALANCER_ALIAS = "api-lb";

    public DaprTopology {
        if (apiReplicas < 1 || schedulerReplicas < 1) {
            throw new IllegalArgumentException("apiReplicas and schedulerReplicas must be >= 1.");
        }
        if (schedulerReplicas % 2 == 0) {
            throw new IllegalArgumentException("etcd needs an odd scheduler member count.");
        }
    }

    public static DaprTopology integration() {
        return new DaprTopology(1, 1);
    }

    public static DaprTopology perf(int apiReplicas) {
        return new DaprTopology(apiReplicas, 3);
    }

    public boolean loadBalanced() {
        return apiReplicas > 1;
    }

    public boolean schedulerHa() {
        return schedulerReplicas > 1;
    }

    public static String apiServerAlias(int replica) {
        return "api-server-" + replica;
    }

    public static String sidecarAlias(int replica) {
        return "dapr-sidecar-" + replica;
    }

    public String schedulerAlias(int member) {
        return schedulerHa() ? "dapr-scheduler-" + member : "dapr-scheduler";
    }

    public String schedulerHostAddress() {
        return IntStream.range(0, schedulerReplicas).mapToObj(i -> schedulerAlias(i) + ":" + SCHEDULER_PORT).collect(Collectors.joining(","));
    }

    /** One member of the HA cluster (embedded etcd, ephemeral data dir). */
    public String[] schedulerCommand(int member) {
        String initialCluster = IntStream.range(0, schedulerReplicas)
                .mapToObj(i -> schedulerAlias(i) + "=http://" + schedulerAlias(i) + ":2380").collect(Collectors.joining(","));
        String alias = schedulerAlias(member);
        return new String[] {
            "./scheduler", "--port", String.valueOf(SCHEDULER_PORT), "--log-level", "info",
            "--id", alias,
            "--etcd-initial-cluster", initialCluster,
            "--etcd-client-listen-address", "0.0.0.0",
            "--etcd-data-dir", "/tmp/etcd",
            "--override-broadcast-host-port", alias + ":" + SCHEDULER_PORT,
        };
    }

    /** REST on 5000, gRPC (h2c) on 5001, with 1 h timeouts so ConsumeSession streams survive. */
    public String nginxConfig() {
        List<String> lines = new ArrayList<>(List.of("worker_processes auto;", "events { worker_connections 8192; }", "http {", "  access_log off;"));
        for (String[] upstream : new String[][] {{"api_rest", "5000"}, {"api_grpc", "5001"}}) {
            lines.add("  upstream " + upstream[0] + " {");
            for (int i = 0; i < apiReplicas; i++) {
                lines.add("    server " + apiServerAlias(i) + ":" + upstream[1] + ";");
            }
            lines.add("    keepalive 256;");
            lines.add("  }");
        }
        lines.addAll(List.of(
                "  server {",
                "    listen 5000;",
                "    location / {",
                "      proxy_pass http://api_rest;",
                "      proxy_http_version 1.1;",
                "      proxy_set_header Connection \"\";",
                "      proxy_read_timeout 1h;",
                "      client_max_body_size 0;",
                "    }",
                "  }",
                "  server {",
                "    listen 5001 http2;",
                "    http2_max_concurrent_streams 10000;",
                "    client_body_timeout 1h;",
                "    client_max_body_size 0;",
                "    location / {",
                "      grpc_pass grpc://api_grpc;",
                "      grpc_read_timeout 1h;",
                "      grpc_send_timeout 1h;",
                "    }",
                "  }",
                "}"));
        return String.join("\n", lines) + "\n";
    }
}
