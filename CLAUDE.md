# Claude Context: DaprMQ

## Project Overview

**daprmq** is a C# library providing a simple FIFO queue-based Dapr actor for storing and retrieving JSON-serialized data. It's designed for lightweight message queuing, task scheduling, and ordered processing in Dapr applications.

- **Package Name**: `DaprMQ` (NuGet)
- **C# Namespaces**: `DaprMQ`, `DaprMQ.Interfaces`, `DaprMQ.ApiServer`
- **GitHub**: https://github.com/olitomlinson/dapr-mq
- **.NET**: 10.0+
- **Dapr**: 1.17.0+

## Project Structure

```
.
├── dotnet/
│   ├── src/
│   │   ├── DaprMQ/                    # Main actor implementation
│   │   │   ├── QueueActor.cs              # Core actor class
│   │   │   └── QueueActor.csproj          # Project file
│   │   ├── DaprMQ.Interfaces/         # Actor interfaces and models
│   │   │   ├── IQueueActor.cs             # Actor interface definition
│   │   │   ├── Models.cs                    # Request/Response models
│   │   │   └── DaprMQ.Interfaces.csproj
│   │   └── DaprMQ.ApiServer/          # ASP.NET Core API server
│   │       ├── Program.cs                   # API server entry point
│   │       ├── Controllers/QueueController.cs # REST API endpoints
│   │       └── DaprMQ.ApiServer.csproj
│   └── tests/
│       └── DaprMQ.Tests/              # Unit tests
│           ├── QueueActorTests.cs         # xUnit tests
│           └── DaprMQ.Tests.csproj
├── docs/
│   ├── QUICKSTART.md                        # Getting started guide
│   ├── ARCHITECTURE.md                      # Design details
│   └── API_REFERENCE.md                     # Complete API docs
├── dapr/
│   ├── components/                          # Dapr component configs (state store)
│   └── config/                              # Dapr runtime config
└── docker-compose.yml                       # Full stack (PostgreSQL, Dapr, API)

```

## Core API

### Actor Interface

```csharp
using DaprMQ.Interfaces;

// Main methods:
Task<PushResponse> Push(PushRequest request);              // Add to end (FIFO)
Task<PopResponse> Pop();                                   // Remove single item from front
Task<PopWithAckResponse> PopWithAck(PopWithAckRequest);    // Pop with acknowledgement (creates lock)
Task<AcknowledgeResponse> Acknowledge(AcknowledgeRequest); // Acknowledge and remove locked items
Task<ExtendLockResponse> ExtendLock(ExtendLockRequest);    // Extend existing lock TTL
```

### Usage Modes

1. **Library Mode**: Import and register actor in existing Dapr apps
2. **API Server Mode**: Run example REST API (`daprmq-server`)
3. **Docker Mode**: Full stack with PostgreSQL + Dapr placement

## Key Files

- **[dotnet/src/DaprMQ/QueueActor.cs](dotnet/src/DaprMQ/QueueActor.cs)**: Core `DaprMQ` implementation
- **[dotnet/src/DaprMQ.Interfaces/IQueueActor.cs](dotnet/src/DaprMQ.Interfaces/IQueueActor.cs)**: Actor interface definition
- **[dotnet/src/DaprMQ.Interfaces/Models.cs](dotnet/src/DaprMQ.Interfaces/Models.cs)**: Request/Response models
- **[dotnet/src/DaprMQ.ApiServer/Program.cs](dotnet/src/DaprMQ.ApiServer/Program.cs)**: ASP.NET Core API server (HTTP + gRPC)
- **[dotnet/src/DaprMQ.ApiServer/Controllers/QueueController.cs](dotnet/src/DaprMQ.ApiServer/Controllers/QueueController.cs)**: REST API endpoints
- **[dotnet/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs](dotnet/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)**: gRPC service implementation
- **[dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto](dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto)**: gRPC service definition
- **[dotnet/tests/DaprMQ.Tests/QueueActorTests.cs](dotnet/tests/DaprMQ.Tests/QueueActorTests.cs)**: Actor unit tests

