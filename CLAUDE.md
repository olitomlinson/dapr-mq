# Claude Context: DaprMQ

## Project Overview

**daprmq** is a C# library providing a simple FIFO queue-based Dapr actor for storing and retrieving JSON-serialized data. It's designed for lightweight message queuing, task scheduling, and ordered processing in Dapr applications.

- **Package Name**: `DaprMQ` (NuGet)
- **C# Namespaces**: `DaprMQ`, `DaprMQ.Interfaces`, `DaprMQ.ApiServer`
- **GitHub**: https://github.com/olitomlinson/dapr-mq
- **.NET**: 10.0+
- **Dapr**: 1.17.0+

## Project Structure

- `dotnet/src/DaprMQ/` - Actor implementation (QueueActor.cs, QueueActor.csproj)
- `dotnet/src/DaprMQ.Interfaces/` - Interfaces and models (IQueueActor.cs, Models.cs)
- `dotnet/src/DaprMQ.ApiServer/` - REST/gRPC API (Program.cs, Controllers/, Services/, Protos/)
- `dotnet/tests/DaprMQ.Tests/` - Unit tests (QueueActorTests.cs, QueueControllerTests.cs, DaprMQGrpcServiceTests.cs)
- `dashboard/` - React web UI (App.tsx, components/, services/, hooks/)
- `docs/` - QUICKSTART.md, ARCHITECTURE.md, API_REFERENCE.md
- `dapr/components/` - Dapr component configs (state store)
- `dapr/config/` - Dapr runtime config

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

## Error Handling Patterns

### ErrorCode Enum
All actor operations return responses with optional `ErrorCode`:
- `QueueFull` - Queue reached capacity
- `QueueEmpty` - No items available to pop
- `Locked` - Item already locked by another consumer
- `LockNotFound` - Lock ID doesn't exist or expired
- `LockExpired` - Lock TTL exceeded
- `ValidationError` - Invalid request (e.g., negative priority)
- `ActorNotFound` - Queue actor doesn't exist

**Location**: [Interfaces/Models.cs](dotnet/src/DaprMQ.Interfaces/Models.cs)

### Response Pattern
Actor responses use discriminated union pattern:
```csharp
// Check for errors first
if (response.ErrorCode.HasValue) {
    // Handle error based on ErrorCode
}
// Then handle success
var item = response.ItemJson;
```

### HTTP Status Mappings
Controller maps ErrorCode → HTTP status:
- `QueueEmpty` → 204 No Content
- `Locked` → 423 Locked
- `LockNotFound` / `LockExpired` → 410 Gone
- `ValidationError` → 400 Bad Request
- `ActorNotFound` → 404 Not Found
- Success → 200 OK

**Location**: [Controllers/QueueController.cs](dotnet/src/DaprMQ.ApiServer/Controllers/QueueController.cs)

### gRPC Response Pattern
gRPC uses protobuf `oneof` (union types):
```csharp
return new PopResponse {
    Success = new PopSuccess { ItemJson = item }
};
// OR
return new PopResponse {
    Empty = new PopEmpty { Message = "Queue empty" }
};
```
**Location**: [Services/DaprMQGrpcService.cs](dotnet/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)

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

**Runtime:** C# 13, .NET 10.0 | **Framework:** ASP.NET Core (HTTP + gRPC) | **Dapr:** Dapr.Actors, Dapr.AspNetCore, Dapr.Client | **gRPC:** Grpc.AspNetCore 2.70.0, Protocol Buffers | **Testing:** xUnit, Moq

## Dashboard (Web UI)

The DaprMQ Dashboard is an interactive React-based web interface for testing and managing queue operations. It provides a visual way to interact with the API server without requiring CLI tools or gRPC clients.

### Overview

- **Purpose**: Interactive testing UI for queue management (push, pop, acknowledgement, dead letter routing)
- **Technology**: React 18.2.0, TypeScript 5.2, Vite 5.0.8
- **Styling**: CSS Modules with CSS Variables
- **Deployment**: Nginx Alpine (production), Vite dev server (local)
- **Communication**: REST API only (no gRPC in dashboard)

