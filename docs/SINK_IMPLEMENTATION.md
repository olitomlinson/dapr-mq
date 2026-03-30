# Implementation: HTTP Sinks for DaprMQ

## Context

DaprMQ provides automatic HTTP delivery of queued messages to configured endpoints (sinks) using a internal **polling model**. This enables event-driven workflows without requiring consumers to implement their own polling loops.

**Why:** Enable automatic message delivery to external HTTP endpoints (webhooks, event forwarding, serverless function triggers) with acknowledgement-based reliability.

**Architecture:** The HTTP Sink entity is essentially a Client of the Queue.  HTTP Sink uses Dapr Actor Reminders to periodically poll the Queue via `PopWithAck`, then delivers batches of messages via HTTP POST. 

## Architecture Overview

```
Push Message → QueueActor (no sink knowledge)

Separate flow (every N seconds):

Dapr Actor Reminder → SinkActor.ReceiveReminderAsync("sink-poll")
                          ↓
                      PollAndDeliver()
                          ↓
        PopWithAck(Count=100, MaxConcurrency=M) → QueueActor
                          ↓
                    Items with locks
                          ↓
                      HTTP POST → External Endpoint
                          ↓
            If HTTP 200 OK: Acknowledge locks → QueueActor
            If HTTP 202 Accepted: Endpoint handles ack
            If HTTP failure: Locks expire → auto-requeue
```

## Key Features

1. **Complete Decoupling**: QueueActor has ZERO knowledge of sink actors - no methods, no state, no references
2. **Self-Regulating Polling**: SinkActor controls its own polling frequency via Dapr Actor Reminders
3. **MaxConcurrency Control**: SinkActor passes `MaxConcurrency` parameter to `PopWithAck` - QueueActor returns `min(Count, MaxConcurrency - LockCount)` items
4. **Batch Efficiency**: Requests up to 100 items per poll, respects concurrency limits
5. **Fail-Fast Retry**: No retries in SinkActor, leverage existing lock expiry for reliability
6. **Predictable Actor ID**: `{queueId}-sink` pattern (follows `{queueId}-deadletter` convention)
7. **Configurable Polling**: 1-60 seconds interval (default: 5s)
8. **No Authentication**: v1 uses unauthenticated HTTP POST (can add headers in v2)

## Data Models

### SinkActorState (SinkActor.cs)

```csharp
public record SinkActorState
{
    public required string Url { get; init; }                      // HTTP endpoint URL
    public required string QueueActorId { get; init; }             // Queue to poll from
    public required int MaxConcurrency { get; init; }              // Max concurrent locks
    public required int LockTtlSeconds { get; init; }              // Lock TTL for PopWithAck
    public required int PollingIntervalSeconds { get; init; }      // Reminder interval
}
```

### InitializeSinkRequest (Models.cs)

```csharp
public record InitializeSinkRequest
{
    public required string Url { get; init; }
    public required string QueueActorId { get; init; }
    public required int MaxConcurrency { get; init; }
    public required int LockTtlSeconds { get; init; }
    public required int PollingIntervalSeconds { get; init; }
}
```

### PopWithAckRequest Enhancement (Models.cs)

```csharp
public record PopWithAckRequest
{
    public int TtlSeconds { get; init; } = 30;
    public int Count { get; init; } = 1;
    public bool AllowCompetingConsumers { get; init; } = false;
    public int? MaxConcurrency { get; init; }  // NEW: Optional capacity limit
}
```

### PopWithAckResponse Enhancement (Models.cs)

```csharp
public record PopWithAckResponse
{
    public List<PopWithAckItem> Items { get; init; } = new();
    public bool Locked { get; init; }
    public bool IsEmpty { get; init; }
    public bool MaxConcurrencyReached { get; init; }  // NEW: Capacity limit reached
    public string? Message { get; init; }
}
```

### API Models (ApiModels.cs)

```csharp
public record ApiRegisterSinkRequest(
    string Url,
    int MaxConcurrency = 5,
    int LockTtlSeconds = 30,
    int PollingIntervalSeconds = 5  // NEW: Configurable polling interval
);
```

