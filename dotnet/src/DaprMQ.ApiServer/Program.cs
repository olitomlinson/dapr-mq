using System.Text.Json;
using System.Text.Json.Serialization;
using Dapr.Actors.Runtime;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using DaprMQ.ApiServer.Configuration;
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

// Add services to the container
builder.Services.AddControllers().AddDapr();

// Add Dapr Client services globally (required for IActorProxyFactory used by gRPC)
builder.Services.AddDaprClient();

// Register Actor Proxy Factory explicitly (needed for DaprActorInvoker)
builder.Services.AddSingleton<Dapr.Actors.Client.IActorProxyFactory, Dapr.Actors.Client.ActorProxyFactory>();

// Add gRPC services
builder.Services.AddGrpc();

// Add gRPC reflection (allows introspection of services)
builder.Services.AddGrpcReflection();

// Configure actor settings
var actorConfig = new ActorConfiguration
{
    ActorTypeName = builder.Configuration.GetValue("ACTOR_TYPE_NAME", "QueueActor")
};
builder.Services.AddSingleton(actorConfig);

// Register actor invoker abstraction (used by both API server and actors for actor invocation)
builder.Services.AddSingleton<IActorInvoker>(sp =>
    new DaprActorInvoker(
        sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
        actorConfig.ActorTypeName));

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DaprMQ API (.NET)", Version = "v1" });
});

// Register actor configuration (for actor's internal use - DLQ actor type name)
builder.Services.AddSingleton(new DaprMQ.Configuration.ActorConfiguration
{
    ActorTypeName = actorConfig.ActorTypeName
});

// Register actors (conditionally based on environment variable)
var registerActors = builder.Configuration.GetValue<bool>("REGISTER_ACTORS", true);
if (registerActors)
{
    builder.Services.AddActors(options =>
    {
        options.Actors.RegisterActor<DaprMQ.QueueActor>(actorConfig.ActorTypeName);

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
