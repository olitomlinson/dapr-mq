# Feature: Soft Delete for External Segments

## Overview

The Soft Delete mechanism provides safe, crash-resistant deletion of offloaded segments from external storage. Instead of immediately deleting segments when they're loaded back into actor state, segments are queued for deletion with a timestamp and deleted after a configurable retention period (default 86400 seconds / 24 hours) by an Actor Timer.

**Key Characteristics:**
- **Timestamp-Based Queuing**: Segments marked for deletion with Unix timestamp
- **Configurable Retention**: Default 86400-second (24-hour) grace period (configurable via environment variable)
- **Actor Timer Cleanup**: Background timer processes deletions every 60 seconds
- **Crash-Safe**: Deletion queue persists in actor state, survives process crashes
- **Idempotent**: Handles already-deleted segments gracefully
- **Testable**: Zero-retention mode for integration tests via environment variable

## Problem Statement

**Race Condition in Immediate Deletion:**

When `LoadOffloadedSegmentAsync` loads an offloaded segment back into actor state, it previously deleted the external copy immediately:

```csharp
// OLD CODE - UNSAFE
await client.DeleteStateAsync("statestore", offloadKey);
```

If the process crashes **after** this delete but **before** the actor state is saved, data is lost—the segment is gone from external storage but not yet persisted in actor state.

**Solution:**

Queue segments for deletion in the same transaction as state changes, then delete asynchronously after a safety grace period.

## Use Cases

- **Production Safety**: Protect against data loss during crashes or restarts
- **Recovery Operations**: 86400-second (24-hour) window to recover accidentally deleted segments
- **Debugging**: Segments remain in external storage for debugging before automatic cleanup
- **Testing**: Fast deletion mode (0-second retention) for integration tests

## Architecture

### State Structure

**New State Key**: `segment_deletion_metadata`

```csharp
{
  "pending_deletions": [
    {
      "segment_key": "offloaded_queue_1_seg_0_my-queue",
      "requested_at_unix_seconds": 1709596800.0
    },
    {
      "segment_key": "offloaded_queue_1_seg_1_my-queue",
      "requested_at_unix_seconds": 1709596820.0
    }
  ]
}
```

### Data Structures

```csharp
public record PendingDeletion
{
    public required string SegmentKey { get; init; }
    public required double RequestedAtUnixSeconds { get; init; }
}

public record SegmentDeletionMetadata
{
    /// <summary>
    /// List of pending deletions with timestamps.
    /// Using List instead of Queue for efficient filtering by timestamp.
    /// </summary>
    public List<PendingDeletion> PendingDeletions { get; init; } = new();
}
```

### Flow Diagram

```
1. LoadOffloadedSegmentAsync called
   ├─> Load segment from external store
   ├─> Save to actor state
   ├─> Update metadata (remove from offloaded range)
   ├─> Queue for deletion (with timestamp) ← NEW
   └─> SaveStateAsync (atomic commit)

2. Actor Timer fires every 60s
   ├─> Load deletion metadata
   ├─> Filter list by timestamp:
   │   ├─> Eligible: retention period elapsed
   │   └─> Not ready: retention period not elapsed
   ├─> Process eligible deletions:
   │   ├─> Delete from external store (success)
   │   ├─> Already deleted (remove from list)
   │   └─> Transient error (keep for retry)
   └─> SaveStateAsync (updated list = not ready + failed)
```

## Configuration

### Environment Variable

**`SEGMENT_DELETION_RETENTION_SECONDS`**

- **Type**: Integer
- **Default**: 86400 (24 hours, if not set or invalid)
- **Range**: 0+ seconds
- **Purpose**: How long to keep segments before actual deletion

**Examples:**

```bash
# Production mode - default 86400 seconds / 24 hours (don't set variable)
unset SEGMENT_DELETION_RETENTION_SECONDS

# Testing mode - immediate deletion
export SEGMENT_DELETION_RETENTION_SECONDS=0

# Custom retention - 7200 seconds / 2 hours
export SEGMENT_DELETION_RETENTION_SECONDS=7200
```

### Actor Timer Configuration

- **Timer Name**: `segment-cleanup-timer`
- **Interval**: 60 seconds (constant)
- **Durability**: Non-durable (re-registered on each actor activation)

## Implementation Details

### Key Methods

#### 1. QueueSegmentForDeletionAsync

**Purpose**: Queue a segment for deletion with current timestamp

```csharp
private async Task QueueSegmentForDeletionAsync(string segmentKey)
{
    // Load current deletion metadata
    // Check if already queued (avoid duplicates)
    // Create PendingDeletion with current timestamp
    // Enqueue and save to state
    // Note: State saved by caller's SaveStateAsync (atomic)
}
```

**Called by**: `LoadOffloadedSegmentAsync` (line 1065)

#### 2. OffloadSegmentCleanupAsync

**Purpose**: Actor Timer callback to process deletions

```csharp
private async Task OffloadSegmentCleanupAsync()
{
    // Load deletion metadata
    // Get retention period from environment
    // Filter list into:
    //   - Eligible: past retention period (process these)
    //   - Not ready: before retention period (keep as-is)
    // For each eligible deletion:
    //   - Attempt delete
    //   - If already deleted: remove from list
    //   - If transient error: keep in failed list for retry
    // Build updated list = not ready + failed
    // Save updated list
}
```

