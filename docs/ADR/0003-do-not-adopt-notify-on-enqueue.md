# ADR 0003: Don't adopt notify-on-enqueue; optimise for throughput, not idle latency

- **Status:** Accepted
- **Date:** 2026-10-15

## Context

`Consume` and `ConsumeSession` streams find new messages by polling the queue actor with `DequeueLocked`. When the
queue is empty, `Consume` backs off from 200 ms to 2 s. `ConsumeSession` polls every 200 ms.
[notify-on-enqueue.md](../../proposals/wont-do/notify-on-enqueue.md) proposed having the actor wake waiting streams instead,
in two phases:

1. **Gateway-local wakes.** An enqueue or nack wakes the streams on the gateway it passed through.
2. **Actor-side waiters.** The actor records which gateways have waiting streams, and a worker background service
   pushes wakes straight to those gateways over a new internal port.

What it would gain:

- **Under load, nothing.** A busy stream never idle-polls, because ADR 0001 refills its window on acks.
- **On an idle queue, about 1 s of first-message latency.** A message that arrives after a quiet spell is picked up in
  a few ms instead of ≈1 s on average (2 s worst case) for `Consume`, or ≈114 ms p50 for `ConsumeSession`.
- **A small amount of idle CPU,** about 0.1 core per 200 idle streams, measured in
  [consume-stream-sdks.md](../../proposals/consume-stream-sdks.md).

What phase 2 would cost:

- A new internal port, with pod-to-pod traffic outside Dapr (so no mTLS).
- A NetworkPolicy, and `POD_IP` wiring in Helm and compose.
- In-memory waiter state on the queue actor, a background notifier on the worker, and a wake endpoint on the gateway.
- About seven new failure modes to design, test and maintain, even though each one is bounded.
- A worse worst case: the proposal raised the idle fallback poll to 15 s, so a lost wake (after a worker crash, say)
  could delay delivery by up to 15 s instead of 2 s.

Phase 1 is cheap, but on its own it covers only the enqueues that pass through the consumer's own gateway. With the
Helm default of 3 gateways, that is about a third. It still adds wake paths to the enqueue and nack handlers that
every later change has to keep in mind.

## Decision

We will not implement notify-on-enqueue, neither phase 1 nor phase 2. Consume streams keep discovering new messages
by polling with backoff.

DaprMQ is aimed at **high-throughput workloads** rather than workloads that need low latency on idle queues. For
throughput, what counts is how efficiently a busy stream settles and refills. That is covered by ack-driven refill
(ADR 0001), batched acknowledge, batched lock renewal and the prefetch window. Notify-on-enqueue improves none of
these. It only shortens the wait for the first message after a quiet spell, which background and batch processing
don't notice. That gain isn't worth the engineering effort, the new infrastructure surface or the extra failure modes.

## Consequences

- The latency from an idle queue to the first message stays at up to 2 s for `Consume` (≈1 s on average once fully
  backed off). Docs and SDK guidance should say so plainly rather than imply push delivery.
- Workloads that need sub-second delivery on idle queues, such as interactive request/response, are a poor fit. The
  options for them are keeping their queues busy, or tuning `MaxIdlePollInterval` down and accepting more idle CPU.
- Idle CPU is still kept in check by polling backoff. `ConsumeSession` doesn't have backoff yet, which is tracked in
  [consume-session-idle-poll-has-no-backoff.md](../issues/consume-session-idle-poll-has-no-backoff.md). That fix is
  consistent with this decision: it lowers cost and makes latency slightly worse.
- [notify-on-enqueue.md](../../proposals/wont-do/notify-on-enqueue.md) stays as a record of the design, marked as rejected.
- The "notify-on-enqueue" follow-ons in [consume-stream-sdks.md](../../proposals/consume-stream-sdks.md) and
  [continuous-receive.md](../../proposals/continuous-receive.md), and the "Not addressed" note in ADR 0001, are
  resolved by this decision rather than left open.

## When to revisit

Reopen this decision only if a concrete use case needs low latency on idle queues and can't be met by tuning the poll
interval. A typical case is a user-facing flow, with multiple gateways or topic fan-out, where a 1–2 s first-message
delay breaks a requirement. Phase 1 alone would also be worth reconsidering if single-gateway deployments become the
main target.