## Technology Stack

- **Dapr SDK**: `Dapr.Actors`, `Dapr.AspNetCore`, `Dapr.Client`
- **Web Framework**: ASP.NET Core 10.0 (HTTP REST + gRPC)
- **gRPC**: `Grpc.AspNetCore` 2.70.0, Protocol Buffers
- **Testing**: xUnit, Moq
- **Language**: C# 13 with .NET 10.0

## Common Commands

```bash
# Build
cd dotnet
dotnet build                                # Build all projects
dotnet build src/DaprMQ              # Build actor library

# Test
dotnet test                                 # Run all tests
dotnet test --logger "console;verbosity=detailed"  # Verbose output

# Run API Server
cd src/DaprMQ.ApiServer
dapr run --app-id daprmq-api \
  --app-port 5000 \
  --resources-path ../../dapr/components \
  -- dotnet run

# Docker
docker-compose up                          # Full stack
```

## Testing & Development Workflow

**CRITICAL: Follow Test-First Development (TDD) Approach**

1. **Write tests BEFORE implementation code** - Always write failing tests first
2. **Run fast feedback tests after EVERY code change** - No exceptions
3. **Use integration tests for final verification** - Before committing

### Fast Feedback Loop (< 5 seconds)

Run unit tests after every code change - NO Docker required:

```bash
cd dotnet
dotnet test tests/DaprMQ.Tests/DaprMQ.Tests.csproj
```

### Full Verification (requires Docker)

Run integration tests before committing:

```bash
./build-and-test.sh
```

### Test Organization

- **Actor unit tests** (28 tests in `QueueActorTests.cs`): Business logic in `QueueActor.cs`
- **HTTP controller unit tests** (14 tests in `QueueControllerTests.cs`): HTTP status codes, request validation
- **gRPC service unit tests** (8 tests in `DaprMQGrpcServiceTests.cs`): gRPC status code mappings, error handling
- **Integration tests** (28 tests in `DaprMQ.IntegrationTests`): Full stack end-to-end with Dapr + PostgreSQL (HTTP + gRPC)

### When to Run Tests

- **After every code change**: Run fast unit tests (`dotnet test`)
- **Before committing**: Run full integration tests (`./build-and-test.sh`)
- **After refactoring**: Run both unit and integration tests
- **When fixing bugs**: Write failing test FIRST, then fix code

### Outside-In TDD Workflow (Recommended for New Features)

When adding new features to the API, follow this **outside-in** approach to ensure you build exactly what users need:

**1. Integration Tests First** (Define external API contract)
   - Write failing integration tests that describe the user-facing HTTP API
   - This defines the "what" - what should the feature do from a user's perspective?
   - Tests compile but fail because endpoints don't exist yet
   - Example: `ExtendLock_ValidLock_ExtendsExpiry` - full HTTP flow with real assertions

**2. API Models** (Make integration tests compile)
   - Add request/response models to `ApiServer/Models/ApiModels.cs`
   - Just enough to make integration tests compile and route correctly

**3. Controller Tests** (Define HTTP layer behavior)
   - Write failing controller unit tests using mocked actor responses
   - Test HTTP status code mappings: 200 OK, 400 Bad Request, 404 Not Found, 410 Gone, etc.
   - Verify error handling and response transformations
   - Example: `ExtendLock_LockExpired_Returns410`

**4. Actor Interface Models** (Make controller tests compile)
   - Add actor request/response models to `Interfaces/Models.cs`
   - Update `IQueueActor.cs` interface
   - Add method name constant to `ActorMethodNames.cs`

**5. Implement Controller Logic** (Make controller tests pass)
   - Implement the controller endpoint
   - Map actor responses to HTTP status codes
   - Handle error cases appropriately

**6. Actor Tests** (Define business logic)
   - Write failing actor unit tests with mocked `IActorStateManager`
   - Test all business logic paths: success, validation errors, edge cases
   - Example: `ExtendLock_MultipleExtensions_Accumulates`

