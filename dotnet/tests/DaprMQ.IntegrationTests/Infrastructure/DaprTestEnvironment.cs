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
    private IContainer? _daprPlacementContainer;
    private IContainer? _daprSidecarContainer;
    private IContainer? _apiServerContainer;

    // Exposed endpoints and connection strings
    public string PostgresConnectionString { get; private set; } = string.Empty;
    public string DaprHttpEndpoint { get; private set; } = string.Empty;
    public string DaprGrpcEndpoint { get; private set; } = string.Empty;
    public string ApiServerUrl { get; private set; } = string.Empty;
    public string ApiServerGrpcUrl { get; private set; } = string.Empty;

    // HTTP clients for testing
    public HttpClient ApiClient { get; private set; } = null!;
    public HttpClient DaprSidecarClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Check if container logs should be redirected to console
        var enableContainerLogs = Environment.GetEnvironmentVariable("ENABLE_CONTAINER_LOGS")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

        // Create a custom Docker network for all containers
        _network = new NetworkBuilder()
            .WithName($"daprmq-test-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();

        // 1. Start PostgreSQL first (required by state store)
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16.2-alpine")
            .WithDatabase("actor_state")
            .WithUsername("postgres")
            .WithPassword("test_password")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres-db")
            .Build();

        await _postgresContainer.StartAsync();
        PostgresConnectionString = _postgresContainer.GetConnectionString();

        // 2. Start Dapr placement service
        _daprPlacementContainer = new ContainerBuilder()
            .WithImage("daprio/dapr:1.17.0")
            .WithNetwork(_network)
            .WithNetworkAliases("dapr-placement")
            .WithCommand("./placement", "-port", "50005")
            .WithPortBinding(50005, true)  // Use dynamic port binding on host
            .Build();

        await _daprPlacementContainer.StartAsync();

        // Wait a bit for placement to be ready
        await Task.Delay(TimeSpan.FromSeconds(2));

        // 3. Start API server container WITHOUT wait strategy (will be ready after Dapr starts)
        var apiServerBuilder = new ContainerBuilder()
            .WithImage("daprmq-api:test")
            .WithNetwork(_network)
            .WithNetworkAliases("api-server")
            .WithPortBinding(5000, true) // HTTP/1.1 REST endpoint
            .WithPortBinding(5001, true) // HTTP/2 gRPC endpoint
            .WithEnvironment("ASPNETCORE_URLS", "http://+:5000")
            .WithEnvironment("REGISTER_ACTORS", "true")
            // Tell the API server where to find Dapr sidecar on the Docker network using FULL endpoint URLs
            .WithEnvironment("DAPR_HTTP_ENDPOINT", "http://dapr-sidecar:3500")
            .WithEnvironment("DAPR_GRPC_ENDPOINT", "http://dapr-sidecar:50001")
            // Configure soft-delete retention for testing (5 seconds gives tests time to validate queued segments)
            .WithEnvironment("SEGMENT_DELETION_RETENTION_SECONDS", "5")
            // Configure cleanup scan interval for fast testing (2 seconds instead of default 60)
            .WithEnvironment("SEGMENT_CLEANUP_SCAN_INTERVAL_SECONDS", "2")
            // Configure logging for integration tests
            .WithEnvironment("Logging__LogLevel__Default", "Warning")
            .WithEnvironment("Logging__LogLevel__DaprMQ", "Debug")
            .WithEnvironment("Logging__LogLevel__DaprMQ.ApiServer", "Debug")
            .WithEnvironment("Logging__LogLevel__Microsoft.AspNetCore", "Warning")
            // Allow optional override of actor type name via environment variable
            .WithEnvironment("ACTOR_TYPE_NAME", Environment.GetEnvironmentVariable("ACTOR_TYPE_NAME") ?? "QueueActor");

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

        // 4. Start Dapr sidecar (connects to API server via Docker network)
        // Mount the components directory from project root (3 levels up from bin/Debug/net10.0)
        var testProjectRoot = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..");
        var componentsPath = Path.GetFullPath(Path.Combine(testProjectRoot, "dapr-components"));

        var daprSidecarBuilder = new ContainerBuilder()
            .WithImage("daprio/daprd:1.17.0")
            .WithNetwork(_network)
            .WithNetworkAliases("dapr-sidecar")
            .WithCommand("./daprd",
                "--app-id", "daprmq-api",
                "--app-channel-address", "api-server",  // Connect to API server via Docker network
                "--app-port", "5000",
                "--dapr-http-port", "3500",
                "--dapr-grpc-port", "50001",
                "--placement-host-address", "dapr-placement:50005",
                "--resources-path", "/tmp/dapr-components",
                "--log-level", "info")  // Enable debug logging for Dapr
            .WithBindMount(componentsPath, "/tmp/dapr-components")
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
        }

        if (_daprPlacementContainer != null)
        {
            await _daprPlacementContainer.DisposeAsync();
        }

        if (_postgresContainer != null)
        {
            await _postgresContainer.DisposeAsync();
        }

        if (_network != null)
        {
            await _network.DeleteAsync();
        }
    }
}
