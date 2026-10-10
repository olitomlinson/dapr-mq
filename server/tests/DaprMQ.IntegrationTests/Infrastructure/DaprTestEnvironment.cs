using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;

namespace DaprMQ.IntegrationTests.Infrastructure;

/// <summary>
/// Complete Dapr test environment with TestContainers
/// Spins up PostgreSQL, Dapr placement/scheduler, Dapr sidecar, and API server
///
/// To enable container logs output to console, set the environment variable:
/// ENABLE_CONTAINER_LOGS=true dotnet test
///
/// DAPRMQ_TEST_TOPOLOGY=split puts the API behind a gateway in front of a separate actor-hosting
/// worker, as Helm deploys it, instead of one process doing both (see <see cref="DaprTopology"/>).
/// </summary>
public class DaprTestEnvironment : IAsyncLifetime
{
    private INetwork? _network;
    private PostgreSqlContainer? _postgresContainer;
    private IContainer? _wireMockContainer;
    private IContainer? _daprPlacementContainer;
    private readonly List<IContainer> _daprSchedulerContainers = [];
    private readonly List<IContainer> _daprSidecarContainers = [];
    private readonly List<IContainer> _apiServerContainers = [];
    private IContainer? _loadBalancerContainer;
    private IContainer? _operatorContainer;
    private IContainer? _operatorSidecarContainer;
    private string _componentsPath = string.Empty;

    // Containers hosting the actors (the workers when split), for tests that take them down.
    private readonly List<(IContainer App, IContainer Sidecar)> _actorHosts = [];

    private const string WorkerAppId = "daprmq-api";
    private const string GatewayAppId = "daprmq-gateway";

    // Exposed endpoints and connection strings
    public string PostgresConnectionString { get; private set; } = string.Empty;
    public string WireMockUrl { get; private set; } = string.Empty;
    public string WireMockInternalUrl { get; private set; } = string.Empty;
    public string DaprHttpEndpoint { get; private set; } = string.Empty;
    public string DaprGrpcEndpoint { get; private set; } = string.Empty;
    public string ApiServerUrl { get; private set; } = string.Empty;
    public string ApiServerGrpcUrl { get; private set; } = string.Empty;

    public DaprTopology Topology { get; private set; } = DaprTopology.Default;

    // HTTP clients for testing
    public HttpClient ApiClient { get; private set; } = null!;
    public HttpClient DaprSidecarClient { get; private set; } = null!;

    /// <summary>
    /// Host-side directory bind-mounted into the Dapr sidecar's bindings.localstorage rootPath.
    /// Tests can inspect this directory to verify blob upload/deletion.
    /// </summary>
    public string BlobStoreDirectory => _blobStoreTestDirectory;

    private string _schedulerTestDirectory;
    private string _blobStoreTestDirectory;


    public Task InitializeAsync() => InitializeAsync(null, null, false);

    /// <summary>
    /// <paramref name="extraApiServerEnvironment"/> lets a dedicated fixture override API server
    /// settings (e.g. ACTOR_IDLE_TIMEOUT_SECONDS) that the shared fixture leaves at production
    /// defaults - used by tests that need to force an actor cold deterministically.
    /// <paramref name="apiServerImage"/> lets a dedicated fixture point at a differently-built
    /// image (e.g. built against a different local Dapr.Actors SDK version) instead of the
    /// default "daprmq-api:test" - used to A/B compare SDK builds against the same scenario.
    /// </summary>
    public Task InitializeAsync(IReadOnlyDictionary<string, string>? extraApiServerEnvironment) =>
        InitializeAsync(extraApiServerEnvironment, null, false);

    /// <summary>
    /// <paramref name="enableQueryInstrumentation"/> turns on log_statement=all on the Postgres
    /// container - real per-query overhead (formatting and logging every statement), so it
    /// defaults to off and must be opted into explicitly by callers that read GetPostgresLogsAsync
    /// (currently just the perf harness's state-reads benchmark). Leaving it off for the shared
    /// "Dapr Collection" fixture and ReentrancyTestFixture keeps the rest of the suite - including
    /// high-throughput tests like BulkOperations_10000Messages - unaffected.
    /// </summary>
    public Task InitializeAsync(IReadOnlyDictionary<string, string>? extraApiServerEnvironment, string? apiServerImage) =>
        InitializeAsync(extraApiServerEnvironment, apiServerImage, false);