**7. Implement Actor Logic** (Make actor tests pass)
   - Implement the actor method in `QueueActor.cs`
   - Focus on correctness - tests guide the implementation
   - All tests should now pass ✅

**Why Outside-In?**
- ✅ Ensures you build what users actually need (not over-engineering)
- ✅ Integration tests fail early if layers don't connect properly
- ✅ Each layer is tested independently (fast feedback)
- ✅ Prevents implementing features that don't match API design
- ✅ Clear progression: external contract → HTTP layer → business logic

**Fast Feedback Loop:**
```bash
# After each implementation step, run relevant tests
dotnet test --filter "FullyQualifiedName~ExtendLock"  # Run specific feature tests
dotnet test                                            # Run all unit tests (fast)
./build-and-test.sh                                   # Run integration tests (slow)
```

### Traditional TDD Workflow (For Internal Changes)

For changes that don't affect the external API (refactoring, bug fixes, internal optimizations):

1. Write failing test that describes desired behavior
2. Run test to confirm it fails (red) ❌
3. Write minimal code to make test pass (green) ✅
4. Refactor while keeping tests green
5. Run fast tests after each change
6. Commit only when all tests pass

### Controller Testing Architecture

The API server uses `IActorInvoker` abstraction for testability:

```csharp
// Interface enables mocking in unit tests
public interface IActorInvoker
{
    Task<TResponse> InvokeMethodAsync<TResponse>(...);
    Task<TResponse> InvokeMethodAsync<TRequest, TResponse>(...);
}

// Implementation wraps Dapr's ActorProxy
public class DaprActorInvoker : IActorInvoker { ... }
```

This pattern enables controller unit tests to mock actor responses and verify HTTP status code mappings without requiring Dapr runtime.

## Architecture Notes

### Actor Model
- Each actor ID = separate queue instance
- Single-threaded per actor (no race conditions)
- State persisted to Dapr state store (PostgreSQL, Redis, etc.)
- Automatic activation/deactivation via Dapr

### State Management (v4.0+ Segmented Queues)
- **Segmented storage**: Queues split into 100-item segments for performance
- State keys: `queue_0_seg_0`, `queue_0_seg_1`, etc. (priority + segment number)
- Metadata tracks: `head_segment` (pop from), `tail_segment` (push to), `count`
- Format: `List<string>` per segment (max 100 JSON items each)
- Push appends to tail segment, allocates new segment when full (100 items)
- Pop removes from head segment, deletes empty segments automatically
- State saved after every operation
- **Benefits**: Constant memory/network per operation regardless of queue size

### REST API Endpoints
- `POST /queue/{queueId}/push` - Push item
- `POST /queue/{queueId}/pop` - Pop single item (with optional `require_ack` header for locking)
- `POST /queue/{queueId}/acknowledge` - Acknowledge and remove locked item
- `POST /queue/{queueId}/extend-lock` - Extend existing lock TTL
- `GET /health` - Health check

### gRPC API

The API server exposes equivalent gRPC endpoints alongside HTTP REST APIs on the same port (5000). Both protocols share the same actor invocation logic via `IActorInvoker` abstraction.

**Proto File**: [dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto](dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto)

**Service Definition**:
```protobuf
service DaprMQ {
  rpc Push(PushRequest) returns (PushResponse);
  rpc Pop(PopRequest) returns (PopResponse);
  rpc PopWithAck(PopWithAckRequest) returns (PopWithAckResponse);
  rpc Acknowledge(AcknowledgeRequest) returns (AcknowledgeResponse);
  rpc ExtendLock(ExtendLockRequest) returns (ExtendLockResponse);
}
```

**Key Features**:
- **HTTP/2 Binary Protocol**: Lower latency than JSON over HTTP/1.1
- **Type Safety**: Protocol Buffers provide strongly-typed contracts
- **Cross-Language**: Proto files enable client generation in any language
- **Same Port**: HTTP and gRPC run on port 5000 (ASP.NET Core auto-negotiates)

