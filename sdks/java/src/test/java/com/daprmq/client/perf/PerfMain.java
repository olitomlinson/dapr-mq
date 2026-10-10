package com.daprmq.client.perf;

import com.daprmq.client.DaprMQClient;
import com.daprmq.client.integration.DaprMQServer;
import com.daprmq.client.integration.DaprTopology;
import com.daprmq.client.perf.Records.RunContext;
import com.daprmq.client.perf.Records.RunEnvironment;
import io.grpc.ManagedChannel;
import io.grpc.ManagedChannelBuilder;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

/**
 * Java SDK performance harness (sdks/testing/PERFORMANCE_TESTS.md). From sdks/java:
 *
 * <pre>
 *   mvn -q -Pperf test-compile exec:exec -Dperf.args="--suite pr"
 *   mvn -q -Pperf test-compile exec:exec -Dperf.args="--profile enqueue-ramp --api-replicas 3"
 * </pre>
 *
 * Runs go to {@code <out>/sdk-java}; the cross-SDK report and the regression table come from the
 * .NET PerfReport tool, so {@code dotnet} must be on PATH for those (the runs are saved regardless).
 */
public final class PerfMain {
    private PerfMain() {
    }

    public static void main(String[] args) throws Exception {
        PerfOptions options;
        try {
            options = PerfOptions.parse(args);
        } catch (IllegalArgumentException e) {
            System.err.println(e.getMessage());
            System.exit(2);
            return;
        }
        Files.createDirectories(options.outDir().resolve("sdk-java"));
        System.exit(options.reportOnly() ? perfReport("report", "--results", options.outDir().toString()) : run(options));
    }

    /** Runs the shared .NET report tool; its own output goes straight to the console. */
    static int perfReport(String... args) throws InterruptedException {
        Path project = repoRoot().resolve("sdks/testing/perf/DaprMQ.PerfReport");
        List<String> command = new ArrayList<>(List.of("dotnet", "run", "-c", "Release", "--project", project.toString(), "--"));
        command.addAll(List.of(args));
        try {
            return new ProcessBuilder(command).inheritIO().start().waitFor();
        } catch (IOException e) {
            System.err.println("dotnet not found: skipping the report and regression check.");
            return 0;
        }
    }

    private static Path repoRoot() {
        return PerfOptions.defaultOutDir().getParent();
    }

    private static int run(PerfOptions options) throws Exception {
        DaprMQServer server = null;
        String httpUrl;
        String grpcAddress;
        String description;
        Integer schedulerReplicas = null;
        if (options.http() != null) {
            httpUrl = options.http();
            grpcAddress = options.grpc();
            description = "external " + options.http();
        } else {
            if (!DaprMQServer.apiImageAvailable()) {
                System.err.println("Docker image " + DaprMQServer.API_IMAGE + " not found - run ./build-and-test.sh --skip-tests from the repo root first.");
                return 2;
            }
            DaprTopology topology = DaprTopology.perf(options.apiReplicas());
            schedulerReplicas = topology.schedulerReplicas();
            System.out.printf("Starting Testcontainers stack (%s, %d API replica(s), %d schedulers)...%n",
                    DaprMQServer.API_IMAGE, topology.apiReplicas(), schedulerReplicas);
            server = DaprMQServer.start(topology);
            httpUrl = server.httpUrl();
            grpcAddress = server.grpcAddress();
            description = "testcontainers " + DaprMQServer.API_IMAGE;
        }

        int exitCode = 0;
        List<String> runIds = new ArrayList<>();
        ManagedChannel channel = ManagedChannelBuilder.forTarget(grpcAddress).usePlaintext().build();
        try (DaprMQClient client = new DaprMQClient(httpUrl, channel)) {
            if (!client.waitForReady(Duration.ofMinutes(2))) {
                throw new IllegalStateException("Server did not report daprmq.DaprMQ.operations SERVING within 2 minutes.");
            }
            RunEnvironment environment = RunEnvironment.capture(options.envLabel(), description);
            for (String profile : options.profiles()) {
                System.out.println();
                System.out.println("=== Profile " + profile + " ===");
                RunContext context = new RunContext(Instant.now(), environment, options.scale(profile), options.apiReplicas(), schedulerReplicas, Records.sdkVersion());

                Map<String, Object> record;
                if (Profiles.LOAD.containsKey(profile)) {
                    record = Records.load(context, profile, Profiles.LOAD.get(profile), Scenarios.load(client, Profiles.LOAD.get(profile)));
                } else if (Profiles.QUEUE_DRAIN.containsKey(profile)) {
                    Map<String, Object> result = Scenarios.queueDrain(client, Profiles.QUEUE_DRAIN.get(profile));
                    record = Records.queueDrain(context, profile, Profiles.QUEUE_DRAIN.get(profile), result);
                    printQueueDrain(result);
                } else {
                    Map<String, Object> result = Scenarios.sessionDrain(client, channel, Profiles.SESSION_DRAIN.get(profile));
                    record = Records.sessionDrain(context, profile, Profiles.SESSION_DRAIN.get(profile), result);
                    printSessionDrain(result);
                }

                System.out.println("Run:    " + Records.save(options.outDir(), record));
                runIds.add((String) record.get("runId"));
                // Lost/reordered messages or failed operations make the timings meaningless - fail the run (and CI).
                @SuppressWarnings("unchecked")
                Map<String, Object> checks = (Map<String, Object>) record.get("checks");
                if (!(Boolean) checks.get("passed")) {
                    System.out.println("FAILED: " + checks.get("failures"));
                    exitCode = 1;
                }
            }
        } finally {
            channel.shutdownNow().awaitTermination(10, TimeUnit.SECONDS);
            if (server != null) {
                server.close();
            }
        }

        perfReport("report", "--results", options.outDir().toString());
        List<String> check = new ArrayList<>(List.of("check", "--results", options.outDir().toString(), "--runs", String.join(",", runIds),
                "--baseline-branch", options.baselineBranch()));
        if (options.gate()) {
            check.add("--gate");
        }
        boolean regressed = perfReport(check.toArray(String[]::new)) == 3;
        return exitCode == 0 && regressed ? 3 : exitCode;
    }