**Triggered by**: Actor Timer every 60 seconds

**Key Improvement**: Uses List filtering instead of Queue iteration, avoiding potential confusion about infinite loops and making the code more efficient by only processing eligible deletions.

#### 3. IsAlreadyDeletedError

**Purpose**: Detect if deletion failed because segment already deleted

```csharp
private static bool IsAlreadyDeletedError(Exception ex)
{
    // Check for "not found", "does not exist", "404" in exception message
    // Dapr returns RpcException with StatusCode.NotFound for missing keys
}
```

### Timer Registration

```csharp
protected override async Task OnActivateAsync()
{
    // ... existing initialization ...

    // Register cleanup timer (not durable, must re-register on each activation)
    await RegisterTimerAsync(
        ExternalSegmentCleanupTimerName,
        nameof(OffloadSegmentCleanupAsync),
        null,
        TimeSpan.FromSeconds(OffloadSegmentCleanupScanIntervalSeconds),  // Due time (first run)
        TimeSpan.FromSeconds(OffloadSegmentCleanupScanIntervalSeconds)); // Period (recurring)
}
```

## Error Handling

### Already-Deleted Segments

**Scenario**: Segment deleted manually or by previous cleanup run

**Handling**:
- Catch exception during `DeleteStateAsync`
- Check if error indicates "not found"
- If yes: remove from queue (idempotent cleanup)
- If no: re-queue for retry (transient error)

### Transient Errors

**Scenario**: Network error, state store unavailable

**Handling**:
- Catch exception
- Re-queue segment for next cleanup cycle
- Log warning for monitoring

### State Store Unavailable

**Scenario**: Cleanup runs but can't delete (state store down)

**Handling**:
- All deletions fail with transient errors
- All segments re-queued
- Next cleanup cycle (60s later) retries
- Queue grows but remains bounded in actor state

## Design Decisions

### Why List Instead of Queue?

**Original concern**: Using Queue with dequeue/re-enqueue pattern could appear to create an infinite loop risk.

**Reality**: While the original Queue-based implementation was technically safe (iterating a snapshot, not the live queue), using a **List with filtering** provides:

1. **Clarity**: Explicitly separates eligible vs. not-ready deletions upfront
2. **Efficiency**: Only processes eligible deletions instead of iterating all items every 60s
3. **Intent**: Code clearly shows "filter by timestamp, then process" logic
4. **Maintainability**: Easier to understand and modify filtering logic

**Implementation**:
```csharp
// Filter list by timestamp
var eligibleDeletions = pendingList
    .Where(pd => (currentTimestamp - pd.RequestedAtUnixSeconds) >= retentionSeconds)
    .ToList();
var notReadyDeletions = pendingList
    .Where(pd => (currentTimestamp - pd.RequestedAtUnixSeconds) < retentionSeconds)
    .ToList();

// Process only eligible deletions
foreach (var pendingDeletion in eligibleDeletions) { ... }

// Rebuild list: not ready + failed
updatedList.AddRange(notReadyDeletions);
updatedList.AddRange(failedDeletions);
```

This approach makes the loop termination condition obvious and avoids any cognitive load around "can this loop become infinite?"

## Benefits

### 1. Atomicity
Deletion queued in same transaction as state save. If crash occurs, both operations roll back together.

### 2. Crash Safety
Deletion queue persists in actor state. After crash/restart, pending deletions resume processing.

### 3. Grace Period
86400-second (24-hour) retention provides safety buffer:
- Recover from accidental deletes
- Debug production issues
- Manually intervene if needed

### 4. Idempotency
Already-deleted segments handled gracefully. No errors if segment missing.

### 5. Performance
No synchronous deletion. Pop operations complete faster without waiting for external delete.

### 6. Testability
Zero-retention mode enables fast integration tests without waiting 86400 seconds (24 hours).

### 7. Observability
Detailed logging:
- Queue size on each addition
- Cleanup summary (success/failed/not ready counts)
- Age of segments when deleted
- Retention period in use

## Trade-offs

### Storage Cost
Deleted segments remain in external storage for retention period (default 86400 seconds / 24 hours).

**Impact**: Minimal for typical workloads. Only segments actively being consumed accumulate.

### Cleanup Delay
Segments deleted up to 86400 seconds (24 hours) after loading.

**Impact**: Acceptable for storage cleanup. Segments are "garbage" once loaded into actor state.

### Additional State
Deletion metadata adds ~100 bytes + (~150 bytes × pending deletions).

**Impact**: Negligible. Typical actors have 0-5 pending deletions.

### Timer Overhead
Cleanup runs every 60 seconds.

**Impact**: Negligible CPU/network. Cleanup completes in <100ms for typical queue sizes.

## Monitoring

### Log Patterns