**gRPC Status Code Mappings**:
```
HTTP 200 → gRPC OK (0)
HTTP 204 → gRPC OK with empty result
HTTP 400 → gRPC INVALID_ARGUMENT (3)
HTTP 404 → gRPC NOT_FOUND (5)
HTTP 410 → gRPC FAILED_PRECONDITION (9) - lock expired
HTTP 423 → gRPC UNAVAILABLE (14) - resource locked
HTTP 500 → gRPC INTERNAL (13)
```

**Testing with grpcurl**:
```bash
# List services
grpcurl -plaintext localhost:5000 list

# Push item
grpcurl -plaintext -d '{"queue_id":"test","item_json":"{\"msg\":\"hello\"}","priority":1}' \
  localhost:5000 daprmq.DaprMQ/Push

# Pop item
grpcurl -plaintext -d '{"queue_id":"test"}' \
  localhost:5000 daprmq.DaprMQ/Pop

# Pop with acknowledgement
grpcurl -plaintext -d '{"queue_id":"test","ttl_seconds":30}' \
  localhost:5000 daprmq.DaprMQ/PopWithAck
```

**C# Client Example**:
```csharp
using Grpc.Net.Client;
using DaprMQ.ApiServer.Grpc;

var channel = GrpcChannel.ForAddress("http://localhost:5000");
var client = new DaprMQ.DaprMQClient(channel);

// Push
var pushResponse = await client.PushAsync(new PushRequest
{
    QueueId = "my-queue",
    ItemJson = "{\"id\":1,\"name\":\"test\"}",
    Priority = 1
});

// Pop with acknowledgement
var popResponse = await client.PopWithAckAsync(new PopWithAckRequest
{
    QueueId = "my-queue",
    TtlSeconds = 30
});

if (popResponse.ResultCase == PopWithAckResponse.ResultOneofCase.Success)
{
    var lockId = popResponse.Success.LockId;
    // Process item...

    // Acknowledge
    await client.AcknowledgeAsync(new AcknowledgeRequest
    {
        QueueId = "my-queue",
        LockId = lockId
    });
}
```

**gRPC Reflection (Service Discovery)**:

The server enables gRPC Server Reflection, which allows clients to discover services and methods at runtime without needing the `.proto` file.

```bash
# Discover available services (use gRPC port 5001 for local dev, or 810x for docker)
grpcurl -plaintext localhost:5001 list

# Output:
# grpc.reflection.v1.ServerReflection
# daprmq.DaprMQ

# Describe a service
grpcurl -plaintext localhost:5001 describe daprmq.DaprMQ

# Describe a specific method
grpcurl -plaintext localhost:5001 describe daprmq.DaprMQ.Push

# Describe a message type
grpcurl -plaintext localhost:5001 describe daprmq.PushRequest

# Via Docker (gateway instance):
grpcurl -plaintext localhost:8102 list
```

**Proto File Access**:

While reflection is available, clients can also access the proto definition directly:
- **Source**: [dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto](dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto)
- **Package: daprmq`
- **C# Namespace**: `DaprMQ.ApiServer.Grpc`

For client generation in other languages:
```bash
# Generate Python client
python -m grpc_tools.protoc -I. --python_out=. --grpc_python_out=. daprmq.proto

# Generate Go client
protoc --go_out=. --go-grpc_out=. daprmq.proto

