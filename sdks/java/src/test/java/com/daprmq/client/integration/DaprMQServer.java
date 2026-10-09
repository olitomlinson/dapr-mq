package com.daprmq.client.integration;

import org.testcontainers.containers.BindMode;
import org.testcontainers.containers.GenericContainer;
import org.testcontainers.containers.Network;
import org.testcontainers.containers.output.OutputFrame;
import org.testcontainers.images.builder.Transferable;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Supplier;

/**
 * Starts a throwaway DaprMQ stack with Testcontainers (no docker compose, no pre-existing server),
 * mirroring server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs: Postgres,
 * Dapr placement + scheduler, the DaprMQ API server and a daprd sidecar on one private network with
 * dynamic host ports. {@link DaprTopology#perf(int)} scales it out for the perf harness
 * (sdks/java/src/test/java/com/daprmq/client/perf).
 *
 * <p>Prerequisite: the API image must exist locally - build it with
 * {@code ./build-and-test.sh --skip-tests} from the repo root (default tag {@code daprmq-api:test},
 * override with DAPRMQ_API_IMAGE).
 */
public final class DaprMQServer implements AutoCloseable {
    public static final String API_IMAGE = System.getenv().getOrDefault("DAPRMQ_API_IMAGE", "daprmq-api:test");
    private static final String DAPR_VERSION = "1.18.4";
    private static final String POSTGRES_PASSWORD = "test_password";
    private static final Duration STARTUP_TIMEOUT = Duration.ofSeconds(90);

    /** Buffered stdout/stderr of the containers worth reading when a test fails, keyed by alias. */
    private static final Map<String, StringBuffer> LOGS = new ConcurrentHashMap<>();

    private final List<GenericContainer<?>> containers;
    private final Network network;
    private final String httpUrl;
    private final String grpcAddress;

    private DaprMQServer(List<GenericContainer<?>> containers, Network network, String httpUrl, String grpcAddress) {
        this.containers = containers;
        this.network = network;
        this.httpUrl = httpUrl;
        this.grpcAddress = grpcAddress;
    }

    /**
     * Prints the API server's and sidecar's output. A failing scenario usually says far more from
     * daprd's side (actor placement, lock timeouts) than from the client exception.
     */
    public void dumpLogs() {
        LOGS.forEach((alias, buffer) -> {
            System.out.println("===== " + alias + " =====");
            System.out.println(buffer);
        });
    }

    public String httpUrl() {
        return httpUrl;
    }

    public String grpcAddress() {
        return grpcAddress;
    }

    public static DaprMQServer start() {
        return start(DaprTopology.integration());
    }

    public static boolean apiImageAvailable() {
        return docker("image", "inspect", API_IMAGE);
    }

