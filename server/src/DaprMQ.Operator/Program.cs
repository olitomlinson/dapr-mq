using DaprMQ.Operator.Scaling;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// DaprMQ.Operator - DaprMQ's control-plane service. v1 hosts the KEDA external scaler that scales
// consumer workloads on queue depth; it reads depth from the workers (see IWorkerDepthClient).

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

builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGrpcService<DaprMQExternalScaler>();
app.MapGrpcHealthChecksService();
app.MapGet("/health", () => new { status = "healthy", service = "daprmq-operator" });

app.Run();