    @SuppressWarnings("unchecked")
    private static double get(Map<String, Object> map, String... path) {
        Object value = map;
        for (String key : path) {
            value = ((Map<String, Object>) value).get(key);
        }
        return ((Number) value).doubleValue();
    }

    private static void printSessionDrain(Map<String, Object> r) {
        System.out.println();
        System.out.println("=== Session drain ===");
        System.out.printf("Seed:                 %10.1f s%n", get(r, "seedSeconds"));
        System.out.printf("Wall clock:           %10.1f s   (ideal %.0f s, efficiency %.1f%%)%n", get(r, "wallClockSeconds"), get(r, "idealSeconds"), get(r, "efficiency") * 100);
        System.out.printf("First message after:  %10.2f s%n", get(r, "timeToFirstMessageSeconds"));
        System.out.printf("Peak utilisation:     %10.1f%%%n", get(r, "peak", "utilization") * 100);
        System.out.printf("Claim latency ms:     p50 %.0f  p95 %.0f  max %.0f%n", get(r, "claimLatencyMs", "p50"), get(r, "claimLatencyMs", "p95"), get(r, "claimLatencyMs", "max"));
        System.out.printf("Delivery ms:          p50 %.0f  p95 %.0f  max %.0f%n", get(r, "deliveryLatencyMs", "p50"), get(r, "deliveryLatencyMs", "p95"), get(r, "deliveryLatencyMs", "max"));
        System.out.printf("Streams %s (failed claims %s, re-claimed sessions %s)%n", r.get("streams"), r.get("failedClaims"), r.get("sessionsClaimedMoreThanOnce"));
        System.out.printf("Messages %s (duplicates %s, missing %s, FIFO violations %s)%n", r.get("messagesHandled"), r.get("duplicates"), r.get("missing"), r.get("fifoViolations"));
    }

    private static void printQueueDrain(Map<String, Object> r) {
        System.out.println();
        System.out.println("=== Queue drain ===");
        System.out.printf("Publish:              %10.1f s%n", get(r, "seedSeconds"));
        System.out.printf("Wall clock:           %10.1f s%s%n", get(r, "wallClockSeconds"), r.get("idealSeconds") == null ? ""
                : String.format("   (ideal %.1f s, efficiency %.1f%%)", get(r, "idealSeconds"), get(r, "efficiency") * 100));
        System.out.printf("Throughput:           %10.0f msg/s%n", get(r, "messagesPerSecond"));
        System.out.printf("First message after:  %10.2f s%n", get(r, "timeToFirstMessageSeconds"));
        System.out.printf("Peak handlers:        %10s%n", r.get("peakConcurrentHandlers"));
        System.out.printf("Delivery ms:          p50 %.0f  p95 %.0f  max %.0f%n", get(r, "deliveryLatencyMs", "p50"), get(r, "deliveryLatencyMs", "p95"), get(r, "deliveryLatencyMs", "max"));
        System.out.printf("Messages %s (duplicates %s, missing %s, order violations %s)%n", r.get("messagesHandled"), r.get("duplicates"), r.get("missing"), r.get("orderViolations"));
    }
}
