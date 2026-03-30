# Feature: Dead Letter Queue (DLQ)

## Overview

The Dead Letter Queue feature provides a mechanism to safely handle messages that cannot be processed successfully. When a locked message fails processing, it can be moved to a separate dead letter queue actor instance where it can be inspected, reprocessed, or archived without blocking the main queue.

**Key Characteristics:**
- **Lock-Based Safety**: Only locked messages can be moved to DLQ (requires prior PopWithAck)
- **Atomic Operation**: Item pushed to DLQ BEFORE being removed from main queue (prevents data loss)
- **Item Removal**: Successfully moved items are fully removed from the main queue
- **Priority Preservation**: Items maintain their original priority in the DLQ
- **Separate Actor Instance**: DLQ uses naming convention `{actorId}-deadletter`
- **Indefinite Storage**: Messages in DLQ have no automatic expiration
- **Lock Validation**: Expired or invalid locks are rejected

## Use Cases

- **Failed Message Handling**: Move messages that consistently fail processing to DLQ for later analysis
- **Poison Message Isolation**: Prevent problematic messages from blocking queue processing
- **Manual Intervention**: Allow operators to inspect and manually reprocess failed messages
- **Error Analysis**: Collect failed messages for debugging and pattern analysis
- **Circuit Breaking**: Stop retrying failed messages after N attempts (app-level logic)

## Architecture

### DLQ Actor Naming

Dead letter queues are separate actor instances with predictable names:
```
Original Queue: "order-queue"
Dead Letter Queue: "order-queue-deadletter"
```

This naming convention enables:
- Easy discovery of DLQ actors
- Clear association between main queue and DLQ
- Independent lifecycle management

### Operation Flow

1. **Lock Validation**: Verify lock exists, matches provided ID, and hasn't expired
2. **Item Retrieval**: Use `Peek()` to read locked item data (for DLQ push)
3. **Push to DLQ**: Create DLQ actor proxy and push item with original priority
4. **Remove Item**: Dequeue item from main queue and update metadata
5. **Void Lock**: Remove lock state ONLY after successful DLQ push and item removal
6. **State Save**: Atomically persist all changes

### Priority Preservation

The DLQ preserves the original priority of failed messages:
```csharp
// Priority 0 item fails processing
var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });
// popResult.LockId = "abc123"

// Move to DLQ - maintains priority 0
await actor.DeadLetter(new DeadLetterRequest { LockId = "abc123" });

// DLQ now contains priority 0 item at: "my-queue-deadletter"
```

## API

### New Method: DeadLetter

**Actor Interface:**
```csharp
Task<DeadLetterResponse> DeadLetter(DeadLetterRequest request)
```

**Request Model:**
```csharp
public record DeadLetterRequest
{
    public string LockId { get; init; }  // Lock ID from PopWithAck
}
```

**Response Model:**
```csharp
public record DeadLetterResponse
{
    public required string Status { get; init; }        // "SUCCESS" or "ERROR"
    public string? ErrorCode { get; init; }             // Error code if Status="ERROR"
    public string? Message { get; init; }               // Human-readable message
    public string? DlqId { get; init; }                 // DLQ ID if Status="SUCCESS"
}
```

**HTTP REST API:**
```bash
POST /queue/{queueId}/deadletter
Content-Type: application/json

{
  "lockId": "abc123def456"
}
```

**gRPC API:**
```protobuf
rpc DeadLetter(DeadLetterRequest) returns (DeadLetterResponse);

message DeadLetterRequest {
  string queue_id = 1;
  string lock_id = 2;
}

message DeadLetterResponse {
  oneof result {
    DeadLetterSuccess success = 1;
    DeadLetterError error = 2;
  }
}
```

## Error Handling

### Error Codes

| Error Code | HTTP Status | gRPC Status | Description |
|------------|-------------|-------------|-------------|
| `LOCK_NOT_FOUND` | 404 Not Found | NOT_FOUND | No active lock exists |
| `INVALID_LOCK_ID` | 400 Bad Request | INVALID_ARGUMENT | Lock ID doesn't match active lock |
| `LOCK_EXPIRED` | 410 Gone | FAILED_PRECONDITION | Lock TTL has expired |
| `ITEM_NOT_FOUND` | 500 Internal | INTERNAL | Locked item missing from segment |
| `DLQ_PUSH_FAILED` | 500 Internal | INTERNAL | Failed to push to DLQ actor |

