# Quick Start Guide

Get up and running with Dapr QueueActor in 5 minutes.

## Prerequisites

- **Docker & Docker Compose** (for Docker mode)
- **.NET 10.0+** (for library mode)
- **Dapr CLI** (optional, for local development)

## Quick Start with Docker (Recommended)

The fastest way to try QueueActor is using Docker Compose.

### 1. Clone the Repository

```bash
git clone https://github.com/olitomlinson/dapr-mq.git
cd dapr-mq
```

### 2. Start Services

```bash
docker-compose up
```

This starts:
- PostgreSQL database (state store)
- Dapr placement service
- API server with Dapr sidecar

Wait for all services to be healthy (~30 seconds).

### 3. Test the API

Open a new terminal and run the example script:

```bash
chmod +x examples/curl_examples.sh
./examples/curl_examples.sh
```

Or manually test with curl:

```bash
# Push an item
curl -X POST http://localhost:8000/queue/my-queue/push \
  -H "Content-Type: application/json" \
  -d '{"item": {"task": "hello", "priority": "high"}}'

# Pop items
curl -X POST "http://localhost:8000/queue/my-queue/pop"
```

## Quick Start as Library

Use QueueActor in your existing .NET/Dapr application.

### 1. Install Package

```bash
cd dotnet
dotnet add package DaprMQ.Interfaces
dotnet add package Dapr.Actors
```

### 2. Register Actor

In your ASP.NET Core app with Dapr:

```csharp
using Dapr.Actors.AspNetCore;
using DaprMQ;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddActors(options =>
{
    options.Actors.RegisterActor<QueueActor>();
});

var app = builder.Build();
app.MapActorsHandlers();
app.Run();
```

### 3. Use the Actor

There are two ways to invoke actors: **remoting** (interface-based, type-safe) and **nonremoting** (method strings, decoupled).

#### Option A: Remoting (Recommended for C# apps)

```csharp
using Dapr.Actors;
using Dapr.Actors.Client;
using DaprMQ.Interfaces;

// Create proxy with interface
var proxy = ActorProxy.Create<IQueueActor>(
    new ActorId("my-queue"),
    "QueueActor"
);

// Push items (type-safe)
await proxy.Push(new PushRequest
{
    ItemJson = "{\"task\": \"send_email\"}",
    Priority = 0
});

// Pop items (type-safe)
var result = await proxy.Pop();
foreach (var itemJson in result.ItemsJson)
{
    // Process item
}
```

#### Option B: Nonremoting (Recommended for API servers / cross-language)

```csharp
using Dapr.Actors;
using Dapr.Actors.Client;
using DaprMQ.Interfaces;  // Only for request/response models

// Create proxy without interface
var proxy = ActorProxy.Create(new ActorId("my-queue"), "QueueActor");

// Push items
var pushResult = await proxy.InvokeMethodAsync<PushRequest, PushResponse>(
    "Push",
    new PushRequest
    {
        ItemJson = "{\"task\": \"send_email\"}",
        Priority = 0
    }
);

// Pop items
var popResult = await proxy.InvokeMethodAsync<PopResponse>("Pop");
foreach (var itemJson in popResult.ItemsJson)
{
    // Process item
}
```

## Quick Start with Example API Server

Run the included API server example.

### 1. Build the API Server

```bash
cd dotnet/src/DaprMQ.ApiServer
dotnet build
```

### 2. Start Dapr Services

You need PostgreSQL and Dapr placement service. Use Docker Compose:

```bash
docker-compose up postgres-db placement
```

### 3. Run API Server

```bash
cd dotnet/src/DaprMQ.ApiServer
dapr run \
  --app-id daprmq-service \
  --app-port 5000 \
  --resources-path ../../../dapr/components \
  --config ../../../dapr/config/config.yml \
  -- dotnet run
```

### 4. Test API

```bash
curl http://localhost:8000/health
```

## Verify Installation

Run the test suite to verify everything works:

```bash
cd dotnet
dotnet test
```

## Next Steps

- Read [ARCHITECTURE.md](ARCHITECTURE.md) to understand how it works
- See [API_REFERENCE.md](API_REFERENCE.md) for complete API documentation
- Check [examples/](../examples/) for more usage patterns
- Explore configuration options in `dapr/components/` and `dapr/config/`

## Troubleshooting

### Docker Compose Issues

**Services not starting:**
```bash
# Check service status
docker-compose ps

# View logs
docker-compose logs api-server
docker-compose logs api-server-dapr
```

**Port conflicts:**
Edit `docker-compose.yml` and change port mappings:
```yaml
ports:
  - "8001:8000"  # Changed from 8000:8000
```

### Library Installation Issues

**Dapr SDK not found:**
```bash
# Install latest Dapr SDK
dotnet add package Dapr.Actors
dotnet add package Dapr.AspNetCore
```

**Build errors:**
```bash
# Restore NuGet packages
dotnet restore
```

### Actor Registration Issues

**Actor not found:**
- Ensure actor is registered before creating proxy
- Check Dapr logs for registration errors
- Verify Dapr sidecar is running

**State not persisting:**
- Check state store configuration in `dapr/components/`
- Verify database connection string
- Check Dapr component logs

## Common Patterns

### Multiple Queues

Each actor ID is a separate queue:

```bash
# Queue 1
curl -X POST http://localhost:8000/queue/queue-1/push -d '{"item": {"data": 1}}'

# Queue 2
curl -X POST http://localhost:8000/queue/queue-2/push -d '{"item": {"data": 2}}'

# They're independent
curl -X POST "http://localhost:8000/queue/queue-1/pop0"  # Returns item 1 only
```

### Batch Processing

Pop multiple items at once:

```bash
# Pop up to 50 items
curl -X POST "http://localhost:8000/queue/batch-queue/pop"
```

### Task Queue Pattern

```csharp
// Producer: Push tasks
foreach (var task in tasks)
{
    await proxy.Push(new PushRequest
    {
        ItemJson = JsonSerializer.Serialize(new { task_id = task.Id, data = task.Data }),
        Priority = 0
    });
}

// Consumer: Pop and process
while (true)
{
    var result = await proxy.Pop();
    if (result.ItemsJson.Any())
    {
        foreach (var itemJson in result.ItemsJson)
        {
            // Process item
            var item = JsonSerializer.Deserialize<dynamic>(itemJson);
            await ProcessTaskAsync(item);
        }
    }
    else
    {
        await Task.Delay(1000); // Wait for more tasks
    }
}
```

## Getting Help

- Check [README.md](../README.md) for overview
- Read [ARCHITECTURE.md](ARCHITECTURE.md) for design details
- Browse [examples/](../examples/) for code samples
- Open an issue on GitHub for bugs