### Key Features

- **Queue Management**: Create/select queue instances by ID with URL persistence (`?queue_name=my-queue`)
- **Message Publishing**: Push auto-generated test payloads with priority levels (0 = fast lane, 1+ = normal)
- **Pop Modes**: Basic pop (immediate removal) or pop with acknowledgement (30s TTL lock)
- **Lock Management**: Track locked messages with countdown, acknowledge or route to dead letter queue
- **Dead Letter Support**: Visual indicators for DLQ queues (`{queueId}-deadletter`), one-click routing for failed items
- **Real-time Stats**: Message count tracking per queue session
- **Auto-Generated Payloads**: `{ userId, action: login|logout|purchase|signup, timestamp }`

### Architecture

**Data Flow:**
```
Dashboard UI (React on :3000)
    ↓ (fetch REST API)
API Server (:8002)
    ↓ (IActorInvoker)
QueueActor (Dapr)
    ↓
State Store (PostgreSQL/Redis)
```

**API Integration:**
- Base URL: `VITE_API_BASE_URL` env var (default: `http://localhost:8002`)
- Dev proxy: `/queue/*` routes to API server during development
- Endpoints: `/queue/{id}/push`, `/queue/{id}/pop`, `/queue/{id}/acknowledge`, `/queue/{id}/deadletter`
- Error handling: Modal displays HTTP errors with status codes (204→empty, 400, 404, 410, 423, 500)

### Directory Structure

```
dashboard/
├── src/
│   ├── components/       # React components (CSS Modules)
│   │   ├── QueueHeader.tsx     # Queue ID editor + stats
│   │   ├── PushSection.tsx     # Priority-based push
│   │   ├── PopSection.tsx      # Pop vs PopWithAck
│   │   ├── MessagesList.tsx    # Message display list
│   │   ├── MessageItem.tsx     # Individual message + lock info
│   │   └── ErrorModal.tsx      # API error notifications
│   ├── hooks/            # React hooks
│   │   ├── useQueueOperations.ts  # Core state + API calls
│   │   └── useQueueId.ts          # URL param management
│   ├── services/
│   │   └── queueApi.ts          # API client (fetch-based)
│   ├── types/
│   │   └── queue.ts             # TypeScript interfaces
│   └── utils/
│       └── queueHelpers.ts      # ID generation, validation
├── vite.config.ts        # Build config + dev proxy
├── tsconfig.json         # TypeScript config (strict mode)
├── Dockerfile            # Multi-stage: Node → Nginx
├── nginx.conf            # SPA routing + asset caching
└── package.json          # Dependencies + scripts
```

### Key Files

- **[dashboard/src/App.tsx](dashboard/src/App.tsx)**: Root component with queue ID management
- **[dashboard/src/services/queueApi.ts](dashboard/src/services/queueApi.ts)**: API client with error handling
- **[dashboard/src/hooks/useQueueOperations.ts](dashboard/src/hooks/useQueueOperations.ts)**: Core state management and API orchestration
- **[dashboard/src/types/queue.ts](dashboard/src/types/queue.ts)**: TypeScript interfaces for API/UI models
- **[dashboard/vite.config.ts](dashboard/vite.config.ts)**: Dev proxy configuration
- **[dashboard/Dockerfile](dashboard/Dockerfile)**: Multi-stage build (Node build → Nginx serve)

### Development Setup

**Local Development:**
```bash
cd dashboard
npm install
npm run dev       # Runs on http://localhost:3000, proxies API to :8002
npm run build     # Production build → dist/
npm run preview   # Preview production build
```

**Docker:**
```bash
docker build -t daprmq-dashboard .
docker run -p 80:3000 daprmq-dashboard
```

**Environment Configuration:**
- `.env` - Development config
- `.env.production` - Production config
- `VITE_API_BASE_URL` - API server URL (replaced at build time)

