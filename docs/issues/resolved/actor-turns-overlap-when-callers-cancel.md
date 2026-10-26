# Actor turns overlap when callers cancel queued calls

**Status:** Resolved by Dapr 1.19.0 (2026-10-26). The runtime fix ([dapr/dapr#10842](https://github.com/dapr/dapr/pull/10842)) narrowed the overlap but didn't close it: the repro below, on runtime 1.19.0 with `Dapr.Actors` 1.18.10, still overlapped in 3 of 12 trials with reentrancy off and 4 of 12 with it on. The SDK's own turn lock ([dapr/dotnet-sdk#1937](https://github.com/dapr/dotnet-sdk/pull/1937), in `Dapr.Actors` 1.19.0) closes it: on runtime and SDK 1.19.0, 0 of 24 trials overlapped. DaprMQ's temporary turn guard ([#77](https://github.com/olitomlinson/dapr-mq/pull/77)) was removed in the Dapr 1.19.0 upgrade. Keep `Dapr.Actors` at 1.19.0 or later.

**Severity:** **Critical** without the guard. It silently corrupts queue actor state under load.

**Affects:** Dapr runtime 1.18.7 with `Dapr.Actors` 1.18.10, with actor reentrancy either on or off. In DaprMQ, any actor whose callers cancel calls. The queue actor is the one seen corrupted.

---

## Summary

When a caller cancels actor calls, through the `CancellationToken` passed to `ActorProxy.InvokeMethodAsync`, while those calls are queued behind the actor's running turn, the Dapr sidecar dispatches them to the app about 50ms later. **The earlier turn is still executing when they arrive.** Several turns then run on the same actor id at once, which breaks turn-based concurrency. Without cancellation, turns are serialised correctly.

DaprMQ cancels actor calls in two places:
- the gateway's settle calls, after a 10s timeout ([DaprMQGrpcService.cs:39](../../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L39))
- a Consume stream's outstanding calls, when the stream ends

Under load, slow queue actor turns (see [lock-sweep-reads-are-unbounded.md](lock-sweep-reads-are-unbounded.md)) make both happen often.

## What it did to DaprMQ

From the 100,000-message KEDA load test with 81 workers × 1 handler, before the guard:

- `LockCount` drifted from the actual `{lockId}-lock` rows by thousands. One run ended with a count of 2,788 against 24,524 rows, and another drove the count to **−1,274**. Concurrent turns read the metadata, then wrote it back over each other.
- In one run, 25,961 lock rows covered only 3,400 distinct items, and 20,946 of the rows weren't in any expiry bucket. They were never swept, and their items were never redelivered.
- "Count desync detected for priority 1, removing queue metadata" was logged 4–58 times per run. A turn found the head segment empty because another turn had just changed it, then deleted the priority's metadata. That stranded the remaining segments: 80,820 items in one case.
- KEDA kept replicas running on queues whose count no longer matched their contents.

The guard fixed it. With the same test, there were 0 desyncs, and `LockCount` equalled the lock rows: 2,901 vs 2,901 once idle. The guard held back 448 calls and dropped 109 that had been cancelled while waiting.

## How it was proven

1. **In DaprMQ, a per-actor in-turn counter.** Incremented in `OnPreActorMethodAsync`, decremented in `OnPostActorMethodAsync` or `OnActorMethodFailedAsync`, it found 2,549 calls that started while another turn on the same actor was in progress with reentrancy off, and 224 with it on.
2. **A standalone reproduction:** [olitomlinson/dapr-actor-concurrency-repro](https://github.com/olitomlinson/dapr-actor-concurrency-repro).
   - The actor method only waits. It has no state, makes no other calls, has no lifecycle hooks, and counts its own invocations from its first line to its `finally`.
   - Over 12 cancellation trials per mode, invocations overlapped in 8 with reentrancy off and 7 with it on, up to 7 and 9 at once. Without cancellation there were none.
   - A Gantt timeline shows three queued calls dispatched 49ms after the client cancelled them, while the earlier call still had half a second to run.

Two measurements were wrong at first, and are worth avoiding:
- **Inferring overlaps from log timestamps.** The .NET console logger writes from a background queue, so lines are stamped late under load.
- **HTTP middleware that marks a call finished after the response is sent.** The sidecar may already have sent the next call.

Both reported overlaps that didn't happen. Count inside the turn, or inside the method, instead.

## The workaround

[ActorTurnGuard.cs](../../../server/src/DaprMQ.ApiServer/Services/ActorTurnGuard.cs) is middleware on the actor hosts, added in [Program.cs:387](../../../server/src/DaprMQ.ApiServer/Program.cs#L387) when `REGISTER_ACTORS` is set. It's modelled on the `ActorTurnLock` in dotnet-sdk#1937:
- **It wraps every request the sidecar sends under `/actors/{type}/{id}/…`:** method calls, reminders, timers and deactivation. It holds one turn per actor until the SDK has finished the request, state save and response included.
- **A call with the same non-empty `Dapr-Reentrancy-Id`** as the chain holding the turn may enter (A → B → A).
- **A call whose request is aborted while it waits is dropped without running.**
- **It logs a warning naming #10835 each time it steps in.** Those warnings show how often the runtime tried to overlap turns.

## Removing the workaround

Once the runtime fixes #10835, or DaprMQ moves to an SDK release that includes #1937 or an equivalent:

1. Remove `app.UseActorTurnGuard(...)` from [Program.cs](../../../server/src/DaprMQ.ApiServer/Program.cs), along with `Services/ActorTurnGuard.cs` and `server/tests/DaprMQ.Tests/ActorTurnGuardTests.cs`.
2. Rerun the load test above, with an in-turn counter or the repro's checks, to confirm there are no overlaps and `LockCount` matches the lock rows.
3. Move this document to `docs/issues/resolved/`.
