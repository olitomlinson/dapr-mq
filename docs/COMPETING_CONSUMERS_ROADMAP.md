# Competing Consumers Implementation Roadmap

## Overview

Multi-phase implementation to enable concurrent consumers with best-effort FIFO. This document tracks remaining work.

---

## Phase 2: Actor Reminders (2-3 days)

**Goal:** Auto-expire locks via durable reminders instead of client-side checking.

**Changes:**
- Implement `IRemindable.ReceiveReminderAsync`
- PopWithAck: Register reminder `await RegisterReminderAsync($"lock-{lockId}", TimeSpan.FromSeconds(ttl))`
- Reminder callback: Delete `{lockId}-lock` and remove from lock registry
- ExtendLock: Unregister old reminder, register new with extended TTL
- Remove manual expiration checks from PopWithAck, Acknowledge

**Key Code:**
```csharp
public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
{
    if (reminderName.StartsWith("lock-"))
    {
        string lockId = reminderName.Substring(5);
        await StateManager.RemoveStateAsync($"{lockId}-lock");

        // Remove from lock registry
        var registry = await StateManager.TryGetStateAsync<List<string>>("_lock_registry");
        if (registry.HasValue && registry.Value.Contains(lockId))
        {
            registry.Value.Remove(lockId);
            await StateManager.SetStateAsync("_lock_registry", registry.Value);
        }

        await StateManager.SaveStateAsync();
    }
}
```

**Tests:**
- Reminder registered on lock creation
- Callback cleans up lock state
- ExtendLock updates reminder
- Locks auto-expire after TTL

---

## Phase 3: Move Items into Locks (3-4 days)

**Goal:** Dequeue items when locked (remove from queue head), store in lock state. Queue head always unlocked.

**Changes:**
- Update `LockState` record: add `string ItemJson` field
- PopWithAck: Dequeue item, store in lock state (decrement queue count)
- Acknowledge: Return ItemJson from lock (no queue modification needed)
- Reminder callback: Re-queue expired item to original queue at same priority

**Key Code:**
```csharp
// In PopWithAck after peeking
var (itemJson, priority, headSegment) = await PopWithPriorityAsync(); // Now actually dequeues
var lockData = new LockState {
    LockId = lockId,
    ItemJson = itemJson,  // NEW
    Priority = priority,
    // ...
};

// In ReceiveReminderAsync
var lockState = await StateManager.TryGetStateAsync<LockState>($"{lockId}-lock");
if (lockState.HasValue)
{
    await Push(new PushRequest {
        Items = new[] { new PushItem {
            ItemJson = lockState.Value.ItemJson,
            Priority = lockState.Value.Priority
        }}
    });
}
```

**Tests:**
- Push 10 → PopWithAck 5 → queue count = 5 (not 10)
- Lock expires → queue count = 6 (re-queued)
- Acknowledge returns ItemJson from lock

---

## Phase 4: Competing Consumer Flag (2-3 days)

**Goal:** Enable parallel locks via `AllowCompetingConsumers` flag.

**Changes:**
- Add `bool AllowCompetingConsumers` to `PopWithAckRequest`
- Add `bool CompetingConsumerMode` to `LockState`
- Replace `_current_lock_id` (string) with `_lock_registry` (List<string>)
- PopWithAck: If `AllowCompetingConsumers=false`, block if registry not empty (legacy)
- PopWithAck: If `AllowCompetingConsumers=true`, create lock without blocking (new)

**Key Code:**
```csharp
// Check for blocking
var lockRegistry = await StateManager.TryGetStateAsync<List<string>>("_lock_registry");
if (!request.AllowCompetingConsumers && lockRegistry.HasValue && lockRegistry.Value.Count > 0)
{
    return new PopWithAckResponse { Locked = true };
}

// Add to registry
var registry = lockRegistry.HasValue ? lockRegistry.Value : new List<string>();
registry.Add(lockId);
await StateManager.SetStateAsync("_lock_registry", registry);
```

**Tests:**
- Two PopWithAck (`AllowCompetingConsumers=true`) both succeed with different items
- Legacy mode (`false`) blocks with 423 Locked
- Registry tracks multiple locks

---

## Phase 5: Production Hardening (2 days)

**Goal:** Metrics, validation, docs.

**Changes:**
- Add `GetMetrics()` to `IQueueActor`: total items, locked items, lock details
- Add `GET /queue/{id}/metrics` endpoint
- Validation: competing mode requires TTL >= 5 seconds
- Update docs: API_REFERENCE.md, ARCHITECTURE.md, CLAUDE.md
- Dashboard: add competing consumer toggle

**Tests:**
- Metrics endpoint accuracy
- Load test: 10 consumers × 100 items
- Mixed workload: legacy + competing, no deadlocks

---

## Implementation Summary

| Phase | Duration | Risk | Status |
|-------|----------|------|--------|
| 1. Named locks | 2-3 days | State migration | ✅ Complete |
| 2. Reminders | 2-3 days | Reminder lifecycle | ✅ Complete |
| 3. Lock items | 3-4 days | Re-queue logic | Pending |
| 4. Competing flag | 2-3 days | Backward compat | Pending |
| 5. Production | 2 days | Performance | Pending |

**Total:** 11-15 days

---

## Key Architectural Decisions

✅ **Phase 1:** Named locks (`{lockId}-lock`) + single lock tracking (`_current_lock_id`)
✅ **Phase 2:** Durable reminders replace client-side expiration
⏳ **Phase 3:** Items removed from queue when locked → head always available
⏳ **Phase 4:** Lock registry enables multiple concurrent locks
⏳ **Phase 5:** Observable, documented, production-ready

**No skip-locked algorithm needed** - removing items from queue when locked means head is always unlocked.