    public Task InitializeAsync(IReadOnlyDictionary<string, string>? extraApiServerEnvironment, string? apiServerImage, bool enableQueryInstrumentation) =>
        InitializeAsync(extraApiServerEnvironment, apiServerImage, enableQueryInstrumentation, DaprTopology.FromEnvironment());

    /// <summary>
    /// <paramref name="topology"/> scales the stack out for the perf harness: N API replicas (each
    /// with its own sidecar) behind an nginx load balancer, and a scheduler HA cluster.
    /// <see cref="ApiServerUrl"/>/<see cref="ApiServerGrpcUrl"/> then point at the load balancer.
    /// When split, the API replicas are gateways and the actors live on separate workers;
    /// <paramref name="extraApiServerEnvironment"/> applies to both.
    /// </summary>
    public async Task InitializeAsync(IReadOnlyDictionary<string, string>? extraApiServerEnvironment, string? apiServerImage, bool enableQueryInstrumentation, DaprTopology topology)
    {
        Topology = topology;
        // Check if container logs should be redirected to console
        var enableContainerLogs = Environment.GetEnvironmentVariable("ENABLE_CONTAINER_LOGS")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

        // Create a custom Docker network for all containers
        _network = new NetworkBuilder()
            .WithName($"daprmq-test-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();

        // 1. Start PostgreSQL first (required by state store)
        // log_statement=all logs every statement with its bound parameters, so a caller can
        // count exactly which actor state keys hit Postgres (the perf harness's state-reads
        // benchmark). It adds real per-query overhead, so it's opt-in via
        // enableQueryInstrumentation, not on by default for the shared fixture or high-throughput
        // tests that don't need it.
        var postgresBuilder = new PostgreSqlBuilder()
            .WithImage("public.ecr.aws/docker/library/postgres:16.2-alpine")
            .WithDatabase("actor_state")
            .WithUsername("postgres")
            .WithPassword("test_password")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres-db");

        if (enableQueryInstrumentation)
        {
            postgresBuilder = postgresBuilder.WithCommand(
                "-c", "log_statement=all",
                "-c", "log_line_prefix=%m [%p] ");
        }

        _postgresContainer = postgresBuilder.Build();

        await _postgresContainer.StartAsync();
        PostgresConnectionString = _postgresContainer.GetConnectionString();

        // 2. Start WireMock server for HTTP sink testing
        const string wireMockNetworkAlias = "wiremock-server";
        const int wireMockInternalPort = 8080;

        _wireMockContainer = new ContainerBuilder()
            .WithImage("mirror.gcr.io/wiremock/wiremock:3.3.1")
            .WithNetwork(_network)
            .WithNetworkAliases(wireMockNetworkAlias)
            .WithPortBinding(wireMockInternalPort, true)  // Use dynamic port binding on host
            .Build();

        await _wireMockContainer.StartAsync();

        var wireMockPort = _wireMockContainer.GetMappedPublicPort(wireMockInternalPort);
        WireMockUrl = $"http://localhost:{wireMockPort}";
        WireMockInternalUrl = $"http://{wireMockNetworkAlias}:{wireMockInternalPort}";

        // 3. Start Dapr placement service
        _daprPlacementContainer = new ContainerBuilder()
            .WithImage("ghcr.io/dapr/dapr:1.18.4")
            .WithNetwork(_network)
            .WithNetworkAliases("dapr-placement")
            .WithCommand("./placement", "-port", "50005")
            .WithPortBinding(50005, true)  // Use dynamic port binding on host
            .Build();

        await _daprPlacementContainer.StartAsync();

        // Wait a bit for placement to be ready
        await Task.Delay(TimeSpan.FromSeconds(2));

        const string schedulerContainerDataDir = "/data/dapr-scheduler";
        _schedulerTestDirectory = TestDirectoryManager.CreateTestDirectory("scheduler");
        _blobStoreTestDirectory = TestDirectoryManager.CreateTestDirectory("blobstore");
        // 4. Start Dapr scheduler service (one member, or an HA cluster of topology.SchedulerReplicas)
        for (var member = 0; member < topology.SchedulerReplicas; member++)
        {
            var schedulerBuilder = new ContainerBuilder()
                .WithImage("ghcr.io/dapr/dapr:1.18.4")
                .WithNetwork(_network)
                .WithNetworkAliases(topology.SchedulerAlias(member))
                .WithPortBinding(DaprTopology.SchedulerPort, true);

            // HA members keep etcd in the container (ephemeral); the single scheduler keeps its
            // historical bind-mounted data dir.
            schedulerBuilder = topology.SchedulerHa
                ? schedulerBuilder.WithCommand(topology.SchedulerCommand(member))
                : schedulerBuilder
                    .WithBindMount(_schedulerTestDirectory, schedulerContainerDataDir, AccessMode.ReadWrite)
                    .WithCommand("./scheduler", "--port", "50006", "--log-level", "info", "--etcd-data-dir", schedulerContainerDataDir);

            var scheduler = schedulerBuilder.Build();
            _daprSchedulerContainers.Add(scheduler);
        }

        // HA members only report healthy once they reach quorum, so start them all before waiting.
        await Task.WhenAll(_daprSchedulerContainers.Select(c => c.StartAsync()));

        // Wait a bit for scheduler to be ready
        await Task.Delay(TimeSpan.FromSeconds(2));

        // 5./6. API server replicas (and, when split, workers), each with its own Dapr sidecar
        var testProjectRoot = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..");
        var componentsPath = Path.GetFullPath(Path.Combine(testProjectRoot, "dapr-components"));
        _componentsPath = componentsPath;

        var apiRole = topology.IsSplit ? ReplicaRole.Gateway : ReplicaRole.Combined;
        var replicas = await Task.WhenAll(
            Enumerable.Range(0, topology.ApiReplicas).Select(replica =>
                StartReplicaAsync(apiRole, replica, topology, apiServerImage, extraApiServerEnvironment, componentsPath, enableContainerLogs))
            .Concat(Enumerable.Range(0, topology.Workers).Select(worker =>
                StartReplicaAsync(ReplicaRole.Worker, worker, topology, apiServerImage, extraApiServerEnvironment, componentsPath, enableContainerLogs))));

        var (api, sidecar) = replicas[0];
        ApiServerUrl = $"http://localhost:{api.GetMappedPublicPort(5000)}";
        ApiServerGrpcUrl = $"http://localhost:{api.GetMappedPublicPort(5001)}";

        DaprHttpEndpoint = $"http://localhost:{sidecar.GetMappedPublicPort(3500)}";
        DaprGrpcEndpoint = $"http://localhost:{sidecar.GetMappedPublicPort(50001)}";

        // Wait until every replica (gateways and workers) reports its own readiness
        await WaitForReadyAsync(TimeSpan.FromMinutes(2));

        // 7. Load balancer in front of the replicas. nginx resolves its upstreams at startup, so it
        // goes last, once every replica's alias exists.
        if (topology.LoadBalanced)
        {
            _loadBalancerContainer = new ContainerBuilder()
                .WithImage("public.ecr.aws/docker/library/nginx:1.27-alpine")
                .WithNetwork(_network)
                .WithNetworkAliases(DaprTopology.LoadBalancerAlias)
                .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(topology.NginxConfig()), "/etc/nginx/nginx.conf")
                .WithPortBinding(5000, true)
                .WithPortBinding(5001, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5000).UntilInternalTcpPortIsAvailable(5001))
                .Build();

            await _loadBalancerContainer.StartAsync();

            ApiServerUrl = $"http://localhost:{_loadBalancerContainer.GetMappedPublicPort(5000)}";
            ApiServerGrpcUrl = $"http://localhost:{_loadBalancerContainer.GetMappedPublicPort(5001)}";
        }