    public static DaprMQServer start(DaprTopology topology) {
        if (!apiImageAvailable()) {
            throw new IllegalStateException(
                    "Docker image " + API_IMAGE + " not found - run ./build-and-test.sh --skip-tests from the repo root first.");
        }

        String schedulerDir = worldWritableDir("daprmq-scheduler-");
        String blobstoreDir = worldWritableDir("daprmq-blobstore-");
        String componentsDir = componentsDir();
        List<GenericContainer<?>> containers = new ArrayList<>();
        Network network = Network.newNetwork();

        try {
            GenericContainer<?> postgres = container("postgres:16.2-alpine", network, "postgres-db")
                    .withEnv("POSTGRES_DB", "actor_state")
                    .withEnv("POSTGRES_USER", "postgres")
                    .withEnv("POSTGRES_PASSWORD", POSTGRES_PASSWORD);
            start(containers, postgres);
            waitFor("postgres", () -> {
                try {
                    return postgres.execInContainer("pg_isready", "-U", "postgres", "-d", "actor_state").getExitCode() == 0;
                } catch (IOException | InterruptedException e) {
                    return false;
                }
            });

            start(containers, container("daprio/dapr:" + DAPR_VERSION, network, "dapr-placement")
                    .withCommand("./placement", "-port", "50005"));
            // HA members keep etcd in the container (ephemeral); the single scheduler keeps the
            // bind-mounted data dir. Members only reach quorum together, so none is waited on alone.
            String[] schedulerAliases = new String[topology.schedulerReplicas()];
            for (int member = 0; member < topology.schedulerReplicas(); member++) {
                schedulerAliases[member] = topology.schedulerAlias(member);
                GenericContainer<?> scheduler = container("daprio/dapr:" + DAPR_VERSION, network, schedulerAliases[member]);
                start(containers, topology.schedulerHa()
                        ? scheduler.withCommand(topology.schedulerCommand(member))
                        : scheduler
                                .withFileSystemBind(schedulerDir, "/data/dapr-scheduler", BindMode.READ_WRITE)
                                .withCommand("./scheduler", "--port", "50006", "--log-level", "info", "--etcd-data-dir", "/data/dapr-scheduler"));
            }
            sleep(2000); // no health probe for placement/scheduler; same grace period as the .NET fixture
            requireRunning(containers, "dapr-placement");
            requireRunning(containers, schedulerAliases);

            List<GenericContainer<?>> apis = new ArrayList<>();
            for (int replica = 0; replica < topology.apiReplicas(); replica++) {
                String apiAlias = DaprTopology.apiServerAlias(replica);
                String sidecarAlias = DaprTopology.sidecarAlias(replica);
                GenericContainer<?> api = container(API_IMAGE, network, apiAlias)
                        .withExposedPorts(5000, 5001)
                        .withEnv("ASPNETCORE_URLS", "http://+:5000")
                        .withEnv("REGISTER_ACTORS", "true")
                        .withEnv("DAPR_HTTP_ENDPOINT", "http://" + sidecarAlias + ":3500")
                        .withEnv("DAPR_GRPC_ENDPOINT", "http://" + sidecarAlias + ":50001")
                        .withEnv("Logging__LogLevel__Default", "Warning")
                        .withEnv("QUEUE_ACTOR_TYPE_NAME", "QueueActor")
                        .withEnv("HTTP_SINK_ACTOR_TYPE_NAME", "HttpSinkActor");
                GenericContainer<?> sidecar = container("daprio/daprd:" + DAPR_VERSION, network, sidecarAlias)
                        .withFileSystemBind(componentsDir, "/tmp/dapr-components", BindMode.READ_ONLY)
                        .withFileSystemBind(blobstoreDir, "/tmp/blobstore", BindMode.READ_WRITE)
                        .withCommand(
                                "./daprd", "--app-id", "daprmq-api", "--app-channel-address", apiAlias, "--app-port", "5000",
                                "--dapr-http-port", "3500", "--dapr-grpc-port", "50001",
                                "--placement-host-address", "dapr-placement:50005", "--scheduler-host-address", topology.schedulerHostAddress(),
                                "--resources-path", "/tmp/dapr-components", "--config", "/tmp/dapr-components/config.yml",
                                "--log-level", "info");
                if (replica == 0) {
                    // Replica 0 also answers to the historical single-instance aliases.
                    api.withNetworkAliases("api-server");
                    sidecar.withNetworkAliases("dapr-sidecar");
                }
                start(containers, api);
                start(containers, sidecar);
                apis.add(api);
            }

            // Every replica on its own, not just through the load balancer (which answers as soon as one is up).
            HttpClient probeClient = HttpClient.newHttpClient();
            for (GenericContainer<?> api : apis) {
                String url = "http://" + api.getHost() + ":" + api.getMappedPort(5000);
                waitFor("DaprMQ API + sidecar at " + url + " (queue operations servable)", () -> operationsReady(probeClient, url));
            }

            GenericContainer<?> front = apis.get(0);
            if (topology.loadBalanced()) {
                // nginx resolves its upstreams at startup, so it goes last, once every replica's alias exists.
                front = container("nginx:1.27-alpine", network, DaprTopology.LOAD_BALANCER_ALIAS)
                        .withExposedPorts(5000, 5001)
                        .withCopyToContainer(Transferable.of(topology.nginxConfig()), "/etc/nginx/nginx.conf");
                start(containers, front);
                String lbUrl = "http://" + front.getHost() + ":" + front.getMappedPort(5000);
                waitFor("nginx load balancer", () -> operationsReady(probeClient, lbUrl));
            }

            String httpUrl = "http://" + front.getHost() + ":" + front.getMappedPort(5000);
            String grpcAddress = front.getHost() + ":" + front.getMappedPort(5001);
            return new DaprMQServer(containers, network, httpUrl, grpcAddress);
        } catch (RuntimeException e) {
            stopAll(containers, network);
            throw e;
        }
    }

    @Override
    public void close() {
        stopAll(containers, network);
    }

