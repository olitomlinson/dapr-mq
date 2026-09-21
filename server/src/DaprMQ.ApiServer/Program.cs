using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using DaprMQ.ApiServer.Services;
using DaprMQ.Interfaces;
using Grpc.Net.Client;

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
builder.Services.AddControllers().AddDapr(builder => builder.UseGrpcChannelOptions(new GrpcChannelOptions()
{
    MaxReceiveMessageSize = 16 * 1024 * 1024,
    MaxSendMessageSize = 16 * 1024 * 1024
}));

// Add Dapr Client services globally (required for IActorProxyFactory used by gRPC)
builder.Services.AddDaprClient(dapr =>
{
    dapr.UseGrpcChannelOptions(new GrpcChannelOptions
    {
        MaxReceiveMessageSize = 16 * 1024 * 1024,
        MaxSendMessageSize = 16 * 1024 * 1024
    });
});

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
    BlobReaperActorTypeName = builder.Configuration.GetValue("BLOB_REAPER_ACTOR_TYPE_NAME", "BlobReaperActor"),
    TopicActorTypeName = builder.Configuration.GetValue("TOPIC_ACTOR_TYPE_NAME", "TopicActor"),
    SessionCoordinatorActorTypeName = builder.Configuration.GetValue("SESSION_COORDINATOR_ACTOR_TYPE_NAME", "SessionCoordinatorActor")
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

// Register BlobReaperActor invoker (dedicated invoker for BlobReaperActor operations, used by QueueActor)
builder.Services.AddSingleton<IBlobReaperActorInvoker>(sp =>
    new BlobReaperActorInvoker(
        sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
        actorConfig.BlobReaperActorTypeName));

// Register TopicActor invoker (dedicated invoker for TopicActor operations)
builder.Services.AddSingleton<ITopicActorInvoker>(sp =>
    new TopicActorInvoker(
        sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
        actorConfig.TopicActorTypeName));

// Register SessionCoordinatorActor invoker (dedicated invoker for SessionCoordinatorActor
// operations, used by QueueActor to self-register a session actor into its directory)
builder.Services.AddSingleton<ISessionCoordinatorActorInvoker>(sp =>
    new SessionCoordinatorActorInvoker(
        sp.GetRequiredService<Dapr.Actors.Client.IActorProxyFactory>(),
        actorConfig.SessionCoordinatorActorTypeName));

// Register QueueActorStateReader (used by SessionCoordinatorActor's directory sweep to check a
// session's item count via Dapr's actor-state data-plane API directly, bypassing actor
// activation - see QueueActorStateReader for why that matters for a sweep that specifically
// targets idle, likely-cold candidates)
var daprHttpEndpoint = builder.Configuration.GetValue("DAPR_HTTP_ENDPOINT", "http://localhost:3500") ?? "http://localhost:3500";
builder.Services.AddSingleton<DaprMQ.IQueueActorStateReader>(sp =>
    new DaprMQ.QueueActorStateReader(
        sp.GetRequiredService<IHttpClientFactory>(),
        actorConfig.QueueActorTypeName,
        daprHttpEndpoint));

// Register TopicActor tunables (global defaults - see TopicActorConfig)
builder.Services.AddSingleton(new TopicActorConfig());

// Register object store used to offload large item payloads (backed by a Dapr output binding)
var objectStoreConfig = new ObjectStoreConfig
{
    GlobalPrefix = builder.Configuration.GetValue("DAPRMQ_BLOB_GLOBAL_PREFIX", string.Empty) ?? string.Empty,
    BindingName = builder.Configuration.GetValue("DAPRMQ_BLOB_BINDING_NAME", "daprmq-blobstore") ?? "daprmq-blobstore"
};
builder.Services.AddSingleton(objectStoreConfig);
builder.Services.AddSingleton<IObjectStore, DaprBindingObjectStore>();

