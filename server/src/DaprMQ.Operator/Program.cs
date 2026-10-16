using DaprMQ.Operator.Console;
using DaprMQ.Operator.Scaling;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// DaprMQ.Operator - DaprMQ's control-plane service. v1 hosts the KEDA external scaler that scales
// consumer workloads on queue depth; it reads depth from the workers (see IWorkerDepthClient).
// The scaler reports the ScaledObjects KEDA asks about to the workers' scaling registry, which is
// where the console's scaling page finds them.

var builder = WebApplication.CreateBuilder(args);

// Same port layout as the API server: HTTP/1.1 (health) on the ASPNETCORE_URLS port, gRPC h2c
// (what KEDA's scalerAddress points at) on port+1.
var httpPort = 5000;
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? builder.Configuration["ASPNETCORE_URLS"];
if (!string.IsNullOrEmpty(urls) && int.TryParse(urls.Split(':').Last().TrimEnd('/'), out var parsedPort))
{
    httpPort = parsedPort;
}

builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(httpPort, o => o.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(httpPort + 1, o => o.Protocols = HttpProtocols.Http2);
});

var workerAppId = builder.Configuration.GetValue<string>("WORKER_APP_ID");
if (string.IsNullOrWhiteSpace(workerAppId))
{
    throw new InvalidOperationException("WORKER_APP_ID is required: the Dapr app-id of the DaprMQ workers to read queue depth from");
}

builder.Services.AddDaprClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IWorkerDepthClient>(sp => new CachingWorkerDepthClient(
    new DaprWorkerDepthClient(sp.GetRequiredService<Dapr.Client.DaprClient>(), workerAppId),
    TimeSpan.FromMilliseconds(builder.Configuration.GetValue("DEPTH_CACHE_TTL_MS", 1000)),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(new ScalerOptions
{
    StreamPollInterval = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("STREAM_POLL_INTERVAL_MS", 1000))
});

// Off on a console-only Deployment (Helm console.separateDeployment), so console load can never
// delay KEDA's GetMetrics.
var scalerEnabled = builder.Configuration.GetValue("SCALER_ENABLED", true);

builder.Services.AddSingleton<IScalingRegistryClient>(sp => new DaprScalingRegistryClient(
    sp.GetRequiredService<Dapr.Client.DaprClient>(), workerAppId));
builder.Services.AddSingleton(new ScaledObjectReporterOptions
{
    FlushInterval = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("SCALER_REGISTRATION_FLUSH_MS", 10_000))
});
builder.Services.AddSingleton<ScaledObjectReporter>();
builder.Services.AddSingleton<IScaledObjectObserver>(sp => sp.GetRequiredService<ScaledObjectReporter>());
if (scalerEnabled)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ScaledObjectReporter>());
}

// The read-only admin console: /api/console/* plus the SPA from wwwroot, on the HTTP port. Off by
// default, and with it off the operator is unchanged. No auth in v1 - anyone who can reach the
// Service can read message payloads (docs/CONSOLE.md).
var consoleEnabled = builder.Configuration.GetValue("CONSOLE_ENABLED", false);
if (consoleEnabled)
{
    builder.Services.AddSingleton<IWorkerAdminClient>(sp => new ConcurrencyLimitedWorkerAdminClient(
        new DaprWorkerAdminClient(sp.GetRequiredService<Dapr.Client.DaprClient>(), workerAppId),
        builder.Configuration.GetValue("CONSOLE_MAX_WORKER_CALLS", 8)));
    builder.Services.AddSingleton(sp => new ConsoleResponseCache(
        TimeSpan.FromMilliseconds(builder.Configuration.GetValue("CONSOLE_CACHE_TTL_MS", 10_000)),
        sp.GetRequiredService<TimeProvider>()));

    // KEDA history for the scaling page. Replica counts need Kubernetes read access (Helm
    // console.kubernetesRead); without it, the page shows what the scaler's metric asks for.
    if (builder.Configuration.GetValue("KUBERNETES_READ", false))
    {
        builder.Services.AddSingleton<IScalingStatusReader>(new KubernetesScalingStatusReader(
            new k8s.Kubernetes(k8s.KubernetesClientConfiguration.InClusterConfig())));
    }

    builder.Services.AddSingleton(new ScalingSamplerOptions
    {
        Interval = TimeSpan.FromMilliseconds(builder.Configuration.GetValue("SCALING_SAMPLE_INTERVAL_MS", 15_000)),
        History = TimeSpan.FromMinutes(builder.Configuration.GetValue("SCALING_HISTORY_MINUTES", 60))
    });
    builder.Services.AddSingleton(sp => new ScalingSampler(
        sp.GetRequiredService<IScalingRegistryClient>(),
        sp.GetRequiredService<IWorkerAdminClient>(),
        sp.GetService<IScalingStatusReader>(),
        sp.GetRequiredService<ScalingSamplerOptions>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<ScalingSampler>>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ScalingSampler>());
}

builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (scalerEnabled)
{
    app.MapGrpcService<DaprMQExternalScaler>();
}
app.MapGrpcHealthChecksService();
app.MapGet("/health", () => new { status = "healthy", service = "daprmq-operator" });

if (consoleEnabled)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapConsoleEndpoints();
    // Client-side routes (/queues/orders) load the SPA.
    app.MapFallbackToFile("index.html");
}

app.Run();