    /** The daprmq.DaprMQ.operations signal over HTTP: queue operations can be served. Writes nothing. */
    private static boolean operationsReady(HttpClient client, String httpUrl) {
        HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(httpUrl + "/health/operations"))
                .timeout(Duration.ofSeconds(5))
                .GET()
                .build();
        try {
            return client.send(request, HttpResponse.BodyHandlers.discarding()).statusCode() == 200;
        } catch (IOException | InterruptedException e) {
            return false; // any failure just means "not ready yet"
        }
    }

    private static GenericContainer<?> container(String image, Network network, String alias) {
        StringBuffer buffer = LOGS.computeIfAbsent(alias, k -> new StringBuffer());
        return new GenericContainer<>(image)
                .withNetwork(network)
                .withNetworkAliases(alias)
                .withStartupTimeout(STARTUP_TIMEOUT)
                // Buffered rather than printed: only a failing test dumps them, via dumpLogs().
                .withLogConsumer((OutputFrame frame) -> buffer.append(frame.getUtf8String()));
    }

    /**
     * Neither placement nor scheduler exposes a port or a health endpoint, so Testcontainers'
     * start() returns as soon as the container is created - a process that dies immediately after
     * goes unnoticed, and its network alias disappears with it. daprd then spends the rest of the
     * run failing to resolve that alias, and the first actor call that needs it hangs until it is
     * cancelled. Fail here instead, with the container's own output.
     */
    private static void requireRunning(List<GenericContainer<?>> containers, String... aliases) {
        for (String alias : aliases) {
            for (GenericContainer<?> container : containers) {
                if (container.getNetworkAliases().contains(alias) && !container.isRunning()) {
                    throw new IllegalStateException(
                            alias + " exited during startup. Its output was:\n" + container.getLogs());
                }
            }
        }
    }

    private static void start(List<GenericContainer<?>> containers, GenericContainer<?> container) {
        container.start();
        containers.add(container);
    }

    private static void stopAll(List<GenericContainer<?>> containers, Network network) {
        List<GenericContainer<?>> reversed = new ArrayList<>(containers);
        Collections.reverse(reversed);
        for (GenericContainer<?> container : reversed) {
            container.stop();
        }
        containers.clear();
        network.close();
    }

    private static boolean docker(String... args) {
        List<String> command = new ArrayList<>();
        command.add("docker");
        Collections.addAll(command, args);
        try {
            Process process = new ProcessBuilder(command)
                    .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                    .redirectError(ProcessBuilder.Redirect.DISCARD)
                    .start();
            return process.waitFor() == 0;
        } catch (IOException e) {
            return false;
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return false;
        }
    }

    /** Walks up from the working directory to the repo root, which owns the shared dapr-components. */
    private static String componentsDir() {
        String relative = "server/tests/DaprMQ.IntegrationTests/dapr-components";
        for (Path dir = Paths.get("").toAbsolutePath(); dir != null; dir = dir.getParent()) {
            Path candidate = dir.resolve(relative);
            if (Files.isDirectory(candidate)) {
                return candidate.toString();
            }
        }
        throw new IllegalStateException("Could not locate " + relative + " above " + Paths.get("").toAbsolutePath());
    }

    /**
     * The scheduler and sidecar containers write here as a non-root user, so the directory has to
     * be world-writable. Permissions are set after creation, not via asFileAttribute: creation-time
     * modes are masked by the process umask (0755 under the usual 022), which is enough on macOS
     * where bind mounts ignore it, but leaves the scheduler unable to open its etcd data dir on
     * Linux - it then exits, taking its network alias with it.
     */
    private static String worldWritableDir(String prefix) {
        try {
            Path path = Files.createTempDirectory(prefix);
            Files.setPosixFilePermissions(path, PosixFilePermissions.fromString("rwxrwxrwx"));
            return path.toString();
        } catch (IOException e) {
            throw new IllegalStateException("Could not create temp dir " + prefix, e);
        }
    }

    private static void waitFor(String description, Supplier<Boolean> probe) {
        long deadline = System.nanoTime() + STARTUP_TIMEOUT.toNanos();
        RuntimeException lastError = null;
        while (System.nanoTime() < deadline) {
            try {
                if (probe.get()) {
                    return;
                }
            } catch (RuntimeException e) {
                lastError = e; // any failure just means "not ready yet"
            }
            sleep(500);
        }
        throw new IllegalStateException(
                description + " not ready after " + STARTUP_TIMEOUT.toSeconds() + "s (last error: " + lastError + ")", lastError);
    }

    private static void sleep(long millis) {
        try {
            Thread.sleep(millis);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException("Interrupted while starting the DaprMQ stack", e);
        }
    }
}