// Register ObjectClaimToken issuer, used in place of exposing raw BlobReferences to end users.
// Signing key defaults to a dev-only value - deployments must override DAPRMQ_OBJECT_CLAIM_SIGNING_KEY.
var objectClaimTokenSigningKey = builder.Configuration.GetValue("DAPRMQ_OBJECT_CLAIM_SIGNING_KEY", "daprmq-dev-only-object-claim-signing-key") ?? "daprmq-dev-only-object-claim-signing-key";
var objectClaimTokenConfig = new ObjectClaimTokenConfig
{
    SigningKey = System.Text.Encoding.UTF8.GetBytes(objectClaimTokenSigningKey),
    TokenTtl = TimeSpan.FromSeconds(builder.Configuration.GetValue("DAPRMQ_OBJECT_CLAIM_TOKEN_TTL_SECONDS", 86400))
};
builder.Services.AddSingleton(objectClaimTokenConfig);
builder.Services.AddSingleton<IObjectClaimTokenIssuer, ObjectClaimTokenIssuer>();

// Register the two-TTL blob reap schedule (backstop at item finalization, extension on download)
var blobReapConfig = new BlobReapConfig
{
    BackstopSeconds = builder.Configuration.GetValue("DAPRMQ_BLOB_REAP_BACKSTOP_SECONDS", 86400),
    PostDownloadSeconds = builder.Configuration.GetValue("DAPRMQ_BLOB_REAP_POST_DOWNLOAD_SECONDS", 86400)
};
builder.Services.AddSingleton(blobReapConfig);

// Register the idempotency-key dedup TTL (single global default, no per-request override)
var idempotencyConfig = new IdempotencyConfig
{
    TtlSeconds = builder.Configuration.GetValue("IDEMPOTENCY_KEY_TTL_SECONDS", 86400),
    UnloadAfterCommit = builder.Configuration.GetValue("IDEMPOTENCY_KEY_UNLOAD_AFTER_COMMIT", true)
};
builder.Services.AddSingleton(idempotencyConfig);

// Add Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DaprMQ API (.NET)", Version = "v1" });
});

// Controls whether this instance exposes the public REST/gRPC API surface.
// Disabled on actor-hosting instances so only the gateway serves external requests.
var enableApi = builder.Configuration.GetValue<bool>("ENABLE_API", true);

// Register actors (conditionally based on environment variable)
var registerActors = builder.Configuration.GetValue<bool>("REGISTER_ACTORS", true);
if (registerActors)
{
    builder.Services.AddActors(options =>
    {
        options.Actors.RegisterActor<DaprMQ.QueueActor>(actorConfig.QueueActorTypeName);
        options.Actors.RegisterActor<DaprMQ.HttpSinkActor>(actorConfig.HttpSinkActorTypeName);
        options.Actors.RegisterActor<DaprMQ.BlobReaperActor>(actorConfig.BlobReaperActorTypeName);
        options.Actors.RegisterActor<DaprMQ.TopicActor>(actorConfig.TopicActorTypeName);
        options.Actors.RegisterActor<DaprMQ.SessionCoordinatorActor>(actorConfig.SessionCoordinatorActorTypeName);

        // Configure actor runtime settings
        options.ActorIdleTimeout = TimeSpan.FromSeconds(60);

        // KNOWN UNFIXED HAZARD: session actors now self-register with their
        // SessionCoordinatorActor on every activation (not just once, ever), so a
        // pruned-but-still-relevant session self-heals. That can trigger the coordinator calling
        // back into itself mid-turn (SessionCoordinatorActor -> QueueActor -> SessionCoordinatorActor)
        // whenever SetSessionLease/ClearSessionLease target a currently-cold session actor - an
        // A -> B -> A call chain that non-reentrant actors deadlock on. Dapr's actor reentrancy
        // (options.ReentrancyConfig) is the documented fix for exactly this shape, but enabling it
        // was verified (on both Dapr 1.18.2 and 1.18.4) to break TopicActor's reminder-driven
        // publish relay entirely - the relay reminder silently never fires once reentrancy is on,
        // no error logged, consistent with the actor lock not being released after a plain
        // (non-reentrant-in-practice) call returns. Not usable as a fix here. The deadlock itself
        // remains unfixed pending a different approach (e.g. making the self-registration call
        // fire-and-forget instead of awaited).
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

// Map the public REST/gRPC API surface (only needed on instances serving external requests)
if (enableApi)
{
    app.MapControllers();
    app.MapGrpcService<DaprMQGrpcService>();
    app.MapGrpcReflectionService();
}

// Map Dapr actor endpoints (only needed when hosting actors)
if (registerActors)
{
    app.MapActorsHandlers();
}

// Health check endpoint
app.MapGet("/health", () => new { status = "healthy", service = "daprmq-api-dotnet" });

app.Run();
