# Feature: N-Queue Priority System

## Overview

The N-Queue Priority System is a flexible priority queuing mechanism that allows messages to be routed to different priority levels (0, 1, 2, ..., N). Messages with lower priority indices are always dequeued first, enabling fine-grained control over message processing order.

**Key Characteristics:**
- **Multiple Priority Levels**: Unbounded priority levels (0 to infinity)
- **Priority-Ordered Dequeuing**: Pop always drains lowest priority index first (0 → 1 → 2 → ...)
- **FIFO Within Priority**: Each priority level maintains FIFO ordering independently
- **Dynamic Queue Creation**: Queues are created on-demand when first item is pushed
- **Transactional Consistency**: Count map keeps track of items per priority level

## Use Cases

- **Task Scheduling**: Route urgent tasks to priority 0, background tasks to higher priorities
- **Message Routing**: Process critical alerts before normal notifications
- **Workload Management**: Balance resource allocation based on task importance
- **SLA Compliance**: Ensure high-priority requests are processed first
- **Emergency Handling**: Fast-track critical operations during incidents

## Architecture

### State Management

The system uses a dynamic multi-queue architecture with a centralized count map:

**Queue State Keys:**
- `queue_0`, `queue_1`, `queue_2`, ..., `queue_N`
- Each stores `List[Dict[str, Any]]` (FIFO queue of dictionaries)
- Created on-demand when first item is pushed to that priority
- Empty queues are cleaned up (set to empty list) after Pop

**Metadata Map State Key:**
- `metadata` (always exists after actor activation)
- Format: `{"0": 5, "1": 3, "2": 1}` (priority → count mapping)
- Used for efficient priority scanning without loading all queues
- Keys removed when count reaches zero (clean state)

### State Schema Example

```
Actor State:
{
  "metadata": {"0": 3, "1": 2, "5": 1},
  "queue_0": [
    {"id": 1, "message": "urgent"},
    {"id": 2, "message": "critical"},
    {"id": 3, "message": "high-priority"}
  ],
  "queue_1": [
    {"id": 4, "message": "medium"},
    {"id": 5, "message": "normal"}
  ],
  "queue_5": [
    {"id": 6, "message": "low-priority"}
  ]
}
```

## API Changes

### Push Method

**Signature:**
```csharp
Task<PushResponse> Push(PushRequest request)
```

**Parameters:**
- `request` (PushRequest): Contains:
  - `ItemJson` (string): JSON string to push onto the queue
  - `Priority` (int, optional): Priority level (0 = highest priority, 1, 2, ...). Default: 1

**Returns:**
- `PushResponse`: Contains `Success` (bool) - true if successful, false otherwise

