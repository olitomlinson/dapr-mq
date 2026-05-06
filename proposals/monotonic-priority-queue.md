# Monotonic Priority Queue Implementation Plan

## Context

DaprMQ currently supports multi-priority queuing where lower priority numbers (0, 1, 2...) are processed first, and any priority can be pushed to at any time. This plan adds an alternate **monotonic priority mode** where:

- Priority must be explicitly set on every push (no default when monotonic mode enabled)
- First push initializes current priority (typically 0, but any valid priority allowed)
- Once messages are pushed to priority 1, popping naturally transitions from 0→1 after priority 0 empties
- **Configurable regression behavior**: When current priority is 1 and priority 0 is pushed:
  - **Reject** (default): Push fails with 400 error
  - **Orphan**: Push succeeds but items won't be processed unless priority reset
  - **DeadLetter**: Push succeeds, items routed to DLQ, returns special response code
- **Configurable priority skipping**: Whether priorities can skip values (0→5)
  - Default: false (sequential only, 0→1→2)
  - When enabled: 0→5 allowed
- Chain effect: priority can only advance forward, never regress (unless manually reset)

This enables use cases like versioned message processing where older versions are rejected/handled after newer versions arrive.

## Implementation Approach

### 1. Data Model Changes

**ActorMetadata unchanged** - CurrentPriority moved to MonotonicPriorityConfig

**Add PriorityMode enum** ([Models.cs](dotnet/src/DaprMQ.Interfaces/Models.cs)):
```csharp
public enum PriorityMode
{
    Standard = 0,
    Monotonic = 1
}
```

**Add MonotonicPriorityConfig** ([QueueActor.cs or Models.cs](dotnet/src/DaprMQ/QueueActor.cs)):
```csharp
public record MonotonicPriorityConfig
{
    public int? CurrentPriority { get; init; }  // Tracks current monotonic priority
    public string RegressionBehavior { get; init; } = "Reject";  // "Reject", "Orphan", "DeadLetter"
    public bool AllowPrioritySkipping { get; init; } = false;  // Allow non-sequential priorities (0→5)
}
```