### Error Scenarios

**LOCK_NOT_FOUND**: Queue has no active lock
```bash
# No lock exists
POST /queue/my-queue/deadletter {"lockId": "xyz"}
# Response: 404 Not Found
```

**INVALID_LOCK_ID**: Wrong lock ID provided
```bash
# Lock exists with ID "abc123" but wrong ID provided
POST /queue/my-queue/deadletter {"lockId": "wrong"}
# Response: 400 Bad Request
```

**LOCK_EXPIRED**: Lock TTL has passed
```bash
# Lock created with 30s TTL, 35 seconds elapsed
POST /queue/my-queue/deadletter {"lockId": "abc123"}
# Response: 410 Gone
```

## Usage Examples

### Example 1: Basic DLQ Flow (HTTP)

```bash
# 1. Push message to queue
curl -X POST http://localhost:5000/queue/orders/push \
  -H "Content-Type: application/json" \
  -d '{"items": [{"item": {"orderId": 123, "customer": "Alice"}, "priority": 1}]}'

# 2. Pop with acknowledgement
curl -X POST http://localhost:5000/queue/orders/pop \
  -H "require_ack: true" \
  -H "ttl_seconds: 30"
# Response: {"item": {...}, "locked": true, "lockId": "abc123def456"}

# 3. Processing fails - move to DLQ
curl -X POST http://localhost:5000/queue/orders/deadletter \
  -H "Content-Type: application/json" \
  -d '{"lockId": "abc123def456"}'
# Response: {"success": true, "dlqActorId": "orders-deadletter"}

# 4. Main queue is now unlocked and empty
curl -X POST http://localhost:5000/queue/orders/pop \
  -H "require_ack: false"
# Response: 204 No Content (empty)

# 5. Inspect DLQ
curl -X POST http://localhost:5000/queue/orders-deadletter/pop \
  -H "require_ack: false"
# Response: {"item": {"orderId": 123, "customer": "Alice"}}
```

### Example 2: Retry Pattern with DLQ (C#)

```csharp
var actor = ActorProxy.Create<IQueueActor>(new ActorId("orders"), "QueueActor");

// Pop with acknowledgement
var popResult = await actor.PopWithAck(new PopWithAckRequest { TtlSeconds = 30 });

if (popResult.ItemJson != null)
{
    var lockId = popResult.LockId!;
    int retryCount = 0;
    const int maxRetries = 3;

    while (retryCount < maxRetries)
    {
        try
        {
            // Process message
            await ProcessOrder(popResult.ItemJson);

            // Success - acknowledge
            await actor.Acknowledge(new AcknowledgeRequest { LockId = lockId });
            break;
        }
        catch (Exception ex)
        {
            retryCount++;
            if (retryCount >= maxRetries)
            {
                // Max retries exceeded - move to DLQ
                var dlqResult = await actor.DeadLetter(new DeadLetterRequest { LockId = lockId });
                Console.WriteLine($"Moved to DLQ: {dlqResult.DlqId}");
            }
            else
            {
                // Retry after delay
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retryCount)));
            }
        }
    }
}
```

### Example 3: Priority Preservation (gRPC)

```csharp
var client = new DaprMQ.DaprMQClient(channel);

// Push urgent priority 0 message
await client.PushAsync(new PushRequest
{
    QueueId = "alerts",
    Items =
    {
        new PushItem
        {
            ItemJson = "{\"severity\":\"critical\",\"alert\":\"disk full\"}",
            Priority = 0  // Fast lane
        }
    }
});

// Pop with acknowledgement
var popResponse = await client.PopWithAckAsync(new PopWithAckRequest
{
    QueueId = "alerts",
    TtlSeconds = 30
});

var lockId = popResponse.Success.LockId;

// Processing fails - move to DLQ (preserves priority 0)
await client.DeadLetterAsync(new DeadLetterRequest
{
    QueueId = "alerts",
    LockId = lockId
});

// DLQ now contains priority 0 item at "alerts-deadletter"
// If we later push priority 1 items to DLQ, priority 0 items will pop first
```

