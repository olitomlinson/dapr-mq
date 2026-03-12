# Feature: Segmented Queue Architecture

## Overview

The Segmented Queue Architecture is a performance optimization that splits large priority queues into fixed-size segments (default: 100 items per segment). This prevents memory, network, and compute bottlenecks when queues grow large by ensuring operations only load/serialize small chunks of data at a time.

**Key Characteristics:**
- **Fixed-Size Segments**: Each segment stores max 100 items (configurable)
- **Transparent to API**: No API changes - works seamlessly with existing Push/Pop methods
- **Head/Tail Pointers**: Metadata tracks which segment to pop from (head) and push to (tail)
- **Automatic Allocation**: New segments created automatically when tail segment fills
- **Automatic Cleanup**: Empty head segments deleted immediately after draining
- **Independent per Priority**: Each priority level has its own segment numbering

## Use Cases

- **Large Queues**: Queues with hundreds or thousands of items
- **High-Throughput Systems**: Systems pushing/popping frequently with large backlogs
- **Memory-Constrained Environments**: Limit memory footprint per operation
- **Network Optimization**: Reduce serialization overhead to state store
- **Scalability**: Enable queues to grow to millions of items without performance degradation

## Architecture

### State Key Structure

**Before (Non-Segmented)**:
```
queue_0: [item1, item2, ..., item1000]  // All 1000 items in one key
```

**After (Segmented)**:
```
queue_0_seg_0: [items 0-99]      // First 100 items
queue_0_seg_1: [items 100-199]   // Next 100 items
queue_0_seg_2: [items 200-299]   // etc.
...
queue_0_seg_9: [items 900-999]   // Last 100 items
```

### Metadata Structure

```json
{
  "config": {
    "segment_size": 100
  },
  "queues": {
    "queue_0": {
      "metadata": {
        "count": 250,
        "head_segment": 0,
        "tail_segment": 2
      }
    }
  }
}
```

**Metadata Fields**:
- `config.segment_size`: Max items per segment (default: 100)
- `head_segment`: Segment to pop from (oldest items, front of queue)
- `tail_segment`: Segment to push to (newest items, back of queue)
- `count`: Total items across all segments for this priority

### Segment Lifecycle

#### Push Flow
```
1. Get tail_segment from metadata (e.g., segment 2)
2. Load tail segment: queue_0_seg_2
3. If len(segment) < 100: append item to segment
4. If len(segment) == 100:
   - Allocate new segment (tail_segment = 3)
   - Create empty segment: queue_0_seg_3
   - Append item to new segment
5. Save segment and updated metadata
```

#### Pop Flow
```
1. Get head_segment from metadata (e.g., segment 0)
2. Load head segment: queue_0_seg_0
3. Pop item from front of segment
4. If segment becomes empty:
   - If head_segment < tail_segment:
     - Delete empty segment (set to [])
     - Increment head_segment to next (1)
   - If head_segment == tail_segment:
     - Queue is now empty, delete metadata
5. Save updated segment (if not empty) and metadata
```