## Implementation Details

### 1. QueueActor Changes (QueueActor.cs)

**Modified Methods:**

- `PopWithAck()` - Added MaxConcurrency enforcement:
  ```csharp
  if (request.MaxConcurrency.HasValue)
  {
      int availableCapacity = Math.Max(0, request.MaxConcurrency.Value - metadata.LockCount);
      count = Math.Min(count, availableCapacity);

      if (count == 0)
      {
          return new PopWithAckResponse
          {
              Locked = false,
              IsEmpty = false,
              MaxConcurrencyReached = true,
              Message = $"MaxConcurrency limit reached ({metadata.LockCount}/{request.MaxConcurrency.Value})"
          };
      }
  }
  ```

### 2. SinkActor Implementation (SinkActor.cs)

**Implements:** `ISinkActor, IRemindable`

**Key Methods:**

- **`InitializeSink(InitializeSinkRequest)`** - Stores state, registers "sink-poll" reminder
  ```csharp
  await RegisterReminderAsync(
      "sink-poll",
      null,
      TimeSpan.Zero,  // Start immediately
      TimeSpan.FromSeconds(request.PollingIntervalSeconds));
  ```

- **`UninitializeSink()`** - Unregisters reminder, clears state

- **`ReceiveReminderAsync()`** - Dapr callback, triggers `PollAndDeliver()`

- **`PollAndDeliver()`** - Core polling logic:
  ```
  1. Load SinkActorState from StateManager
  2. Call PopWithAck on QueueActor:
     - Count = 100 (request up to 100 items)
     - MaxConcurrency = state.MaxConcurrency
     - TtlSeconds = state.LockTtlSeconds
     - AllowCompetingConsumers = true
  3. If Items.Count == 0:
     - If MaxConcurrencyReached: log "max concurrency reached"
     - Else: log "no items available"
     - Return early
  4. Create HttpClient with 30s timeout
  5. Serialize popResult.Items as JSON (camelCase)
  6. POST to state.Url
  7. If HTTP 200 OK:
     - For each item: Acknowledge(lockId) on QueueActor
  8. If HTTP 202 Accepted:
     - Log "endpoint will handle acks"
  9. If HTTP failure:
     - Log error (locks will expire and auto-requeue)
  ```

### 3. ISinkActor Interface (ISinkActor.cs)

```csharp
public interface ISinkActor : IActor
{
    Task InitializeSink(InitializeSinkRequest request);
    Task UninitializeSink();
}
```

### 4. API Endpoints (QueueController.cs)

**Endpoints:**

- `POST /queue/{queueId}/sink/http/register` - Initialize SinkActor with polling configuration
- `POST /queue/{queueId}/sink/http/unregister` - Uninitialize SinkActor (stops polling)

**Register Flow:**
```
1. Validate request:
   - URL not empty, valid absolute URI
   - MaxConcurrency: 1-100
   - LockTtlSeconds: 1-300
   - PollingIntervalSeconds: 1-60
2. Calculate sinkActorId = "{queueId}-sink"
3. Build InitializeSinkRequest with all parameters
4. Invoke SinkActor.InitializeSink(request)
   - Stores state
   - Registers reminder "sink-poll"
   - Starts polling immediately
5. Return 200 with sinkActorId
```

**Unregister Flow:**
```
1. Calculate sinkActorId = "{queueId}-sink"
2. Invoke SinkActor.UninitializeSink()
   - Unregisters reminder "sink-poll"
   - Clears state
3. Return 200
```

**Note:** QueueActor is NEVER contacted during register/unregister - complete decoupling

### 5. Actor Registration (Program.cs)

```csharp
options.Actors.RegisterActor<DaprMQ.SinkActor>("SinkActor");
```

### 6. Constants (ActorMethodNames.cs)

```csharp
public const string InitializeSink = "InitializeSink";
public const string UninitializeSink = "UninitializeSink";
```

### 7. gRPC Support