        // Initialize HTTP clients
        ApiClient = new HttpClient { BaseAddress = new Uri(ApiServerUrl), Timeout = TimeSpan.FromMinutes(5) };
        DaprSidecarClient = new HttpClient { BaseAddress = new Uri(DaprHttpEndpoint) };
    }

    private enum ReplicaRole
    {
        /// <summary>Serves the API and hosts the actors (the single-process default).</summary>
        Combined,

        /// <summary>Serves the API only (REGISTER_ACTORS=false), as the Helm gateway.</summary>
        Gateway,

        /// <summary>Hosts the actors only (ENABLE_API=false), as the Helm worker.</summary>
        Worker,
    }

    /// <summary>Starts one app container and its sidecar.</summary>
    private async Task<(IContainer App, IContainer Sidecar)> StartReplicaAsync(ReplicaRole role, int replica, DaprTopology topology,
        string? apiServerImage, IReadOnlyDictionary<string, string>? extraApiServerEnvironment, string componentsPath, bool enableContainerLogs)
    {
        // API replica 0 also answers to the historical single-instance aliases.
        string[] apiAliases = role == ReplicaRole.Worker ? [DaprTopology.WorkerAlias(replica)]
            : replica == 0 ? [DaprTopology.ApiServerAlias(replica), "api-server"] : [DaprTopology.ApiServerAlias(replica)];
        string[] sidecarAliases = role == ReplicaRole.Worker ? [DaprTopology.WorkerSidecarAlias(replica)]
            : replica == 0 ? [DaprTopology.SidecarAlias(replica), "dapr-sidecar"] : [DaprTopology.SidecarAlias(replica)];
        var sidecarHost = sidecarAliases[0];

        // API server starts WITHOUT a wait strategy (it's ready only after its sidecar starts)
        var apiServerBuilder = new ContainerBuilder()
            .WithImage(apiServerImage ?? "daprmq-api:test")
            .WithNetwork(_network)
            .WithNetworkAliases(apiAliases)
            .WithPortBinding(5000, true) // HTTP/1.1 REST endpoint
            .WithPortBinding(5001, true) // HTTP/2 gRPC endpoint
            .WithEnvironment("ASPNETCORE_URLS", "http://+:5000")
            .WithEnvironment("REGISTER_ACTORS", role == ReplicaRole.Gateway ? "false" : "true")
            .WithEnvironment("ENABLE_API", role == ReplicaRole.Worker ? "false" : "true")
            // Gateways find the workers for the daprmq.DaprMQ.operations signal by app-id.
            .WithEnvironment("WORKER_APP_ID", WorkerAppId)
            // Tell the API server where to find its own Dapr sidecar on the Docker network using FULL endpoint URLs
            .WithEnvironment("DAPR_HTTP_ENDPOINT", $"http://{sidecarHost}:3500")
            .WithEnvironment("DAPR_GRPC_ENDPOINT", $"http://{sidecarHost}:50001")
            // Configure logging for integration tests
            .WithEnvironment("Logging__LogLevel__Default", "Warning")
            .WithEnvironment("Logging__LogLevel__DaprMQ", "Debug")
            .WithEnvironment("Logging__LogLevel__DaprMQ.ApiServer", "Debug")
            .WithEnvironment("Logging__LogLevel__Microsoft.AspNetCore", "Warning")
            // Allow optional override of actor type name via environment variable
            .WithEnvironment("QUEUE_ACTOR_TYPE_NAME", Environment.GetEnvironmentVariable("QUEUE_ACTOR_TYPE_NAME") ?? "QueueActor")
            .WithEnvironment("HTTP_SINK_ACTOR_TYPE_NAME", Environment.GetEnvironmentVariable("HTTP_SINK_ACTOR_TYPE_NAME") ?? "HttpSinkActor")
            // Short reap TTLs so LargeObjectTests can observe deletion within a reasonable test timeout
            .WithEnvironment("DAPRMQ_BLOB_REAP_BACKSTOP_SECONDS", "30")
            .WithEnvironment("DAPRMQ_BLOB_REAP_POST_DOWNLOAD_SECONDS", "30");

        if (extraApiServerEnvironment != null)
        {
            foreach (var (key, value) in extraApiServerEnvironment)
            {
                apiServerBuilder = apiServerBuilder.WithEnvironment(key, value);
            }
        }

        // Conditionally redirect container logs to console
        if (enableContainerLogs)
        {
            apiServerBuilder = apiServerBuilder.WithOutputConsumer(Consume.RedirectStdoutAndStderrToConsole());
        }

        var apiServer = apiServerBuilder.Build();
        lock (_apiServerContainers)
        {
            _apiServerContainers.Add(apiServer);
        }

        await apiServer.StartAsync();

        // Give API server a moment to start listening
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Dapr sidecar (connects to its API server via Docker network). Mounts the components
        // directory from the project root (3 levels up from bin/Debug/net10.0).
        var daprSidecarBuilder = new ContainerBuilder()
            .WithImage("ghcr.io/dapr/daprd:1.18.4")
            .WithNetwork(_network)
            .WithNetworkAliases(sidecarAliases)
            .WithCommand("./daprd",
                // Actor state is keyed by the hosting app-id, so actor hosts keep "daprmq-api".
                "--app-id", role == ReplicaRole.Gateway ? GatewayAppId : WorkerAppId,
                "--app-channel-address", apiAliases[0],  // Connect to API server via Docker network
                "--app-port", "5000",
                "--dapr-http-port", "3500",
                "--dapr-grpc-port", "50001",
                "--placement-host-address", "dapr-placement:50005",
                "--scheduler-host-address", topology.SchedulerHostAddress,
                "--resources-path", "/tmp/dapr-components",
                "--config", "/tmp/dapr-components/config.yml",
                "--log-level", "info")  // Enable debug logging for Dapr
            .WithBindMount(componentsPath, "/tmp/dapr-components")
            .WithBindMount(_blobStoreTestDirectory, "/tmp/blobstore")
            .WithPortBinding(3500, true)
            .WithPortBinding(50001, true);

        // Conditionally redirect container logs to console
        if (enableContainerLogs)
        {
            daprSidecarBuilder = daprSidecarBuilder.WithOutputConsumer(Consume.RedirectStdoutAndStderrToConsole());
        }

        var sidecar = daprSidecarBuilder.Build();
        lock (_daprSidecarContainers)
        {
            _daprSidecarContainers.Add(sidecar);
            if (role != ReplicaRole.Gateway)
            {
                _actorHosts.Add((apiServer, sidecar));
            }
        }

        await sidecar.StartAsync();
        return (apiServer, sidecar);
    }

    /// <summary>
    /// Polls every replica's own /health/ready (not the load balancer, which would answer as soon
    /// as any one replica is up). Actor hosts are ready once they host QueueActor, gateways once
    /// their sidecar is connected to placement.
    /// </summary>
    public async Task WaitForReadyAsync(TimeSpan timeout)
    {
        using var probe = new HttpClient();
        var deadline = DateTime.UtcNow + timeout;
        foreach (var app in _apiServerContainers)
        {
            var readyUrl = $"http://localhost:{app.GetMappedPublicPort(5000)}/health/ready";
            string lastResult = "no response";
            while (true)
            {
                try
                {
                    using var response = await probe.GetAsync(readyUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }
                    lastResult = $"HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
                }
                catch (HttpRequestException ex)
                {
                    lastResult = ex.Message;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Replica did not report ready at {readyUrl} within {timeout} (last: {lastResult}).\n" +
                        await DescribeStartupAsync(app));
                }

                await Task.Delay(250);
            }
        }
    }

    /// <summary>
    /// Full raw Postgres log for this container's lifetime so far (log_statement=all, so every
    /// statement including bound parameter values, not just normalized query shapes), which
    /// preserves the actual key values queried (Dapr's postgresql/v2 store keys are
    /// "{appId}||{actorType}||{actorId}||{stateName}", visible in each query's "DETAIL:
    /// parameters: $1 = '...'" line), so a caller can group by actor type/id/state name to see
    /// exactly what each query touched, not just how many ran.
    ///
    /// Shells out to the docker CLI directly rather than using IContainer.GetLogsAsync -
    /// verified that API only returns an early, incomplete snapshot of this container's log
    /// (cuts off right after the initial Postgres startup restart, before any app traffic),
    /// while `docker logs <id>` returns the complete stream reliably.
    ///
    /// Callers should scope by content, not time: everything before the app's actual traffic is
    /// Postgres/Dapr schema-migration and metadata-table bookkeeping (INSERT/SELECT against
    /// dapr_metadata, pg_catalog, information_schema), and every real actor-state query's key
    /// contains "||", so filtering lines containing the container's app-id prefix (e.g.
    /// "daprmq-api||") isolates real traffic cleanly.
    /// </summary>
    public Task<string> GetPostgresLogsAsync() => DockerAsync($"logs {_postgresContainer!.Id}");

    /// <summary>Full log of the first API replica (the gateway when split); see <see cref="GetPostgresLogsAsync"/>.</summary>
    public Task<string> GetApiServerLogsAsync() => DockerAsync($"logs {_apiServerContainers[0].Id}");

    /// <summary>Freezes every actor-hosting app (its sidecar keeps running): a hung worker.</summary>
    public async Task PauseWorkerAppsAsync()
    {
        foreach (var (app, _) in _actorHosts)
        {
            await app.PauseAsync();
        }
    }

    public async Task UnpauseWorkerAppsAsync()
    {
        foreach (var (app, _) in _actorHosts)
        {
            await app.UnpauseAsync();
        }
    }

    /// <summary>SIGKILLs every actor-hosting app (or its sidecar): a crash, not a graceful stop.</summary>
    public async Task KillWorkersAsync(bool sidecar)
    {
        foreach (var (app, side) in _actorHosts)
        {
            await DockerAsync($"kill {(sidecar ? side : app).Id}");
        }
    }

    public Task StopPlacementAsync() => _daprPlacementContainer!.StopAsync();

    public Task StartPlacementAsync() => _daprPlacementContainer!.StartAsync();

    /// <summary>
    /// What a replica that never became ready was doing: its own log and every sidecar's (whose
    /// app it belongs to isn't tracked, and there are only a few), plus each sidecar's actor
    /// runtime view, which is what readiness is decided on.
    /// </summary>
    private async Task<string> DescribeStartupAsync(IContainer stuckApp)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"--- app {stuckApp.Name} (tail) ---");
        report.AppendLine(await DockerAsync($"logs --tail 60 {stuckApp.Id}"));

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var sidecar in _daprSidecarContainers)
        {
            report.AppendLine($"--- sidecar {sidecar.Name} (tail) ---");
            report.AppendLine(await DockerAsync($"logs --tail 60 {sidecar.Id}"));
            try
            {
                var metadata = await client.GetStringAsync($"http://localhost:{sidecar.GetMappedPublicPort(3500)}/v1.0/metadata");
                report.AppendLine($"metadata: {metadata}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"metadata: unavailable ({ex.Message})");
            }
        }

        return report.ToString();
    }

    private static async Task<string> DockerAsync(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Most images (Postgres included) log to stderr, not stdout.
        return (await stdoutTask) + (await stderrTask);
    }

    /// <summary>
    /// Whether an actor state key is actually persisted in the state store right now - reads the
    /// Postgres row directly, bypassing any Dapr.Actors in-process tracker cache. Dapr's
    /// postgresql/v2 actor keys are "{appId}||{actorType}||{actorId}||{stateName}".
    /// </summary>
    public async Task<bool> ActorStateExistsAsync(string actorType, string actorId, string stateName)
    {
        var key = $"daprmq-api||{actorType}||{actorId}||{stateName}".Replace("'", "''");
        var result = await _postgresContainer!.ExecAsync(new[]
        {
            "psql", "-U", "postgres", "-d", "actor_state", "-tAc",
            $"SELECT COUNT(*) FROM daprmq_state WHERE key = '{key}';"
        });

        return long.Parse(result.Stdout.Trim()) > 0;
    }

    /// <summary>
    /// Starts DaprMQ.Operator (KEDA external scaler) with its own Dapr sidecar under a *different*
    /// app-id from the API server - the production split, where the operator can't read actor state
    /// itself and must go through the workers' internal depth read. Returns the operator's gRPC URL.
    /// </summary>
    public async Task<string> StartOperatorAsync(string image = "daprmq-operator:test")
    {
        const string operatorAlias = "daprmq-operator";
        const string operatorSidecarAlias = "daprmq-operator-sidecar";

        _operatorContainer = new ContainerBuilder()
            .WithImage(image)
            .WithNetwork(_network)
            .WithNetworkAliases(operatorAlias)
            .WithPortBinding(5000, true)
            .WithPortBinding(5001, true)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:5000")
            .WithEnvironment("WORKER_APP_ID", "daprmq-api")
            .WithEnvironment("DAPR_HTTP_ENDPOINT", $"http://{operatorSidecarAlias}:3500")
            .WithEnvironment("DAPR_GRPC_ENDPOINT", $"http://{operatorSidecarAlias}:50001")
            // No caching, so a test sees each enqueue/dequeue immediately
            .WithEnvironment("DEPTH_CACHE_TTL_MS", "0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(5000).ForPath("/health")))
            .Build();
        await _operatorContainer.StartAsync();

        _operatorSidecarContainer = new ContainerBuilder()
            .WithImage("ghcr.io/dapr/daprd:1.18.4")
            .WithNetwork(_network)
            .WithNetworkAliases(operatorSidecarAlias)
            .WithCommand("./daprd",
                "--app-id", "daprmq-operator",
                "--dapr-http-port", "3500",
                "--dapr-grpc-port", "50001",
                "--placement-host-address", "dapr-placement:50005",
                "--scheduler-host-address", Topology.SchedulerHostAddress,
                "--resources-path", "/tmp/dapr-components",
                "--config", "/tmp/dapr-components/config.yml",
                "--log-level", "info")
            .WithBindMount(_componentsPath, "/tmp/dapr-components")
            .WithBindMount(_blobStoreTestDirectory, "/tmp/blobstore")
            .WithPortBinding(3500, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3500).ForPath("/v1.0/healthz/outbound").ForStatusCode(System.Net.HttpStatusCode.NoContent)))
            .Build();
        await _operatorSidecarContainer.StartAsync();

        return $"http://localhost:{_operatorContainer.GetMappedPublicPort(5001)}";
    }

    /// <summary>Stops every actor host (app and sidecar), leaving placement and any gateway up.</summary>
    public async Task StopWorkersAsync()
    {
        foreach (var (app, sidecar) in _actorHosts)
        {
            await sidecar.StopAsync();
            await app.StopAsync();
        }
    }

    /// <summary>Restarts the actor hosts stopped by <see cref="StopWorkersAsync"/>; host ports are re-mapped.</summary>
    public async Task StartWorkersAsync()
    {
        foreach (var (app, sidecar) in _actorHosts)
        {
            await app.StartAsync();
            await sidecar.StartAsync();
        }
    }

    public async Task DisposeAsync()
    {
        ApiClient?.Dispose();
        DaprSidecarClient?.Dispose();

        if (_operatorSidecarContainer != null)
        {
            await _operatorSidecarContainer.DisposeAsync();
        }

        if (_operatorContainer != null)
        {
            await _operatorContainer.DisposeAsync();
        }

        if (_loadBalancerContainer != null)
        {
            await _loadBalancerContainer.DisposeAsync();
        }

        foreach (var apiServer in _apiServerContainers)
        {
            await apiServer.DisposeAsync();
        }

        foreach (var sidecar in _daprSidecarContainers)
        {
            await sidecar.DisposeAsync();
        }

        TestDirectoryManager.CleanUpDirectory(_blobStoreTestDirectory);

        if (_daprPlacementContainer != null)
        {
            await _daprPlacementContainer.DisposeAsync();
        }

        foreach (var scheduler in _daprSchedulerContainers)
        {
            await scheduler.DisposeAsync();
        }

        TestDirectoryManager.CleanUpDirectory(_schedulerTestDirectory);

        if (_postgresContainer != null)
        {
            await _postgresContainer.DisposeAsync();
        }

        if (_wireMockContainer != null)
        {
            await _wireMockContainer.DisposeAsync();
        }

        if (_network != null)
        {
            await _network.DeleteAsync();
        }
    }
}