**Normal Operation (0s retention for testing)**:
```
DEBUG: Actor my-queue: Registered cleanup timer (60s interval)
DEBUG: Actor my-queue: Queued 'offloaded_queue_1_seg_0_my-queue' for deletion (list size: 1, retention: 0s)
INFO:  Actor my-queue: Processing 1 pending deletion(s) (retention: 0s, eligible: 1, not ready: 0)
DEBUG: Actor my-queue: Deleted 'offloaded_queue_1_seg_0_my-queue' from state store (aged 0s)
INFO:  Actor my-queue: Cleanup complete - Success: 1, Already deleted: 0, Failed: 0, Not ready: 0, Remaining: 0
```

**Normal Operation (86400s / 24h retention)**:
```
DEBUG: Actor my-queue: Queued 'offloaded_queue_1_seg_0_my-queue' for deletion (list size: 1, retention: 86400s)
INFO:  Actor my-queue: Processing 1 pending deletion(s) (retention: 86400s, eligible: 0, not ready: 1)
DEBUG: Actor my-queue: Segment 'offloaded_queue_1_seg_0_my-queue' not ready for deletion (86040s remaining)
INFO:  Actor my-queue: Cleanup complete - Success: 0, Already deleted: 0, Failed: 0, Not ready: 1, Remaining: 1
```

**Error Conditions**:
```
WARNING: Actor my-queue: Failed to delete 'offloaded_queue_0_seg_5_my-queue', re-queued for retry
ERROR:   Actor my-queue: Critical error in cleanup timer callback
```

### Metrics to Track

- **Queue Size**: `PendingDeletions.Count` per actor
- **Cleanup Success Rate**: `successCount / originalCount` per run
- **Not Ready Count**: Segments waiting for retention period
- **Failed Count**: Transient errors requiring retry

### Alerts

- **Queue Size > 100**: Possible state store issues
- **Success Rate < 95%**: State store availability problems
- **High Failed Count**: Network or state store degradation

## Testing

### Unit Tests

**Test 1**: Queue segment with timestamp
```csharp
[Fact]
public async Task QueueSegmentForDeletion_AddsKeyWithTimestamp()
{
    // Verify SetStateAsync called with PendingDeletion containing SegmentKey and timestamp
}
```

**Test 2**: Cleanup skips non-expired segments
```csharp
[Fact]
public async Task CleanupTimer_SkipsSegmentsNotPastRetention()
{
    // Mock segment requested 1 hour ago, retention = 24h
    // Assert DeleteStateAsync NOT called, segment remains in queue
}
```

**Test 3**: Cleanup processes expired segments
```csharp
[Fact]
public async Task CleanupTimer_ProcessesExpiredDeletions()
{
    // Mock segment requested 25 hours ago, retention = 24h
    // Assert DeleteStateAsync called, segment removed from queue
}
```

**Test 4**: Handles already-deleted segments
```csharp
[Fact]
public async Task CleanupTimer_HandlesAlreadyDeletedSegments()
{
    // Mock DeleteStateAsync to throw "not found" error
    // Assert no exception, key removed from queue
}
```

**Test 5**: Environment variable override
```csharp
[Fact]
public void GetRetentionSeconds_ReadsEnvironmentVariable()
{
    // Set SEGMENT_DELETION_RETENTION_SECONDS=7200
    // Assert returns 7200 instead of default 86400
}
```

### Integration Testing

**Fast Deletion Mode (CI/CD)**:
```bash
export SEGMENT_DELETION_RETENTION_SECONDS=0
# Run tests - segments deleted immediately after 60s timer
```

**Production Simulation**:
```bash
unset SEGMENT_DELETION_RETENTION_SECONDS
# Run tests - segments NOT deleted until 86400s (24h) elapsed
```

## Migration

### Deployment

**Step 1**: Deploy new version with soft-delete
- Actors register timer on activation
- Existing actors unaffected until next request

**Step 2**: Monitor logs
- Check for timer registration messages
- Verify cleanup runs every 60s

**Step 3**: Verify behavior
- Segments queued for deletion (not immediately deleted)
- Queue size remains reasonable (<10 per actor)

### Rollback Safety

- New state key `segment_deletion_metadata` added
- Old version ignores this key
- No breaking changes to existing state structure
- Worst case: orphaned segments in external storage (can be manually cleaned)

## Future Enhancements

### Configurable Cleanup Interval

Make cleanup interval configurable via environment variable:
```bash
export SEGMENT_CLEANUP_INTERVAL_SECONDS=300  # 5 minutes instead of 60 seconds
```

### Batch Deletion API

Use state store batch delete API for better performance:
```csharp
await client.DeleteBulkStateAsync("statestore", segmentKeys);
```

### Metrics Endpoint

Expose deletion queue metrics via actor method:
```csharp
Task<DeletionQueueMetrics> GetDeletionQueueMetrics();
```

### Manual Cleanup Trigger

Add actor method to force immediate cleanup:
```csharp
Task<CleanupResult> TriggerCleanup();
```

## References

- **Dapr Actor Timers**: https://docs.dapr.io/developing-applications/building-blocks/actors/howto-actors/#actor-timers-and-reminders
- **Implementation PR**: [Link to PR]
- **Related Feature**: [Segmented Queue Architecture](segmented-queue.md)