**Behavior:**
1. Validates ItemJson is not empty
2. Validates priority is non-negative integer (>= 0)
3. Loads queue at `queue_{priority}_seg_{tailSegment}` (creates empty if doesn't exist)
4. Appends item to end of segment (FIFO)
5. Loads metadata map
6. Increments count for priority
7. Saves both segment and metadata map

**Example:**
```csharp
// Push high priority item
var response = await actor.Push(new PushRequest
{
    ItemJson = "{\"task\": \"critical_alert\"}",
    Priority = 0
});

// Push low priority item
response = await actor.Push(new PushRequest
{
    ItemJson = "{\"task\": \"cleanup_logs\"}",
    Priority = 5
});

// Push with default priority (1)
response = await actor.Push(new PushRequest
{
    ItemJson = "{\"task\": \"normal_task\"}"
    // Priority not specified - defaults to 1
});
```

### Pop Method

**Signature:**
```csharp
Task<PopResponse> Pop()
```

**Parameters:**
- None

**Returns:**
- `PopResponse`: Contains `ItemsJson` (List<string>) - list with single JSON string (empty list if no items available)

**Behavior (Priority-Ordered Draining):**
1. Loads metadata map
2. Returns empty list if map is empty (no queues have items)
3. Sorts priority keys numerically (0, 1, 2, ...)
4. For each priority with non-zero count (in order):
   - Loads `queue_{priority}_seg_{headSegment}`
   - Pops single item from front (FIFO)
   - Updates segment
   - Updates metadata map (decrements or removes key if zero)
   - Saves updated metadata map
   - Returns item in a list
5. Returns empty list if all queues are empty

**Example:**
```csharp
// Pop single item - will take from priority 0 first, then 1, then 2, etc.
var result = await actor.Pop();
// result.ItemsJson contains single JSON string or empty list
// Drains in order: [p0_item1, p0_item2, p0_item3, p1_item1, p1_item2]
```

## Examples

### Example 1: Basic Priority Usage

```csharp
# Push items with different priorities
await actor.Push({"item": {"id": 1, "msg": "critical"}, "priority": 0})
await actor.Push({"item": {"id": 2, "msg": "low"}, "priority": 5})
await actor.Push({"item": {"id": 3, "msg": "medium"}, "priority": 2})

# Pop all items - returns in priority order: 0, 2, 5
items = await actor.Pop(10)
# [{"id": 1, "msg": "critical"}, {"id": 3, "msg": "medium"}, {"id": 2, "msg": "low"}]
```

### Example 2: Priority Draining

```csharp
# Push 3 high priority, 2 medium priority items
await actor.Push({"item": {"id": 1}, "priority": 0})
await actor.Push({"item": {"id": 2}, "priority": 0})
await actor.Push({"item": {"id": 3}, "priority": 0})
await actor.Push({"item": {"id": 4}, "priority": 1})
await actor.Push({"item": {"id": 5}, "priority": 1})

# Pop 4 items - drains all priority 0, then 1 from priority 1
items = await actor.Pop(4)
# [{"id": 1}, {"id": 2}, {"id": 3}, {"id": 4}]

# Priority 0 completely drained, priority 1 has 1 item left
```

### Example 3: Sparse Priorities

```csharp
# Push to priorities 0, 5, 10 (skip 1-4, 6-9)
await actor.Push({"item": {"id": 1}, "priority": 0})
await actor.Push({"item": {"id": 2}, "priority": 5})
await actor.Push({"item": {"id": 3}, "priority": 10})

# Pop all - returns in order: 0, 5, 10 (skips empty priorities)
items = await actor.Pop(10)
# [{"id": 1}, {"id": 2}, {"id": 3}]
```

### Example 4: REST API Usage

```bash
# Push high priority item
curl -X POST http://localhost:8000/queue/my-queue/push \
  -H "Content-Type: application/json" \
  -d '{"item": {"task": "urgent"}, "priority": 0}'

# Push low priority item
curl -X POST http://localhost:8000/queue/my-queue/push \
  -H "Content-Type: application/json" \
  -d '{"item": {"task": "background"}, "priority": 5}'

# Pop items - priority 0 returned first
curl -X POST "http://localhost:8000/queue/my-queue/pop"
```

## Edge Cases

### 1. Default Priority
**Scenario:** Push without specifying priority
```csharp
await actor.Push({"item": {"data": "value"}})  # Defaults to priority 1 (priority 0 reserved for urgent items)
```

### 2. Sparse Priorities
**Scenario:** Priorities 0, 5, 10 exist (skip 1-4, 6-9)
**Result:** Pop serves in order 0 → 5 → 10, skipping empty priorities

### 3. Pop Depth Exceeds Total Items
**Scenario:** Pop(100) called with only 5 items total
**Result:** Returns all 5 items across all priorities

### 4. Invalid Priority
**Scenario:** Negative priority or non-integer
**Result:** Push returns False, error logged

### 5. Empty Queues
**Scenario:** All queues drained
**Result:** Pop returns empty list immediately (no queue loading)

### 6. Counts Map Desync
**Scenario:** Count > 0 but queue empty (shouldn't happen, but defensive)
**Result:** Pop fixes count and continues to next priority

### 7. Very Large Priority Values
**Scenario:** Priority = 1000
**Result:** Supported (unbounded), but may impact performance if many sparse priorities

## Performance Characteristics

### Time Complexity

**Push:**
- O(1) - constant time
- 2 state operations (queue + metadata map)

**Pop:**
- O(P) where P = number of non-empty priority levels checked to find first item
- Best case: O(1) when all items in priority 0
- Worst case: O(N) when items spread across N priorities

### State Operations

**Current (single queue):**
- Push: 1 state read, 1 state write
- Pop: 1 state read, 1 state write

**With N-queue system:**
- Push: 2 state reads (`queue_{priority}` + `metadata`), 2 state writes
- Pop: 1 state read (`metadata`) + P state reads (queues) + (P+1) state writes

### Latency Impact

**Push:**
- +1 state operation (~2-3ms additional latency)
- Minimal impact

**Pop:**
- Depends on priority distribution
- If most items in priority 0-2: ~3-5ms overhead
- If items spread across 10+ priorities: 20-50ms overhead

### Recommendations

1. **Keep Priority Levels Small**: Use 0-5 for typical use cases
2. **Avoid Sparse Priorities**: Don't use priorities 0, 100, 1000 unless necessary
3. **Monitor Distribution**: Track which priorities are used most
4. **Consider Batching**: Batch pushes if possible to reduce state operations

## Migration and Compatibility

### Breaking Changes

- **State Schema**: Old `"queue"` key no longer used
- **Actor Activation**: Initializes `metadata` instead of `"queue"`
- **No Backward Compatibility**: Old actors with `"queue"` state will not migrate automatically

### Migration Steps

If migrating from old single-queue system:

1. **Export Data**: Pop all items from old actors before upgrading
2. **Deploy New Version**: Update actor code
3. **Re-push Items**: Push items back with appropriate priorities
4. **Clean State**: Old `"queue"` keys will remain in state store (inactive, can be manually deleted)

### Future Actors

All new actors will use the N-queue system with default priority 1 (priority 0 reserved as a fast lane for urgent items).

## Implementation Details

### State Cleanup

**Empty Queues:**
- When a queue is drained during Pop, it's set to an empty list
- Dapr state store may clean up empty state keys automatically

**Zero Counts:**
- When a priority count reaches zero, the key is removed from `metadata`
- Keeps the metadata map clean and minimal

### Validation

**Priority Validation:**
```csharp
if not isinstance(priority, int) or priority < 0:
    return False
```

**Item Validation:**
```csharp
if not isinstance(item, dict):
    return False
```

### Logging

**Push:**
```
INFO: Pushed item to priority 2 queue for actor my-queue. Queue size: 5, Total counts: {'0': 3, '2': 5}
```

**Pop:**
```
INFO: Popped 3 items from priority 0 for actor my-queue
INFO: Popped 2 items from priority 1 for actor my-queue
INFO: Popped 5 total items for actor my-queue. Remaining counts: {'2': 1}
```

## Testing

### Unit Tests

30 unit tests cover:
- Priority push/pop operations
- Counts map updates
- Priority ordering
- Edge cases (negative priority, invalid items, etc.)
- FIFO within priority
- Empty queue handling

**Run tests:**
```bash
dotnet test
```

### Integration Tests

**Manual testing with Docker:**
```bash
# Start stack
docker-compose up

# Use curl examples
bash examples/curl_examples.sh
```

## References

- [QueueActor API Reference](../docs/API_REFERENCE.md)
- [Architecture Documentation](../docs/ARCHITECTURE.md)
- [README](../README.md)
- [Dapr Actors Documentation](https://docs.dapr.io/developing-applications/building-blocks/actors/)

## Version

- **Introduced**: v2.0.0
- **Last Updated**: 2026-02-27