## Implementation Details

### Actor Method (QueueActor.cs)

```csharp
public async Task<DeadLetterResponse> DeadLetter(DeadLetterRequest request)
{
    // 1. Validate lock exists and not expired
    var lockState = await StateManager.TryGetStateAsync<LockState>("_active_lock");
    if (!lockState.HasValue) return LOCK_NOT_FOUND;
    if (lockState.Value.LockId != request.LockId) return INVALID_LOCK_ID;
    if (now >= lockState.Value.ExpiresAt) return LOCK_EXPIRED;

    // 2. Get locked item data using Peek()
    string segmentKey = $"queue_{lockData.Priority}_seg_{lockData.HeadSegment}";
    var segment = await StateManager.TryGetStateAsync<Queue<string>>(segmentKey);
    string itemJson = segment.Value.Peek();  // Read data without removing yet

    // 3. Push to DLQ actor with original priority
    string dlqActorId = $"{Id.GetId()}-deadletter";
    var pushResult = await _actorInvoker.InvokeMethodAsync<PushRequest, PushResponse>(
        new ActorId(dlqActorId),
        _actorConfig.ActorTypeName,
        "Push",
        new PushRequest { ItemJson = itemJson, Priority = lockData.Priority });

    if (!pushResult.Success) return DLQ_PUSH_FAILED;

    // 4. Remove item from main queue (same logic as Acknowledge)
    segment.Value.Dequeue();  // Now remove the item
    await UpdateMetadataAndCleanupSegment(segmentKey, segment.Value, lockData.Priority);

    // 5. Void lock (only after successful DLQ push and item removal)
    await StateManager.RemoveStateAsync("_active_lock");
    await StateManager.SaveStateAsync();

    return SUCCESS;
}
```

### Key Design Decisions

1. **Peek Then Dequeue**: Use `Peek()` to read item data for DLQ push, then `Dequeue()` to remove it after successful push. This ensures data is available for DLQ before removal.

2. **Atomicity**: Push to DLQ FIRST, remove item from main queue SECOND, void lock THIRD. If DLQ push fails, lock remains active and item stays in queue for retry or expiration.

3. **Item Removal Like Acknowledge**: `DeadLetter` behaves like `Acknowledge` - it dequeues the item, updates metadata, handles segment cleanup, and voids the lock. The item is fully removed from the main queue.

4. **Priority From Lock State**: The lock tracks `Priority` and `HeadSegment`. This enables priority preservation without parsing the item JSON.

## Testing

### Unit Tests (QueueActorTests.cs)

- Lock validation (LOCK_NOT_FOUND, INVALID_LOCK_ID, LOCK_EXPIRED)
- Error handling scenarios
- Edge cases with expired locks

### Integration Tests (DeadLetterTests.cs)

- Full DLQ flow with HTTP REST API
- Full DLQ flow with gRPC API
- Priority preservation verification
- Concurrent operations
- Main queue unlocking after DLQ

## Monitoring & Operations

### Observability

Monitor DLQ usage to identify problematic message patterns:

```bash
# Check DLQ depth via Pop operations
curl -X POST http://localhost:5000/queue/my-queue-deadletter/pop

# Dapr actor state inspection (via Dapr HTTP API)
curl http://localhost:3500/v1.0/actors/QueueActor/my-queue-deadletter/state/metadata
```

### DLQ Processing Strategies

1. **Manual Inspection**: Operators pop items from DLQ to investigate root cause
2. **Automated Retry**: Background job periodically attempts to reprocess DLQ items
3. **Archival**: Move old DLQ items to cold storage after N days
4. **Metrics**: Track DLQ rate as a health indicator

## Migration Guide

This feature is fully backward compatible. Existing queues continue to work without modification:

- `Pop()` works as before (no acknowledgement)
- `PopWithAck()` enables new locking behavior
- `DeadLetter()` only works with locked messages

No migration steps required for existing deployments.

## Related Features

- **Message Acknowledgement**: DLQ requires PopWithAck (lock-based acknowledgement)
- **Priority Queues**: DLQ preserves original message priority
- **Lock Extension**: Can extend lock TTL before deciding to deadletter

## Version History

- **v5.0** (2026-03-07): Initial release of Dead Letter Queue feature
