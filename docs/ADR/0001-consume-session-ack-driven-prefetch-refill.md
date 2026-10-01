# ADR 0001: ConsumeSession refills the prefetch window on acks, not only on a timer

- **Status:** Accepted
- **Date:** 2026-10-01

## Context

`ConsumeSession` ([DaprMQGrpcService.cs](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)) runs a
poll loop per session stream. Each iteration dequeues up to `prefetchCount - outstanding` items. When the window was
full it slept a fixed 200 ms, and acks arriving during that sleep only decremented `outstanding`. Nothing woke the
loop, so the window was refilled at most once every 200 ms.

The client therefore stalled whenever `prefetch × handler time < 200 ms`. The `deep-session` perf profile
(4 sessions × 1,000 messages, 10 ms handler, prefetch 10) ran at 47.7% efficiency with a 97 ms p95 gap between
messages. At prefetch 1, a session was capped at about 5 messages/s whatever the handler speed.

A larger prefetch only moves the threshold, and costs more locked messages, more redelivery after a crash and more
client memory.

## Decision

Wake the poll loop when acks bring `outstanding` down to half the window (`prefetchCount / 2`, integer division).

- A `SemaphoreSlim(0, 1)` is released when an Ack or DeadLetter decrement lands exactly on the threshold. Decrements
  step by one, so it fires once per crossing.
- When the window is full, the loop waits on that signal with `PollInterval` as the timeout. When the queue is
  empty, it keeps the plain `PollInterval` delay, because an ack is no reason to look for new messages.
- The 200 ms is now an internal `PollInterval` property, so tests can set it high enough that only the signal can
  explain an early dequeue.

The threshold is half rather than every ack so that refills stay batched. Waking on every ack would turn each
refill into a one-message `DequeueLocked`, which is an actor call and a state save per message. Half the window
keeps batches of at least 5 at prefetch 10 and still leaves enough buffered to cover the dequeue round trip. At
prefetch 1 the threshold is 0, so the loop refills as soon as the single message is acked.

## Consequences

- `deep-session` (local CI suite run, 2026-10-01): efficiency 47.7% → 86.2%, wall clock 21.0 s → 11.6 s, message gap
  p95 97 ms → 0.9 ms.
- `steady-drain`, `session-churn`, `sdk-defaults` and `live-publish` throughput is unchanged, so the extra wakes add
  no visible actor-call overhead. `live-publish` claim latency p50 (42 → 179 ms) and delivery p95 (595 → 790 ms)
  were higher in a single after run. That profile is bound by the empty-queue poll this change doesn't touch, and
  its before runs varied widely, so this is likely noise but is not yet proven.
- The worst case is no worse than before: if the signal never fires, the `PollInterval` timeout refills as it
  always did. A stale release costs one extra loop iteration.
- The `outstanding <= prefetchCount` bound is unchanged (C-04 passes).
- Server-side only, so every SDK benefits without a release.

## Not addressed

- Picking up newly published messages on an empty session is still the 200 ms poll (`live-publish` delivery
  p50 ≈ 114 ms). Fixing that needs the queue actor to notify open streams on enqueue, a separate and larger change.
- The default prefetch (10) is unchanged.