# Generate Java client
protoc --java_out=. --grpc-java_out=. daprmq.proto
```

**HTTP/2 Configuration**:

The server uses **separate ports** for HTTP/1.1 (REST) and HTTP/2 (gRPC) to avoid TLS negotiation requirements:

```csharp
// In Program.cs
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
```

**Port Assignment**:
- **Local development**: REST on 5000, gRPC on 5001
- **Docker (via docker-compose.yml)**:
  - Host 1: REST on 8000, gRPC on 8100
  - Host 2: REST on 8001, gRPC on 8101
  - Gateway: REST on 8002, gRPC on 8102
  - Host 3: REST on 8003, gRPC on 8103

This dual-port configuration is **required** because Kestrel cannot negotiate HTTP/2 without TLS when supporting both protocols on the same port.

**ARM64 (Apple Silicon) Support**:

To support native ARM64 Docker builds without emulation, the project uses **grpc-tools 2.68.1** instead of the newer 2.70.x versions:

- ✅ Native ARM64 Docker builds (no x86_64 emulation needed)
- ✅ Better performance on Apple Silicon
- ✅ Avoids grpc-tools 2.70.0 protoc crash (exit code 139 on linux_arm64)

The proto files are automatically generated during build from [daprmq.proto](dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto).

## Development Conventions

### Code Style
- Language: C# 13 with .NET 10.0
- Nullable reference types: Enabled
- Async/await: All actor methods are async Tasks

### Testing
- Framework: xUnit
- Mocking: Moq for IActorStateManager
- Coverage target: >95%
- Test file naming: `*Tests.cs`

### Namespace Patterns
```csharp
// External imports
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Dapr.Client;

// Internal imports
using DaprMQ.Interfaces;
```

## Important Notes

1. **Package Naming**:
   - NuGet package: `DaprMQ`
   - C# namespaces: `DaprMQ`, `DaprMQ.Interfaces`
   - GitHub repo: `dapr-mq`

2. **Actor Registration**: Must register `DaprMQ` before creating proxies

3. **Item Format**: Push accepts JSON strings (ItemJson property)

4. **Priority System**: Default priority is 1. Priority 0 is reserved as a "fast lane" for urgent items that need to interrupt normal processing. Lower priority numbers are processed first (0 before 1 before 2, etc.).

5. **FIFO Guarantee**: Items always returned in insertion order within priority

6. **Concurrency**: One operation per actor instance at a time

7. **Direct Actor API Access**: You can interact directly with actors via Dapr's HTTP API for testing and debugging. This allows you to invoke actor methods and inspect state without going through the application's REST API:
   ```bash
   # Invoke actor method directly
   curl -X POST http://localhost:3500/v1.0/actors/DaprMQ/my-queue/method/Push \
     -H "Content-Type: application/json" \
     -d '{"item": {"test": "data"}}'

   # Get actor state directly (useful for debugging)
   curl http://localhost:3500/v1.0/actors/DaprMQ/my-queue/state/queue
   ```
   - Dapr sidecar typically runs on port 3500 (configurable)
   - Actor type: `DaprMQ`
   - State key: `queue`
   - Full API reference: https://docs.dapr.io/reference/api/actors_api/

8. **Nonremoting Actor Invocations**: The API server uses Dapr's nonremoting mechanism (`InvokeMethodAsync`) for actor calls instead of interface-based remoting. This provides better decoupling and enables cross-language scenarios:
   ```csharp
   // Nonremoting approach (used by API server)
   var proxy = ActorProxy.Create(actorId, "QueueActor");
   var result = await proxy.InvokeMethodAsync<PushRequest, PushResponse>("Push", request);

   // Remoting approach (still supported for library users)
   var proxy = ActorProxy.Create<IQueueActor>(actorId, "QueueActor");
   var result = await proxy.Push(request);
   ```
   - Both approaches work with the same actor implementation
   - Nonremoting is recommended for API servers and cross-language scenarios
   - Remoting is convenient for type-safe C# applications

## Future Considerations

- Max queue size limits (currently unbounded)
- Batch push operations (currently one-at-a-time)
- Queue metrics/monitoring endpoints
- Alternative state store examples (Redis, CosmosDB)

## Quick Reference Links

- Dapr Docs: https://docs.dapr.io/
- Dapr .NET SDK: https://github.com/dapr/dotnet-sdk
- Project Issues: https://github.com/olitomlinson/dapr-mq/issues