**Extend MetadataConfig** ([QueueActor.cs:24-28](dotnet/src/DaprMQ/QueueActor.cs#L24-L28)):
```csharp
public record MetadataConfig
{
    public int SegmentSize { get; init; } = 100;
    public int BufferSegments { get; init; } = 1;
    public PriorityMode PriorityMode { get; init; } = PriorityMode.Standard;  // NEW
    public MonotonicPriorityConfig? MonotonicPriorityConfig { get; init; }  // NEW: only set when PriorityMode = Monotonic
}
```

**Add ConfigureQueue API** ([Models.cs](dotnet/src/DaprMQ.Interfaces/Models.cs)):
```csharp
public record ConfigureQueueRequest
{
    public PriorityMode? PriorityMode { get; init; }  // 0 = Standard, 1 = Monotonic
    public MonotonicPriorityConfig? MonotonicPriorityConfig { get; init; }  // Required when PriorityMode = Monotonic
}

public record ConfigureQueueResponse
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ResetPriorityRequest
{
    public int? TargetPriority { get; init; }  // null = reset to lowest available
}

public record ResetPriorityResponse
{
    public bool Success { get; init; }
    public int? NewCurrentPriority { get; init; }
    public string? ErrorMessage { get; init; }
}
```

**Extend PushResponse with ErrorCode** ([Models.cs:38-54](dotnet/src/DaprMQ.Interfaces/Models.cs#L38-L54)):
```csharp
public record PushResponse
{
    public bool Success { get; init; }
    public int ItemsPushed { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }  // NEW: "EXPLICIT_PRIORITY_REQUIRED", "PRIORITY_REGRESSION", "PRIORITY_REGRESSION_ORPHANED", "PRIORITY_REGRESSION_DLQ", "PRIORITY_SKIP_DISABLED"
    public string? DeadLetterQueueId { get; init; }  // NEW: DLQ actor ID when routed to DLQ
}
```

### 2. Push Validation Logic

**Modify QueueActor.PushInternal** ([QueueActor.cs:130-134](dotnet/src/DaprMQ/QueueActor.cs#L130-L134)):
```csharp
// After existing validation (priority >= 0)
if (metadata.Config.PriorityMode == PriorityMode.Monotonic)
{
    var config = metadata.Config.MonotonicPriorityConfig ?? new MonotonicPriorityConfig();

    // Validate explicit priority: reject default value (1) on first push
    if (!config.CurrentPriority.HasValue && priority == 1)
    {
        return (false, "EXPLICIT_PRIORITY_REQUIRED",
            "Monotonic queues require explicit priority. Default priority (1) not allowed on first push.");
    }

    // Check for priority regression (pushing to lower priority than current)
    if (config.CurrentPriority.HasValue && priority < config.CurrentPriority.Value)
    {
        switch (config.RegressionBehavior)
        {
            case "Reject":
                return (false, "PRIORITY_REGRESSION",
                    $"Cannot push priority {priority} when current priority is {config.CurrentPriority.Value}");

            case "Orphan":
                // Allow push but return special code indicating orphaned status
                Logger.LogWarning("Orphaned push: priority {Priority} when current is {Current}",
                    priority, config.CurrentPriority.Value);
                // Push succeeds, continue to normal logic, but will return PRIORITY_REGRESSION_ORPHANED code
                // (code returned after successful push)
                break;

            case "DeadLetter":
                // Route to DLQ instead
                string dlqId = $"{Id.GetId()}-deadletter";
                var dlqActor = _actorInvoker.GetQueueActor(dlqId);
                var dlqPush = await dlqActor.Push(new PushRequest { Items = items });
                return (true, "PRIORITY_REGRESSION_DLQ",
                    $"Items routed to DLQ due to priority regression", dlqPush.ItemsPushed, dlqId);

            default:
                return (false, "PRIORITY_REGRESSION", "Invalid regression behavior");
        }
    }

    // Check for priority skipping (when disabled, must be sequential)
    if (config.CurrentPriority.HasValue &&
        !config.AllowPrioritySkipping &&
        priority > config.CurrentPriority.Value + 1)
    {
        return (false, "PRIORITY_SKIP_DISABLED",
            $"Cannot skip from priority {config.CurrentPriority.Value} to {priority}. Sequential priorities required.");
    }
}

// ... normal push logic ...

// After successful push in Orphan mode, return special code
if (metadata.Config.PriorityMode == PriorityMode.Monotonic)
{
    var config = metadata.Config.MonotonicPriorityConfig ?? new MonotonicPriorityConfig();
    if (config.CurrentPriority.HasValue && priority < config.CurrentPriority.Value &&
        config.RegressionBehavior == "Orphan")
    {
        return (true, "PRIORITY_REGRESSION_ORPHANED",
            $"Items pushed to orphaned priority {priority}. Current priority is {config.CurrentPriority.Value}.", itemsPushed);
    }
}
```

### 3. Pop Auto-Advancement Logic

**Modify QueueActor.PopWithPriorityAsync** ([QueueActor.cs:441-444](dotnet/src/DaprMQ/QueueActor.cs#L441-L444)):
```csharp
foreach (var priority in sortedPriorities)
{
    // Existing pop logic...

    // NEW: After successful pop, check if priority exhausted
    if (metadata.Config.PriorityMode == PriorityMode.Monotonic)
    {
        var queueMeta = metadata.Queues[priority];
        if (queueMeta.Count == 0)  // Priority exhausted
        {
            // Advance CurrentPriority to next available priority
            var nextPriority = sortedPriorities.FirstOrDefault(p => p > priority && metadata.Queues[p].Count > 0);
            if (nextPriority > 0)
            {
                var updatedConfig = metadata.Config.MonotonicPriorityConfig with { CurrentPriority = nextPriority };
                metadata = metadata with
                {
                    Config = metadata.Config with { MonotonicPriorityConfig = updatedConfig }
                };
                await StateManager.SetStateAsync("metadata", metadata);
                await StateManager.SaveStateAsync();
            }
        }
    }
}
```

### 4. Configuration & Reset Endpoints

**Add IQueueActor methods** ([IQueueActor.cs](dotnet/src/DaprMQ.Interfaces/IQueueActor.js)):
```csharp
Task<ConfigureQueueResponse> ConfigureQueue(ConfigureQueueRequest request);
Task<ResetPriorityResponse> ResetCurrentPriority(ResetPriorityRequest request);
```

**Implement QueueActor.ConfigureQueue, ResetCurrentPriority, and QueueController endpoints** - See full plan for implementation details.

### 5. Error Handling

**Update QueueController.Push** to handle special error codes and success codes for ORPHANED and DLQ routing.

### 6. Lock Handling in Monotonic Mode

**Block priority advancement when locks exist** - Only advance CurrentPriority when LockCount == 0 to prevent re-queue issues.

**Lock expiry validation** - Prevent re-queue if current priority has advanced past lock priority.

## Test Strategy (TDD)

### Unit Tests (15 tests in QueueActorTests.cs)

1-7: Core monotonic behavior (explicit priority, sequential/skipping, advancement)
8-10: Regression behavior modes (Reject, Orphan, DeadLetter)
11-12: Priority reset
13-15: Lock handling

### Integration Tests (4 tests in MonotonicPriorityTests.cs)

E2E tests covering HTTP contract for regression rejection, auto-advancement, DLQ routing, and priority reset.

## Critical Files

- [QueueActor.cs](dotnet/src/DaprMQ/QueueActor.cs)
- [Models.cs](dotnet/src/DaprMQ.Interfaces/Models.cs)
- [IQueueActor.cs](dotnet/src/DaprMQ.Interfaces/IQueueActor.cs)
- [QueueController.cs](dotnet/src/DaprMQ.ApiServer/Controllers/QueueController.cs)
- [QueueActorTests.cs](dotnet/tests/DaprMQ.Tests/QueueActorTests.cs)
- [MonotonicPriorityTests.cs](dotnet/tests/DaprMQ.IntegrationTests/Tests/MonotonicPriorityTests.cs)

## Verification

1. Unit tests: `dotnet test` - all 15 new tests pass
2. Integration tests: `cd dotnet && docker build -t daprmq-api:test && ./build-and-test.sh`
3. Backwards compatibility verified
4. State migration handled by C# record defaults
5. Manual validation of each regression mode
