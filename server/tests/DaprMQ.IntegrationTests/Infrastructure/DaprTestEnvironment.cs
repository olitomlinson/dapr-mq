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
/// </summary>
public class DaprTestEnvironment : IAsyncLifetime
{
    private INetwork? _network;
    private PostgreSqlContainer? _postgresContainer;
    private IContainer? _wireMockContainer;
    private IContainer? _daprPlacementContainer;
    private IContainer? _daprSchedulerContainer;
    private IContainer? _daprSidecarContainer;
    private IContainer? _apiServerContainer;

    // Exposed endpoints and connection strings
    public string PostgresConnectionString { get; private set; } = string.Empty;
    public string WireMockUrl { get; private set; } = string.Empty;
    public string WireMockInternalUrl { get; private set; } = string.Empty;
    public string DaprHttpEndpoint { get; private set; } = string.Empty;
    public string DaprGrpcEndpoint { get; private set; } = string.Empty;
    public string ApiServerUrl { get; private set; } = string.Empty;
    public string ApiServerGrpcUrl { get; private set; } = string.Empty;

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
    /// <paramref name="enableQueryInstrumentation"/> turns on pg_stat_statements and
    /// log_statement=all on the Postgres container - real per-query overhead (formatting and
    /// logging every statement, plus a tracking extension), so it defaults to off and must be
    /// opted into explicitly by tests that actually read GetActorStateSelectCallCountAsync/
    /// GetActorStateSelectBreakdownAsync/GetPostgresLogsAsync (currently just
    /// QueryCountComparisonTest). Leaving it off for the shared "Dapr Collection" fixture and
    /// ReentrancyTestFixture keeps the rest of the suite - including high-throughput tests like
    /// BulkOperations_10000Messages - unaffected.
    /// </summary>
    public Task InitializeAsync(IReadOnlyDictionary<string, string>? extraApiServerEnvironment, string? apiServerImage) =>
        InitializeAsync(extraApiServerEnvironment, apiServerImage, false);