### Integration with API Server

**Port Configuration:**
- Dashboard Dev: `localhost:3000` → proxies to API Server `:8002`
- Docker: Nginx serves static files, API server on `:8002`

**Queue Naming Convention:**
- Regular queues: Alphanumeric + `-_` (e.g., `user-events`, `task_queue_1`)
- Dead letter queues: `{originalId}-deadletter` (e.g., `user-events-deadletter`)
- Dashboard auto-appends `-deadletter` suffix when routing failed messages

**TypeScript Configuration:**
- Target: ES2020, Module: ESNext (tree-shaking)
- JSX: React JSX (automatic runtime)
- Strict mode enabled

### Development Conventions

- **Styling**: CSS Modules (`.module.css`) for component-scoped styles, CSS Variables for theming
- **State Management**: React hooks (`useState`, `useEffect`) - no external state library
- **Type Safety**: Full TypeScript with strict null checks
- **Naming**: PascalCase for components, camelCase for utilities/hooks
- **API Calls**: Encapsulated in `useQueueOperations` hook with loading/error states

## Common Commands

```bash
# Build
dotnet build                                          # All projects
dotnet build src/DaprMQ                              # Actor library only

# Test
dotnet test                                           # Run all tests
./build-and-test.sh                                  # Full integration (Docker)

# Run API Server
cd src/DaprMQ.ApiServer
dapr run --app-id daprmq-api --app-port 5000 \
  --resources-path ../../dapr/components -- dotnet run

# Dashboard
cd dashboard
npm install                                           # Install dependencies
npm run dev                                           # Dev server (localhost:3000)
npm run build                                         # Production build
docker build -t daprmq-dashboard .                   # Build container

# Docker
docker-compose up                                     # Full stack
```

## Configuration

### Environment Variables

**Actor Configuration:**
- `ACTOR_TYPE_NAME` (default: `"QueueActor"`) - Dapr actor type name
- `REGISTER_ACTORS` (default: `true`) - Set `false` for gateway instances

**Queue Performance:**
- `SEGMENT_DELETION_RETENTION_SECONDS` (default: `60`) - Cleanup retention for offloaded segments
- `SEGMENT_CLEANUP_SCAN_INTERVAL_SECONDS` (default: `60`, min: `1`) - Cleanup scan frequency

**Ports:**
- `ASPNETCORE_URLS` (default: `http://+:5000`) - HTTP port (gRPC uses next port)

**Example (docker-compose.yml):**
```yaml
environment:
  - ASPNETCORE_URLS=http://+:8080
  - REGISTER_ACTORS=false
  - SEGMENT_DELETION_RETENTION_SECONDS=120
```

### Lock Configuration (Constants)
- **Default TTL**: 30 seconds
- **Min TTL**: 1 second
- **Max TTL**: 300 seconds (5 minutes)
- Specified per `PopWithAck` request

### Queue Constants
- **Segment size**: 100 items per segment
- **Actor idle timeout**: 60 seconds
- **Lock ID length**: 11 characters