**Not in v1** - gRPC endpoints for sink registration will be added in a later task. Focus on HTTP REST API only.

## Testing Strategy

### Unit Tests

**File:** `QueueActorTests.cs`

1. `PopWithAck_WithMaxConcurrency_ReturnsAvailableCapacity` - Verify MaxConcurrency enforcement
2. `PopWithAck_WithMaxConcurrencyAtLimit_ReturnsMaxConcurrencyReached` - Verify capacity limit response

**File:** `SinkActorTests.cs` (TODO: Create new file)

1. `InitializeSink_RegistersReminder` - Verify reminder registration
2. `UninitializeSink_UnregistersReminder` - Verify reminder unregistration
3. `ReceiveReminderAsync_CallsPollAndDeliver` - Verify reminder callback
4. `PollAndDeliver_EmptyQueue_ReturnsEarly` - Verify early return on empty queue
5. `PollAndDeliver_MaxConcurrencyReached_LogsAndReturns` - Verify capacity limit handling
6. `PollAndDeliver_WithItems_CallsPopWithAck` - Verify PopWithAck invocation
7. `PollAndDeliver_Http200_AcknowledgesAllLocks` - Verify acknowledgement on success
8. `PollAndDeliver_Http202_DoesNotAcknowledge` - Verify 202 Accepted handling
9. `PollAndDeliver_HttpFailure_LogsError` - Verify failure handling

**File:** `QueueControllerTests.cs`

1. `RegisterSink_WithInvalidUrl_Returns400`
2. `RegisterSink_WithInvalidMaxConcurrency_Returns400`
3. `RegisterSink_WithInvalidLockTtl_Returns400`
4. `RegisterSink_WithInvalidPollingInterval_Returns400`
5. `RegisterSink_Success_Returns200WithSinkId` - Verify only SinkActor.InitializeSink is called
6. `UnregisterSink_Success_Returns200` - Verify only SinkActor.UninitializeSink is called

### Integration Tests (TODO: Update existing)

**File:** `SinkDeliveryTests.cs`

1. `RegisterSink_WithValidConfig_ReturnsSuccess` - Verify registration API
2. `PollingDeliversMessages_OnSchedule` - Wait for polling interval, verify delivery
3. `HttpDelivery_On200Success_AcknowledgesLock` - Verify ack flow on 200
4. `HttpDelivery_On202Accepted_DoesNotAcknowledge` - Verify 202 leaves lock for endpoint to ack
5. `HttpDelivery_OnFailure_LeavesLockToExpire` - Verify failure lets lock expire (wait for TTL, verify requeue)
6. `ConcurrencyLimit_EnforcedByPopWithAck` - Push 10 items with MaxConcurrency=3, verify only 3 delivered
7. `PollingInterval_Configurable` - Verify 1s, 5s, 10s intervals work correctly

## Verification

1. **Unit tests pass** - `cd dotnet && dotnet test` (✅ 111 passed, 0 failed)
2. **Integration tests pass** - `./build-and-test.sh` (✅ 42 passed, 0 failed)
3. **Manual testing:**
   ```bash
   # Start API server
   cd dotnet/src/DaprMQ.ApiServer
   dapr run --app-id daprmq-api --app-port 5000 --resources-path ../../dapr/components -- dotnet run

   # Start test HTTP server
   python3 -m http.server 8080  # Receives POST requests

   # Register sink with 2-second polling
   curl -X POST http://localhost:5000/queue/test-queue/sink/http/register \
     -H "Content-Type: application/json" \
     -d '{"url":"http://localhost:8080/webhook","maxConcurrency":5,"lockTtlSeconds":30,"pollingIntervalSeconds":2}'

   # Push messages
   curl -X POST http://localhost:5000/queue/test-queue/push \
     -H "Content-Type: application/json" \
     -d '{"items":[{"itemJson":"{\"test\":\"data1\"}","priority":1},{"itemJson":"{\"test\":\"data2\"}","priority":1}]}'

   # Wait 2-3 seconds - SinkActor reminder will fire
   # Verify HTTP POST received at localhost:8080 with array of PopWithAckItem objects
   # Expected body: [{"itemJson":"...","lockId":"...","priority":1,"lockExpiresAt":...}, ...]

   # Unregister sink
   curl -X POST http://localhost:5000/queue/test-queue/sink/http/unregister

   # Verify polling stops (no more HTTP requests)
   ```

