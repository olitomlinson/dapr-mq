using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;

// Enable HTTP/2 without TLS for gRPC (h2c protocol)
// This is required for gRPC to work over plaintext HTTP in containers
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel with separate endpoints for HTTP/1.1 (REST) and HTTP/2 (gRPC)
// This is required because Kestrel can't negotiate HTTP/2 without TLS on the same port
var httpPort = 5000; // REST HTTP/1.1 endpoint
var grpcPort = 5001; // gRPC HTTP/2 endpoint

// Parse primary port from ASPNETCORE_URLS if set
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? builder.Configuration["ASPNETCORE_URLS"];
if (!string.IsNullOrEmpty(urls) && urls.Contains(':'))
{
    var portStr = urls.Split(':').Last().TrimEnd('/');
    if (int.TryParse(portStr, out var parsedPort))
    {
        httpPort = parsedPort;
        grpcPort = parsedPort + 1; // gRPC on next port
    }
}

builder.WebHost.UseKestrel(options =>
{
    // HTTP/1.1 endpoint for REST APIs
    options.ListenAnyIP(httpPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http1;
    });

    // HTTP/2 endpoint for gRPC (no TLS required when HTTP/2 only)
    options.ListenAnyIP(grpcPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

// Add CORS support for dashboard
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowDashboard", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Add services to the container
builder.Services.AddControllers().AddDapr();

// Add Dapr Client services globally (required for IActorProxyFactory used by gRPC)
builder.Services.AddDaprClient();

// Register Actor Proxy Factory explicitly (needed for DaprActorInvoker)
builder.Services.AddSingleton<Dapr.Actors.Client.IActorProxyFactory, Dapr.Actors.Client.ActorProxyFactory>();

// Add HttpClient services (required by HttpSinkActor)
builder.Services.AddHttpClient();

// Add gRPC services
builder.Services.AddGrpc();

// Add gRPC reflection (allows introspection of services)
builder.Services.AddGrpcReflection();

// Configure actor settings
var actorConfig = new
{
    QueueActorTypeName = builder.Configuration.GetValue("QUEUE_ACTOR_TYPE_NAME", "QueueActor"),
    HttpSinkActorTypeName = builder.Configuration.GetValue("HTTP_SINK_ACTOR_TYPE_NAME", "HttpSinkActor"),
    DaprPubSubSinkActorTypeName = builder.Configuration.GetValue("DAPR_PUBSUB_SINK_ACTOR_TYPE_NAME", "DaprPubSubSinkActor")
};

// builder.Services.AddSingleton(actorConfig);

// Register QueueActorInvoker invoker (dedicated invoker for QueueActorInvoker operations)
builder.Services.AddSingleton<IQueueActorInvoker>(sp =>
    new QueueActorInvoker(
        sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
        actorConfig.QueueActorTypeName));

// Register HttpSinkActor invoker (dedicated invoker for HttpSinkActor operations)
builder.Services.AddSingleton<IHttpSinkActorInvoker>(sp =>
    new HttpSinkActorInvoker(
        sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
        actorConfig.HttpSinkActorTypeName));

// Register DaprPubSubSinkActor invoker (dedicated invoker for DaprPubSubSinkActor operations)
builder.Services.AddSingleton<IDaprPubSubSinkActorInvoker>(sp =>
    new DaprPubSubSinkActorInvoker(
        sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
        actorConfig.DaprPubSubSinkActorTypeName));

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DaprMQ API (.NET)", Version = "v1" });
});

// Register actors (conditionally based on environment variable)
var registerActors = builder.Configuration.GetValue<bool>("REGISTER_ACTORS", true);
if (registerActors)
{
    builder.Services.AddActors(options =>
    {
        options.Actors.RegisterActor<DaprMQ.QueueActor>(actorConfig.QueueActorTypeName);
        options.Actors.RegisterActor<DaprMQ.HttpSinkActor>(actorConfig.HttpSinkActorTypeName);
        options.Actors.RegisterActor<DaprMQ.DaprPubSubSinkActor>(actorConfig.DaprPubSubSinkActorTypeName);

        // Configure actor runtime settings
        options.ActorIdleTimeout = TimeSpan.FromSeconds(60);
        options.JsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Use camelCase instead of PascalCase
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // Skip null properties
            WriteIndented = false // Set to true only for debugging
        };
    });
}
// No else needed - Dapr Client (from AddDapr) is sufficient for actor invocation

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors("AllowDashboard");
app.UseAuthorization();

// Map controllers
app.MapControllers();

// Map gRPC service
app.MapGrpcService<DaprMQGrpcService>();

// Map gRPC reflection service (enables service introspection)
app.MapGrpcReflectionService();

// Map Dapr actor endpoints (only needed when hosting actors)
if (registerActors)
{
    app.MapActorsHandlers();
}

// Health check endpoint
app.MapGet("/health", () => new { status = "healthy", service = "daprmq-api-dotnet" });

app.Run();
