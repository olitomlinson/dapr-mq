# ConsumeSession: refill the prefetch window when it's half drained

## Context

The `deep-session` perf profile (4 sessions × 1,000 messages, 10 ms settle, prefetch 10) ran at **47.7%
efficiency**: 21.0 s against an ideal of 10 s, with a **97 ms p95 gap between messages**. Every other CI
profile had gaps of about 0 ms.

The cause is the ConsumeSession poll loop in
[DaprMQGrpcService.cs](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs). Each iteration
computes `capacity = prefetchCount - outstanding` ([:747](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L747)).
When the window is full, `capacity` is 0, so it skips the dequeue and falls through to
`await Task.Delay(200ms)` ([:815](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L815)).
Acks arriving during that sleep only decrement `outstanding` ([:673](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L673),
[:699](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L699)). Nothing wakes the loop, so the
window is refilled at most once per 200 ms.

The client therefore stalls whenever **prefetch × settle < 200 ms**:

| Case | Work per window | Effect today |
|---|---|---|
| deep-session (10 × 10 ms) | 100 ms | ~100 ms idle per window, throughput roughly halved |
| prefetch 1, any handler | one message | at most ~5 messages/s per session, whatever the handler speed |
| steady-drain (10 × 100 ms) | 1 s | unaffected |

The prefetch size isn't the problem. A larger window only moves the threshold, and costs more locked
messages, more redelivery after a crash and more client memory. The fix is to refill based on acks, not
on a timer.

## Change

Wake the loop when acks bring `outstanding` down to **half the window or below**
(`outstanding <= prefetchCount / 2`, integer division). The server then fetches the next batch while the
client still has messages in hand.

Why half, not on every ack: waking on each ack would turn each refill into a one-message `DequeueLocked`,
which is an actor call and a state save per message where today there's one per batch. Half the window
keeps dequeues batched (≥ 5 at prefetch 10) and still leaves enough buffered to cover the dequeue's round
trip. At prefetch 1 the threshold is 0, so the loop refills as soon as the single message is acked.

### Implementation (all inside `ConsumeSession`)

1. Add a `SemaphoreSlim refillSignal = new(0, 1)` next to `outstanding`.
2. In the reader task, after each `Interlocked.Decrement(ref outstanding)` (Ack and DeadLetter), release the
   signal when the decrement lands exactly on the threshold:
   ```csharp
   if (Interlocked.Decrement(ref outstanding) == prefetchCount / 2 && refillSignal.CurrentCount == 0)
   {
       refillSignal.Release();
   }
   ```
   Decrements step by one, so "lands exactly on" fires once per crossing. The `CurrentCount` check keeps
   the max-count-1 semaphore from throwing `SemaphoreFullException` (a rare double release is harmless).
3. At the bottom of the loop, choose the wait according to why the loop is idle:
   - **Window full** (`capacity` was 0 this iteration): `await refillSignal.WaitAsync(PollInterval, cts.Token)`.
     It returns early when acks cross the threshold and otherwise times out as today.
   - **Queue empty** (dequeue returned nothing): keep the plain `Task.Delay(PollInterval)`. An ack isn't a
     reason to look for new messages.
4. Leave everything else alone. After a wake, the next iteration dequeues `capacity` items as it does now.
   If the wait times out, the 200 ms fallback still refills whatever capacity exists, so the worst case is
   no worse than today. Lease renewal and idle-drain checks run at the top of each iteration and only get
   checked more often. The `outstanding <= prefetchCount` invariant is unchanged.
5. Extract `200ms` into an internal `PollInterval` (default 200 ms) that tests can override. Without it,
   "woke early" is indistinguishable from "the 200 ms tick happened to land".

A stale release (the signal fired while the loop wasn't waiting on it) costs at most one extra loop
iteration, which sees `capacity` and either dequeues or waits again.

### Out of scope

- **Pickup of newly published messages when a session's queue is empty.** That's still the 200 ms poll,
  and it's what `live-publish` measures (delivery p50 114 ms). Fixing it needs the queue actor to notify
  open streams on enqueue; that's a separate, larger change.
- **Changing the default prefetch (10).**
- **SDK changes.** This is server-side only, so every SDK benefits without a release.

## Tests (TDD, in [DaprMQGrpcServiceConsumeSessionTests.cs](../server/tests/DaprMQ.Tests/DaprMQGrpcServiceConsumeSessionTests.cs))

Set `PollInterval` to something long (e.g. 5 s) so only the signal can explain an early dequeue:

1. **Half-drained window refills early.** Use prefetch 10 and a `DequeueLocked` mock that returns 10 items,
   then more. Ack 5 → a second `DequeueLocked` happens well before `PollInterval`, requesting `Count >= 5`.
2. **Small acks don't trigger a refill.** Ack 1 of 10 → no second `DequeueLocked` within ~500 ms, so
   batching is preserved.
3. **Prefetch 1 refills on its ack.** Ack the single message → immediate dequeue.
4. **DeadLetter frames count the same as acks** for the threshold.
5. **Empty queue:** acks don't cause extra `DequeueLocked` calls beyond one per crossing.
6. Existing tests (idle drain, renewal failure, items keep arriving) pass unchanged.

Then `dotnet test` in `server/tests/DaprMQ.Tests`. Before committing, run `./build-and-test.sh` and the
.NET SDK integration suite. C-04 (`C04_PrefetchCount_BoundsUnackedInFlightDeliveries`) checks that the
prefetch bound still holds.

## Perf tests

**No harness or profile changes are needed.** `deep-session` already exercises this path, and it's in
`--suite ci`. Expected movement:

| Profile | Before | Expected after |
|---|---|---|
| deep-session efficiency | 47.7% | ~90%+ |
| deep-session msg gap p95 | 97 ms | ~0–10 ms (one dequeue round trip) |
| deep-session wall clock | 21.0 s | ~11 s |
| steady-drain, session-churn, live-publish, sdk-defaults | — | unchanged (their windows already outlast 200 ms, or they're bound by the empty-queue poll) |

The suite reporting no change on the other four profiles is part of the check: it shows the extra wakes
didn't add actor-call overhead.

Two optional, manual extras:

- **Confirm the cause before implementing:** `./run-session-perf-test.sh --profile deep-session --prefetch 20`.
  At 20 × 10 ms = 200 ms the stall should mostly disappear, which confirms the timer is the cause.
- **Prefetch 1 after implementing:** `--profile deep-session --prefetch 1` is where the change matters most
  (today capped at about 5 messages/s per session). It's worth one manual before/after run. It doesn't need
  a CI profile, since deep-session already guards the mechanism.

**Sequencing:** the CI comparison table only shows "🟢 improved" once `main` has ≥3 `deep-session` runs
(see [sdks/dotnet/perf/README.md](../sdks/dotnet/perf/README.md#regression-check)). Land the perf workflow
on `main` first and let it build that baseline (a few pushes or nightlies), then open this change, so its PR
summary shows the before/after against a real baseline rather than "no baseline".