## Error Handling

- **Invalid URL**: Return 400 Bad Request from register endpoint
- **MaxConcurrency out of range (< 1 or > 100)**: Return 400 Bad Request
- **LockTtlSeconds out of range (< 1 or > 300)**: Return 400 Bad Request
- **PollingIntervalSeconds out of range (< 1 or > 60)**: Return 400 Bad Request
- **HTTP delivery timeout**: Log error, locks expire → auto-requeue
- **HTTP 4xx/5xx**: Log error with status code, locks expire → auto-requeue
- **Network unreachable**: Log error, locks expire → auto-requeue
- **SinkActor initialization fails**: Return 500 from register endpoint
- **Reminder registration fails**: Log warning, continue (graceful degradation)
- **Queue empty**: Log debug, return early, wait for next reminder
- **MaxConcurrency reached**: Log debug with `MaxConcurrencyReached` flag, return early

## Concurrency Safeguards

- **MaxConcurrency enforcement**: QueueActor's `PopWithAck` calculates `min(Count, MaxConcurrency - LockCount)`
- **MaxConcurrencyReached flag**: Explicit feedback when capacity limit reached
- **Batch size control**: SinkActor requests up to 100 items per poll, but QueueActor enforces MaxConcurrency
- **Actor single-threading**: Dapr guarantees one operation per actor at a time
- **Reminder serialization**: Multiple reminder callbacks don't run concurrently on same actor instance
- **Lock expiry fallback**: Failed deliveries self-heal via existing lock expiry mechanism

## Key Design Decisions

1. **Pull-Based Polling** - SinkActor uses Dapr Actor Reminders to poll QueueActor, not push-based delivery
2. **Complete Decoupling** - QueueActor has ZERO knowledge of sink actors (no methods, no state, no references)
3. **MaxConcurrency via PopWithAck** - SinkActor passes `MaxConcurrency` parameter to `PopWithAck`, QueueActor enforces limit
4. **Predictable Sink Actor ID** - `{queueId}-sink` pattern (follows `{queueId}-deadletter` convention)
5. **Configurable Polling Interval** - 1-60 seconds (default: 5s), stored in SinkActorState
6. **Batch Efficiency** - Requests up to 100 items per poll, respects MaxConcurrency dynamically
7. **Fail-Fast HTTP** - No retries in SinkActor, leverage existing lock expiry for reliability
8. **Self-Regulating** - SinkActor controls its own polling frequency, no external coordination needed
9. **202 Means Endpoint Owns Ack** - 200 = SinkActor acks, 202 = endpoint will ack later via API
10. **No Auth in v1** - Simpler implementation, can add header support in v2
11. **Reminder-Based Scheduling** - Uses Dapr's built-in Actor Reminders (durable, survives restarts)
12. **MaxConcurrencyReached Flag** - Explicit feedback when capacity limit reached vs queue empty

## Future Enhancements (Not in v1)

- **Authentication** - Authorization headers, API keys, OAuth tokens
- **Retry Policies** - Exponential backoff in SinkActor for transient failures
- **Multiple Sink Types** - gRPC, SQS, Kafka, Azure Service Bus
- **Sink Metrics** - Delivery count, failure rate, latency tracking
- **Dead Letter on Repeated Failures** - Move to DLQ after N failed deliveries
- **Dynamic Polling** - Adjust interval based on queue depth or failure rate
- **Batch Size Tuning** - Make the 100-item batch size configurable
- **Circuit Breaker** - Pause polling on repeated HTTP failures
- **Priority-Based Polling** - Poll high-priority queues more frequently
- **gRPC Endpoints** - Add gRPC versions of register/unregister APIs