#### Segment Cleanup
- Empty head segments are deleted immediately (not saved back)
- Tail segment remains even when empty (it's the current push target)
- When last segment empties, queue metadata is removed

### State Schema Example

**Actor with 250 items in priority 0**:

```json
// State Store Keys:
{
  "queue_0_seg_0": [
    {"id": 1, "task": "process"},
    {"id": 2, "task": "send"},
    ... // 98 more items (100 total)
  ],
  "queue_0_seg_1": [
    {"id": 101, "task": "analyze"},
    ... // 99 more items (100 total)
  ],
  "queue_0_seg_2": [
    {"id": 201, "task": "archive"},
    ... // 49 more items (50 total)
  ],
  "metadata": {
    "config": {"segment_size": 100},
    "queues": {
      "queue_0": {
        "metadata": {
          "count": 250,
          "head_segment": 0,
          "tail_segment": 2
        }
      }
    }
  }
}
```

## Segment Offloading (Memory Optimization)

### Overview

**Introduced in**: v4.1.0

Segment Offloading is an additional memory optimization that moves "middle" full segments from the actor's state manager to the external Dapr state store. This reduces active memory footprint while maintaining FIFO guarantees and transparent API behavior.

**Key Benefits**:
- **Memory Reduction**: O(N items) → O(buffer_segments × 100 items)
- **Scalability**: Support much larger queues per actor without memory constraints
- **Transparent**: No API changes - offloading happens automatically
- **Configurable**: Tune memory vs latency with `buffer_segments` parameter

### How It Works

Only **middle segments** are offloaded - segments that are:
1. Full (100 items)
2. Not being consumed (segment_num > head_segment + buffer_segments)
3. Not being written to (segment_num < tail_segment)

**Example**: With `buffer_segments=1`, head=0, tail=5:
- Segment 0: Head (active) - KEEP in actor state
- Segment 1: Buffer - KEEP in actor state
- Segments 2, 3, 4: Middle full segments - OFFLOAD to state store
- Segment 5: Tail (active) - KEEP in actor state

**Result**: Only 3 segments (~300 items) in memory instead of 6 segments (~600 items) - 50% memory reduction.

### Configuration

Add `buffer_segments` to metadata config:

```json
{
  "config": {
    "segment_size": 100,
    "buffer_segments": 1
  },
  "queues": {
    "queue_0": {
      "metadata": {
        "count": 500,
        "head_segment": 0,
        "tail_segment": 4,
        "head_offloaded_segment": 2,
        "tail_offloaded_segment": 3
      }
    }
  }
}
```

**Configuration Parameters**:
- `buffer_segments`: Number of full segments to keep between head and offloaded segments (default: 1)
  - Higher values = more memory, less latency
  - Lower values = less memory, more latency
  - Minimum: 1 (always keep at least 1 buffer segment)

### State Store Keys

Offloaded segments use a predictable key naming convention including actor ID:

```
offloaded_queue_{priority}_seg_{segment_num}_{actor_id}
```

**Example**:
- Actor ID: `user-queue-123`
- Priority: 0
- Segment: 2
- Key: `offloaded_queue_0_seg_2_user-queue-123`

### Offloading Lifecycle

#### When Segments Are Offloaded

**Trigger**: After successful Push operation
**Conditions**:
1. Segment is full (100 items)
2. `segment_num > head_segment + buffer_segments`
3. `segment_num < tail_segment`

**Process**:
1. Save segment data to state store with offloaded key
2. Extend offloaded range (`head_offloaded_segment`/`tail_offloaded_segment`) in metadata
3. Delete segment from actor state manager
4. Continue (non-blocking on failure)

**Note**: Offloaded segments are always contiguous, so they're stored as a range (min/max) rather than a list to prevent unbounded metadata growth.

#### When Segments Are Loaded Back

**Trigger**: Before Pop/PopWithAck operations
**Condition**: `segment_num <= head_segment + buffer_segments`

**Process**:
1. Load segment data from state store
2. Save to actor state manager
3. Shrink offloaded range (increment `head_offloaded_segment`)
4. Delete from state store (cleanup)

#### Example Flow

**Setup**: Queue with 500 items (5 segments), buffer_segments=1

**Initial State**:
```
In Actor State:     segments 0, 1, 2, 3, 4
In State Store:     (none)
Memory Usage:       ~500 items
```

**After Offloading** (head=0, tail=4):
```
In Actor State:     segments 0, 1, 4
In State Store:     segments 2, 3
Memory Usage:       ~300 items (40% reduction)
Metadata:           head_offloaded_segment: 2, tail_offloaded_segment: 3
```

**After Pop 100 Items** (head=1, tail=4):
```
Segment 2 auto-loaded (within buffer distance)
In Actor State:     segments 1, 2, 4
In State Store:     segment 3
Memory Usage:       ~300 items
Metadata:           head_offloaded_segment: 3, tail_offloaded_segment: 3
```

### Performance Characteristics

#### Memory Savings

**Without Offloading**:
- Memory Usage: O(N items) where N = total items
- Example (10,000 items): ~10,000 items in memory

**With Offloading** (buffer_segments=1):
- Memory Usage: O(buffer_segments × 100 + active segments)
- Example (10,000 items): ~300 items in memory (97% reduction)

**Formula**:
```
Memory = (head_segment + buffer_segments + 1) × segment_size + tail_segment_items
       ≈ (buffer_segments + 2) × 100 items
```

#### Latency Trade-offs

**Offload Operation** (during Push):
- Additional time: ~10-50ms per segment
- Frequency: Once per 100 pushes (when segment becomes full)
- Non-blocking: Push always succeeds even if offload fails

**Load Operation** (during Pop):
- Additional time: ~10-50ms per segment
- Frequency: Once per 100 pops (when entering buffer zone)
- Impact: First pop of new segment slightly slower

**Tuning**:
- `buffer_segments=1`: Maximum memory savings, occasional load latency
- `buffer_segments=3`: Moderate memory savings, rare load latency (segments pre-loaded)
- `buffer_segments=5`: Minimal memory savings, almost no load latency

### Error Handling

**Philosophy**: Offloading is an optimization, not a requirement. Failures degrade gracefully.

**Offload Failure**:
- Log warning
- Keep segment in actor state (no memory savings this cycle)
- Continue push operation successfully
- State store unavailable → full memory mode

**Load Failure**:
- Log error
- Attempt to continue with existing state
- May affect pop operation if segment truly needed

**Never Blocks**: Push/Pop operations always complete, regardless of offload/load success.

### Monitoring

**Key Metrics to Track**:
1. Number of offloaded segments per actor
2. Offload success/failure rate
3. Load latency per segment
4. Memory usage before/after offloading
5. State store operation latency

**Logging**:
```
INFO: Offloaded segment 2 for priority 0 to state store (actor user-queue-123)
INFO: Loaded segment 2 for priority 0 from state store (actor user-queue-123)
WARNING: Failed to offload segment 3 for priority 0 (actor user-queue-123): connection timeout
```

### Implementation Details

**Helper Methods**:
```csharp
_get_offloaded_segment_key(priority, segment_num)           # Generate state store key
_get_buffer_segments(metadata)                              # Get configured buffer size
_get_offloaded_range(metadata, priority)                    # Get offloaded range (head, tail)
_offload_segment(priority, segment_num, segment_data)       # Move to state store
_load_offloaded_segment(priority, segment_num)              # Load from state store
_check_and_offload_segments(priority, metadata)             # Check eligibility, offload
_check_and_load_segments(priority, metadata)                # Check buffer, load
```

**Integration Points**:
- `Push()`: Calls `_check_and_offload_segments()` after save
- `Pop()`: Calls `_check_and_load_segments()` before access
- `PopWithAck()`: Calls `_check_and_load_segments()` before access
- `_return_items_to_queue()`: Calls `_check_and_load_segments()` before prepend

### Use Cases

**When to Enable** (default: always enabled):
- Large queues (>500 items)
- Memory-constrained environments
- Cost optimization (reduce actor memory footprint)
- Infrequent access patterns (batch processing)

**When to Increase buffer_segments**:
- High pop throughput requirements
- Latency-sensitive applications
- State store has high latency
- Willing to trade memory for performance

**When to Decrease buffer_segments** (not recommended below 1):
- Extremely memory-constrained
- Willing to accept higher latency
- State store has low latency
- Batch processing (latency not critical)

### Example: Memory Comparison

**Scenario**: Actor with 5,000 items across priority 0

**Without Segmentation** (v3.x):
```
Memory: 5,000 items (~500KB - 5MB)
State keys: queue_0
```

**With Segmentation** (v4.0):
```
Memory: 5,000 items (~500KB - 5MB)
State keys: queue_0_seg_0 ... queue_0_seg_49
```

**With Segmentation + Offloading** (v4.1, buffer_segments=1):
```
Memory: ~300 items (~30KB - 300KB)  [94% reduction]
State keys in actor: queue_0_seg_0, queue_0_seg_1, queue_0_seg_49
State keys in store: offloaded_queue_0_seg_2_actor_id ... offloaded_queue_0_seg_48_actor_id
```

### Migration from v4.0 to v4.1

**Breaking Change (v4.2+)**: Offload storage format changed from JSON string to raw UTF-8 bytes.

**Technical Details**:
- **Old approach**: `SaveStateAsync<string>` caused double JSON serialization (Queue → JSON string → JSON-encoded string)
- **New approach**: `SaveByteStateAsync` + `GetByteStateAsync` with raw UTF-8 bytes (Queue → UTF-8 bytes)
- **Result**: Eliminates redundant serialization pass, reduces CPU overhead and payload size

**Migration Impact**:
- Existing offloaded segments in state store become inaccessible
- Actors with offloaded data will encounter corruption errors on load attempts
- State store incompatibility between old and new versions

**Migration Options**:
1. **Drain queues**: Pop all items before upgrade to empty offloaded segments
2. **Accept data loss**: Deploy new version and clear state store (development/testing only)
3. **Clean deployment**: Start with fresh state store

**Performance Benefits**:
- 10-15% smaller payload size
- Reduced serialization CPU overhead
- Improved throughput for large queues with active offloading

**Automatic** (for new deployments):
1. Deploy v4.1
2. Existing actors activate with new config (buffer_segments=1)
3. Offloading starts automatically on next Push operations
4. Existing segments remain in actor state until consumed or offloaded

## API Changes

**No API changes** - segmentation is fully transparent:

```csharp
# Push works exactly the same
await actor.Push({"item": {"data": "value"}, "priority": 0})

# Pop works exactly the same
items = await actor.Pop()
```

The only visible change is in the state store - segments are created/deleted automatically.

## Examples

### Example 1: Basic Usage (Transparent)

```csharp
using DaprMQ;

# Push 150 items - creates 2 segments automatically
for i in range(150):
    await actor.Push({"item": {"id": i}, "priority": 0})

# Pop items - drains from segment 0, then segment 1
for _ in range(150):
    result = await actor.Pop()
    # Segments cleaned up automatically as they empty
```

### Example 2: Large Queue

```csharp
# Push 1000 items - creates 10 segments
for i in range(1000):
    await actor.Push({"item": {"task_id": i}, "priority": 0})

# Each operation only loads 100 items max
# Memory: ~10-50KB per operation vs 1-5MB for full queue
# Network: ~10-50KB serialized vs 1-5MB per save
```

### Example 3: Multiple Priorities with Segments

```csharp
# Each priority has independent segment numbering
for i in range(150):
    await actor.Push({"item": {"id": i}, "priority": 0})  # queue_0_seg_0, queue_0_seg_1
    await actor.Push({"item": {"id": i}, "priority": 1})  # queue_1_seg_0, queue_1_seg_1

# Pop drains priority 0 completely (both segments), then priority 1
```

### Example 4: Inspecting State via Dapr API

```bash
# View segment directly via Dapr actor state API
curl http://localhost:3500/v1.0/actors/QueueActor/my-queue/state/queue_0_seg_0

# View metadata
curl http://localhost:3500/v1.0/actors/QueueActor/my-queue/state/metadata
```

## Edge Cases

### 1. Segment Transition
**Scenario**: Pop drains all items from head segment
**Result**: Head segment deleted, head_segment pointer incremented, pop continues from next segment

### 2. Single Segment
**Scenario**: Queue has < 100 items
**Result**: Only one segment exists (head_segment == tail_segment), works normally

### 3. Returned Items Overflow
**Scenario**: PopWithAck expires and returns items to head segment, causing it to exceed 100 items temporarily
**Result**: Allowed - segment can temporarily exceed limit when items are returned (avoids complex splitting logic)

### 4. Empty Queue
**Scenario**: Pop called on empty queue
**Result**: No segments exist, metadata has no queue entry, returns empty list

### 5. Segment Cleanup During Pop
**Scenario**: Pop removes last item from head segment, but more segments exist
**Result**: Head segment not saved (deleted), head pointer incremented, metadata updated

## Performance Characteristics

### Memory Usage

**Before Segmentation**:
- Push: Load entire queue (N items) into memory
- Pop: Load entire queue (N items) into memory

**After Segmentation**:
- Push: Load max 100 items into memory
- Pop: Load max 100 items into memory

**Example**: Queue with 10,000 items
- Before: 1-5MB per operation
- After: 10-50KB per operation (100x reduction)

### Network Overhead

**Before Segmentation**:
- Save operation: Serialize entire queue (N items)

**After Segmentation**:
- Save operation: Serialize max 100 items

**Example**: Queue with 10,000 items
- Before: 1-5MB per save
- After: 10-50KB per save (100x reduction)

### Time Complexity

**Push**:
- Before: O(1) append + O(N) serialize
- After: O(1) append + O(1) serialize (fixed segment size)

**Pop**:
- Before: O(N) slice + O(N) serialize
- After: O(1) slice + O(1) serialize (fixed segment size)

### State Operations

**Push** (normal case):
- 2 state reads (metadata + tail segment)
- 2 state writes (segment + metadata)

**Pop** (normal case):
- 2 state reads (metadata + head segment)
- 2 state writes (segment + metadata)

**Pop** (segment transition):
- 2 state reads
- 1 state write (metadata only, empty segment not saved)

## Migration and Compatibility

### Breaking Changes

**IMPORTANT**: This is a **breaking change**. Segmented queues are incompatible with pre-v4.0 state format.

**Old Format** (v3.x):
```json
{
  "queue_0": [item1, item2, ...],
  "metadata": {"queues": {"queue_0": {"metadata": {"count": N}}}}
}
```

**New Format** (v4.0+):
```json
{
  "queue_0_seg_0": [items...],
  "metadata": {
    "config": {"segment_size": 100},
    "queues": {
      "queue_0": {
        "metadata": {
          "count": N,
          "head_segment": 0,
          "tail_segment": M
        }
      }
    }
  }
}
```

### Migration Steps

**Option 1: Fresh Start** (Recommended)
1. Drain all items from old actors (Pop until empty)
2. Deploy v4.0+ with segmented queues
3. Re-push items if needed

**Option 2: Manual State Migration** (Advanced)
1. Export state from old format using Dapr API
2. Transform to new format (split into segments)
3. Import to new state keys
4. Deploy v4.0+

**Option 3: Lazy Migration** (Future Enhancement - Not Implemented)
- Check for old `queue_N` keys on first access
- Migrate to segments on-the-fly
- Requires additional migration code

### Rollback

To rollback to v3.x:
1. Drain all segmented queues
2. Clear actor state
3. Deploy v3.x
4. Re-push items

## Implementation Details

### Segment Size Configuration

- Default: 100 items per segment
- Stored in: `metadata["config"]["segment_size"]`
- Applied: On actor activation
- Change impact: Only affects newly created segments

**Why 100?**
- Balances memory overhead (100 dicts ~10-50KB typically)
- Fast serialization (<10ms network + serialize time)
- Reasonable granularity for cleanup operations
- Low state operation overhead (2 reads, 2 writes per Push/Pop)

### Helper Methods

```csharp
_get_segment_key(priority, segment)  # Build segment key
_get_segment_size(metadata)          # Get configured size
_get_head_segment(metadata, priority)  # Get head pointer
_get_tail_segment(metadata, priority)  # Get tail pointer
_set_segment_pointers(metadata, priority, head, tail)  # Update pointers
```

### Validation

**Segment Size**: Must be positive integer (default: 100)
**Segment Numbers**: Auto-incrementing, starting from 0
**Head/Tail**: head_segment <= tail_segment always

### Logging

```
INFO: Pushed item to priority 0 segment 2 for actor my-queue. Segment size: 87, Total count: 287
INFO: Popped 1 item from priority 0 segment 0 for actor my-queue. Remaining count: 286
INFO: Popped 1 item from priority 0 segment 0 (now empty, moved to segment 1). Remaining count: 200
INFO: Popped last item from priority 0 for actor my-queue. Queue now empty.
```

## Testing

### Unit Tests

Comprehensive test coverage including:
- Segment allocation when full (100+ items)
- Cross-segment pop transitions
- Empty segment cleanup
- Large queues (350+ items, 4 segments)
- Priority + segment combinations
- PopWithAck with segmented returns
- Segment size configuration

**Run tests**:
```bash
dotnet test
dotnet test --filter "FullyQualifiedName~Segment"  # Segment-specific tests
```

### Integration Tests

**Manual testing**:
```bash
# Start stack
docker-compose up

# Push 200 items
for i in {1..200}; do
  curl -X POST http://localhost:8000/queue/test/push \
    -H "Content-Type: application/json" \
    -d "{\"item\": {\"id\": $i}}"
done

# Verify segments via Dapr API
curl http://localhost:3500/v1.0/actors/QueueActor/test/state/queue_0_seg_0
curl http://localhost:3500/v1.0/actors/QueueActor/test/state/queue_0_seg_1

# Pop all items
for i in {1..200}; do
  curl -X POST http://localhost:8000/queue/test/pop
done
```

## Best Practices

1. **Monitor Segment Count**: Track how many segments exist per priority
2. **Avoid Sparse Priorities**: Don't use priorities 0, 100, 1000 (creates many segment keys)
3. **Pop Regularly**: Prevent unbounded segment growth
4. **Use Default Segment Size**: 100 is optimized for most use cases
5. **Plan Migration**: Test migration strategy before upgrading

## Performance Benefits

| Metric | Before (Single Queue) | After (Segmented) | Improvement |
|--------|----------------------|-------------------|-------------|
| Memory per Push | N items | 100 items | 100x for N=10k |
| Memory per Pop | N items | 100 items | 100x for N=10k |
| Network per Save | N items serialized | 100 items serialized | 100x for N=10k |
| Pop Time Complexity | O(N) slice | O(1) slice | Linear to constant |
| Max Queue Size | Limited by memory | Unlimited | Scalable |

## References

- [QueueActor API Reference](../docs/API_REFERENCE.md)
- [Architecture Documentation](../docs/ARCHITECTURE.md)
- [N-Queue Priority System](./n-queue-priority-system.md)
- [Message Acknowledgement](./message-acknowledgement.md)
- [Dapr Actors Documentation](https://docs.dapr.io/developing-applications/building-blocks/actors/)

## Version

- **Segmented Queues**: v4.0.0
- **Segment Offloading**: v4.1.0
- **Last Updated**: 2026-02-28