    public async Task InitializeAsync(IReadOnlyDictionary<string, string>? extraApiServerEnvironment, string? apiServerImage, bool enableQueryInstrumentation)
    {
        // Check if container logs should be redirected to console
        var enableContainerLogs = Environment.GetEnvironmentVariable("ENABLE_CONTAINER_LOGS")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

        // Create a custom Docker network for all containers
        _network = new NetworkBuilder()
            .WithName($"daprmq-test-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();

        // 1. Start PostgreSQL first (required by state store)
        // shared_preload_libraries/log_statement=all enable pg_stat_statements and full
        // per-statement logging so a test can measure exactly how many times (and which) queries
        // actually hit Postgres (e.g. comparing Dapr.Actors SDK builds' default-tracker
        // round-trip behavior - see QueryCountComparisonTest). Both add real per-query overhead
        // (log_statement=all especially - formatting and logging every statement), so this is
        // opt-in via enableQueryInstrumentation, not on by default for the shared fixture or
        // high-throughput tests that don't need it.
        var postgresBuilder = new PostgreSqlBuilder()
            .WithImage("postgres:16.2-alpine")
            .WithDatabase("actor_state")
            .WithUsername("postgres")
            .WithPassword("test_password")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres-db");

        if (enableQueryInstrumentation)
        {
            postgresBuilder = postgresBuilder.WithCommand(
                "-c", "shared_preload_libraries=pg_stat_statements",
                "-c", "pg_stat_statements.track=all",
                // Full statement + bound-parameter logging, so a test can dump every query
                // (with actual key values, not just aggregate counts) for direct comparison -
                // see GetPostgresLogsAsync.
                "-c", "log_statement=all",
                "-c", "log_line_prefix=%m [%p] ");
        }

        _postgresContainer = postgresBuilder.Build();

        await _postgresContainer.StartAsync();
        PostgresConnectionString = _postgresContainer.GetConnectionString();

        if (enableQueryInstrumentation)
        {
            await _postgresContainer.ExecAsync(new[]
            {
                "psql", "-U", "postgres", "-d", "actor_state",
                "-c", "CREATE EXTENSION IF NOT EXISTS pg_stat_statements;"
            });
        }

        // 2. Start WireMock server for HTTP sink testing
        const string wireMockNetworkAlias = "wiremock-server";
        const int wireMockInternalPort = 8080;

        _wireMockContainer = new ContainerBuilder()
            .WithImage("wiremock/wiremock:3.3.1")
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
            .WithImage("daprio/dapr:1.18.4")
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
        // 4. Start Dapr scheduler service
        _daprSchedulerContainer = new ContainerBuilder()
            .WithImage("daprio/dapr:1.18.4")
            .WithNetwork(_network)
            .WithNetworkAliases("dapr-scheduler")
            .WithBindMount(_schedulerTestDirectory, schedulerContainerDataDir, AccessMode.ReadWrite)
            .WithCommand("./scheduler", "--port", "50006", "--log-level", "info", "--etcd-data-dir", schedulerContainerDataDir)
            .WithPortBinding(50006, true)
            .Build();

        await _daprSchedulerContainer.StartAsync();

        // Wait a bit for scheduler to be ready
        await Task.Delay(TimeSpan.FromSeconds(2));

        // 5. Start API server container WITHOUT wait strategy (will be ready after Dapr starts)
        var apiServerBuilder = new ContainerBuilder()
            .WithImage(apiServerImage ?? "daprmq-api:test")
            .WithNetwork(_network)
            .WithNetworkAliases("api-server")
            .WithPortBinding(5000, true) // HTTP/1.1 REST endpoint
            .WithPortBinding(5001, true) // HTTP/2 gRPC endpoint
            .WithEnvironment("ASPNETCORE_URLS", "http://+:5000")
            .WithEnvironment("REGISTER_ACTORS", "true")
            // Tell the API server where to find Dapr sidecar on the Docker network using FULL endpoint URLs
            .WithEnvironment("DAPR_HTTP_ENDPOINT", "http://dapr-sidecar:3500")
            .WithEnvironment("DAPR_GRPC_ENDPOINT", "http://dapr-sidecar:50001")
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

        _apiServerContainer = apiServerBuilder.Build();

        await _apiServerContainer.StartAsync();

        var apiPort = _apiServerContainer.GetMappedPublicPort(5000);
        var grpcPort = _apiServerContainer.GetMappedPublicPort(5001);
        ApiServerUrl = $"http://localhost:{apiPort}";
        ApiServerGrpcUrl = $"http://localhost:{grpcPort}";

        // Give API server a moment to start listening
        await Task.Delay(TimeSpan.FromSeconds(2));

        // 6. Start Dapr sidecar (connects to API server via Docker network)
        // Mount the components directory from project root (3 levels up from bin/Debug/net10.0)
        var testProjectRoot = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..");
        var componentsPath = Path.GetFullPath(Path.Combine(testProjectRoot, "dapr-components"));

        var daprSidecarBuilder = new ContainerBuilder()
            .WithImage("daprio/daprd:1.18.4")
            .WithNetwork(_network)
            .WithNetworkAliases("dapr-sidecar")
            .WithCommand("./daprd",
                "--app-id", "daprmq-api",
                "--app-channel-address", "api-server",  // Connect to API server via Docker network
                "--app-port", "5000",
                "--dapr-http-port", "3500",
                "--dapr-grpc-port", "50001",
                "--placement-host-address", "dapr-placement:50005",
                "--scheduler-host-address", "dapr-scheduler:50006",
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

        _daprSidecarContainer = daprSidecarBuilder.Build();

        await _daprSidecarContainer.StartAsync();

        // Get exposed Dapr sidecar ports
        var daprHttpPort = _daprSidecarContainer.GetMappedPublicPort(3500);
        var daprGrpcPort = _daprSidecarContainer.GetMappedPublicPort(50001);
        DaprHttpEndpoint = $"http://localhost:{daprHttpPort}";
        DaprGrpcEndpoint = $"http://localhost:{daprGrpcPort}";

        // Wait for everything to stabilize - give Dapr time to connect to placement and register actors
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Initialize HTTP clients
        ApiClient = new HttpClient { BaseAddress = new Uri(ApiServerUrl) };
        DaprSidecarClient = new HttpClient { BaseAddress = new Uri(DaprHttpEndpoint) };
    }

    /// <summary>
    /// Resets pg_stat_statements counters to zero, so a subsequent
    /// GetActorStateSelectCallCountAsync call measures only queries issued after this point.
    /// </summary>
    public async Task ResetQueryStatsAsync()
    {
        await _postgresContainer!.ExecAsync(new[]
        {
            "psql", "-U", "postgres", "-d", "actor_state",
            "-c", "SELECT pg_stat_statements_reset();"
        });
    }

    /// <summary>
    /// Total number of times Postgres has executed a SELECT against the actor state table since
    /// the last ResetQueryStatsAsync call - i.e. how many times the Dapr.Actors SDK (via daprd)
    /// actually round-tripped to the state store to read actor state, as opposed to serving a
    /// value from its own in-process tracker cache. Dapr's postgresql/v2 state store keeps
    /// everything in one table (daprmq_state here, per the configured tablePrefix), so this
    /// isn't diluted by unrelated tables.
    /// </summary>
    public async Task<long> GetActorStateSelectCallCountAsync()
    {
        var result = await _postgresContainer!.ExecAsync(new[]
        {
            "psql", "-U", "postgres", "-d", "actor_state", "-tAc",
            "SELECT COALESCE(SUM(calls), 0) FROM pg_stat_statements WHERE query ILIKE 'SELECT%daprmq_state%';"
        });

        return long.Parse(result.Stdout.Trim());
    }

    /// <summary>
    /// Diagnostic breakdown of GetActorStateSelectCallCountAsync's total: each distinct
    /// normalized query pg_stat_statements has recorded against the actor state table, with its
    /// own call count, most-called first. Useful for explaining where a measured total actually
    /// comes from rather than just how big it is.
    /// </summary>
    public async Task<string> GetActorStateSelectBreakdownAsync()
    {
        var result = await _postgresContainer!.ExecAsync(new[]
        {
            "psql", "-U", "postgres", "-d", "actor_state", "-tA", "-F", " | calls=",
            "-c",
            "SELECT query, calls FROM pg_stat_statements WHERE query ILIKE 'SELECT%daprmq_state%' ORDER BY calls DESC;"
        });

        return result.Stdout;
    }

    /// <summary>
    /// Full raw Postgres log for this container's lifetime so far (log_statement=all, so every
    /// statement including bound parameter values, not just normalized query shapes) - unlike
    /// GetActorStateSelectBreakdownAsync/GetActorStateSelectCallCountAsync (pg_stat_statements),
    /// this preserves the actual key values queried (Dapr's postgresql/v2 store keys are
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
    public async Task<string> GetPostgresLogsAsync()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", $"logs {_postgresContainer!.Id}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Postgres (like most well-behaved container images) logs to stderr, not stdout.
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

    public async Task DisposeAsync()
    {
        ApiClient?.Dispose();
        DaprSidecarClient?.Dispose();

        if (_apiServerContainer != null)
        {
            await _apiServerContainer.DisposeAsync();
        }

        if (_daprSidecarContainer != null)
        {
            await _daprSidecarContainer.DisposeAsync();
            TestDirectoryManager.CleanUpDirectory(_blobStoreTestDirectory);
        }

        if (_daprPlacementContainer != null)
        {
            await _daprPlacementContainer.DisposeAsync();
        }

        if (_daprSchedulerContainer != null)
        {
            TestDirectoryManager.CleanUpDirectory(_schedulerTestDirectory);
            await _daprSchedulerContainer.DisposeAsync();
        }

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