**Files**: [Program.cs](dotnet/src/DaprMQ.ApiServer/Program.cs), [QueueActor.cs:96-135](dotnet/src/DaprMQ/QueueActor.cs#L96-L135)

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

Follow this workflow to ensure API-first design:

1. **Integration Tests** - Write failing tests for HTTP API contract (what users see)
2. **API Models** - Add ApiModels.cs request/response types (make integration tests compile)
3. **Controller Tests** - Write unit tests with mocked actor, verify HTTP status codes
4. **Actor Models** - Add Interfaces/Models.cs types, update IQueueActor interface
5. **Controller Logic** - Implement controller endpoint (make controller tests pass)
6. **Actor Tests** - Write unit tests with mocked IActorStateManager (business logic)
7. **Actor Logic** - Implement QueueActor method (make all tests pass)

**Why:** Ensures layers connect properly, prevents over-engineering, maintains clear API → business logic progression.

**Fast Feedback:**
```bash
dotnet test --filter "FullyQualifiedName~ExtendLock"  # Feature-specific
dotnet test                                            # All unit tests
./build-and-test.sh                                   # Full integration
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

### Common Patterns

**Actor Method Constants** (avoid magic strings):
```csharp
using DaprMQ.ApiServer.Constants;

await _actorInvoker.InvokeMethodAsync<PushRequest, PushResponse>(
    actorId,
    ActorMethodNames.Push,  // Use constant, not "Push" string
    request);
```
**Location**: [Constants/ActorMethodNames.cs](dotnet/src/DaprMQ.ApiServer/Constants/ActorMethodNames.cs)

**Actor Invocation (from controllers):**
```csharp
var result = await _actorInvoker.InvokeMethodAsync<PopResponse>(
    new ActorId(queueId),
    ActorMethodNames.Pop);
```

**Validation Pattern** (two layers):
1. **Controller**: Validates request format, required fields → 400 Bad Request
2. **Actor**: Validates business logic (e.g., priority range, TTL bounds) → ErrorCode in response

**Test Mocking Pattern** (StateManager):
```csharp
var stateData = new Dictionary<string, object>();
mock.Setup(m => m.TryGetStateAsync<QueueMetadata>(...))
    .ReturnsAsync((string key, CancellationToken ct) => {
        return stateData.ContainsKey(key)
            ? new ConditionalValue<QueueMetadata>(true, (QueueMetadata)stateData[key])
            : new ConditionalValue<QueueMetadata>(false, null);
    });
```
**Location**: [QueueActorTests.cs](dotnet/tests/DaprMQ.Tests/QueueActorTests.cs)

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

### State Key Conventions

**Three-tier naming pattern:**
1. **Queue segments**: `queue_{priority}_seg_{segmentNum}` - In-memory segment storage
2. **Metadata**: `metadata_{priority}` - Tracks head/tail segment numbers, counts
3. **Lock state**: `locks` - All active locks for the queue
4. **Offloaded segments**: `offloaded_queue_{priority}_seg_{segmentNum}_{actorId}` - External storage
5. **Deletion metadata**: `segment_deletion_metadata` - Tracks pending deletions

**Example state operations:**
```csharp
// Read metadata
var metadata = await StateManager.GetStateAsync<QueueMetadata>($"metadata_{priority}");

// Save segment
await StateManager.SetStateAsync($"queue_{priority}_seg_{segmentNum}", items);

// Atomic commit
await StateManager.SaveStateAsync();
```

**Pattern**: All state changes batched, then committed with single `SaveStateAsync()` call.

**Location**: [QueueActor.cs](dotnet/src/DaprMQ/QueueActor.cs)

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

**Port Configuration:**

| Environment | HTTP | gRPC |
|-------------|------|------|
| Local Dev   | 5000 | 5001 |
| Docker Host 1-3 | 8000-8003 | 8100-8103 |
| Docker Gateway | 8002 | 8102 |

**Note:** Separate ports required - Kestrel cannot negotiate HTTP/2 without TLS on same port.

**Status Mappings:** 200→OK(0), 204→OK(empty), 400→INVALID_ARGUMENT(3), 404→NOT_FOUND(5), 410→FAILED_PRECONDITION(9), 423→UNAVAILABLE(14), 500→INTERNAL(13)

**Testing with grpcurl** (use port 5001 for local dev, 810x for docker):
```bash
# Push item
grpcurl -plaintext -d '{"queue_id":"test","item_json":"{\"msg\":\"hello\"}","priority":1}' \
  localhost:5001 daprmq.DaprMQ/Push

# Pop with acknowledgement
grpcurl -plaintext -d '{"queue_id":"test","ttl_seconds":30}' \
  localhost:5001 daprmq.DaprMQ/PopWithAck
```

**C# Client Example**:
```csharp
using Grpc.Net.Client;
using DaprMQ.ApiServer.Grpc;

var channel = GrpcChannel.ForAddress("http://localhost:5001");
var client = new DaprMQ.DaprMQClient(channel);

var response = await client.PopWithAckAsync(new PopWithAckRequest
    { QueueId = "my-queue", TtlSeconds = 30 });

if (response.ResultCase == PopWithAckResponse.ResultOneofCase.Success) {
    // Process item, then acknowledge
    await client.AcknowledgeAsync(new AcknowledgeRequest
        { QueueId = "my-queue", LockId = response.Success.LockId });
}
```

**gRPC Reflection (Service Discovery)**:

Server Reflection enabled - discover services at runtime without `.proto` file.

```bash
# Discover available services (see port table above)
grpcurl -plaintext localhost:5001 list
grpcurl -plaintext localhost:5001 describe daprmq.DaprMQ
grpcurl -plaintext localhost:5001 describe daprmq.DaprMQ.Push
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

This dual-port configuration is **required** because Kestrel cannot negotiate HTTP/2 without TLS when supporting both protocols on the same port (see port table above).

**ARM64 (Apple Silicon) Support**:

To support native ARM64 Docker builds without emulation, the project uses **grpc-tools 2.68.1** instead of the newer 2.70.x versions:

- ✅ Native ARM64 Docker builds (no x86_64 emulation needed)
- ✅ Better performance on Apple Silicon
- ✅ Avoids grpc-tools 2.70.0 protoc crash (exit code 139 on linux_arm64)

The proto files are automatically generated during build from [daprmq.proto](dotnet/src/DaprMQ.ApiServer/Protos/daprmq.proto).

## Development Conventions

- **Language:** C# 13 (.NET 10.0), nullable reference types enabled, async Tasks
- **Testing:** xUnit + Moq, >95% coverage target, `*Tests.cs` naming
- **Imports:** External first (Dapr.*), then internal (DaprMQ.*)

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

7. **Direct Actor API Access**: Interact with actors via Dapr's HTTP API (port 3500) for testing/debugging:
   ```bash
   # Invoke actor method
   curl -X POST http://localhost:3500/v1.0/actors/DaprMQ/my-queue/method/Push \
     -H "Content-Type: application/json" -d '{"item": {"test": "data"}}'
   ```
   Actor type: `DaprMQ`, State key: `queue`. See https://docs.dapr.io/reference/api/actors_api/

8. **Nonremoting Actor Invocations**: API server uses `InvokeMethodAsync` for better decoupling and cross-language support:
   ```csharp
   // Nonremoting (API server)
   var proxy = ActorProxy.Create(actorId, "QueueActor");
   var result = await proxy.InvokeMethodAsync<PushRequest, PushResponse>("Push", request);

   // Remoting (library users)
   var proxy = ActorProxy.Create<IQueueActor>(actorId, "QueueActor");
   var result = await proxy.Push(request);
   ```

9. **Lock Architecture**: Locks are stored in-place (not separate queue). When item is locked, it remains in queue with `LockId` attached until acknowledged. This enables:
   - Dead letter handling (move locked items on expiry)
   - Lock extension (modify TTL without re-queuing)
   - FIFO guarantee (position preserved during lock)

10. **Segment Offloading**: When queue grows large, older segments move to external Dapr state store. In-memory buffer keeps recent segments for fast access. Offloaded segments cleaned up after `SEGMENT_DELETION_RETENTION_SECONDS` (default 60s).

11. **Dead Letter Queue**: DLQ is a separate actor instance with ID `{originalId}-deadletter`. Failed items moved via internal actor-to-actor invocation using `IActorInvoker`.

## Future Considerations

- Max queue size limits (currently unbounded)
- Batch push operations (currently one-at-a-time)
- Queue metrics/monitoring endpoints
- Alternative state store examples (Redis, CosmosDB)

## Quick Reference Links

- Dapr Docs: https://docs.dapr.io/
- Dapr .NET SDK: https://github.com/dapr/dotnet-sdk
- Project Issues: https://github.com/olitomlinson/dapr-mq/issues
