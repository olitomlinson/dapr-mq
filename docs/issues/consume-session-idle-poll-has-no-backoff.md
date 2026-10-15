# ConsumeSession polls an empty session every 200 ms with no backoff

**Status:** Open. Found while assessing [notify-on-enqueue](../../proposals/wont-do/notify-on-enqueue.md), as the cheap
alternative recommended for sessions. Not yet measured for session streams: the cost below is inferred from the
`Consume` measurements in [consume-stream-sdks.md](../../proposals/consume-stream-sdks.md).

**Severity:**
- **Medium** for deployments that run many concurrent session streams (`SessionQueueConsumer` slots × replicas),
  long `session_idle_timeout_seconds`, or long-running handlers.
- **Low** for a few session streams on busy sessions.

**Affects:** `ConsumeSession`
([DaprMQGrpcService.cs:761](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L761)), and therefore
every SDK's `SessionQueueConsumer` and the `pubsub.daprmq.*` components in sessions mode. The plain-queue `Consume`
stream already backs off.

---

## Summary

When a session has nothing to deliver, the `ConsumeSession` loop waits a flat `PollInterval` (200 ms) and calls
`DequeueLocked` on the session actor again
([DaprMQGrpcService.cs:1050](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L1050)). That is 5 actor
calls a second for each idle stream, for as long as it stays idle.

`Consume` had the same problem and fixed it: its idle wait doubles from 200 ms up to `MaxIdlePollInterval` (2 s), and
resets on a delivery
([DaprMQGrpcService.cs:1302-1310](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L1302-L1310)).
`ConsumeSession` was never given the same backoff.

## When a session stream sits idle

1. **Waiting to drain.** With nothing in flight, the stream polls until `session_idle_timeout_seconds` passes, then
   sends `SessionDrained`. The timeout defaults to `lease_seconds` (30 s), which means **≈150 polls per drained
   session**. `session_idle_timeout_seconds` has no upper bound
   ([DaprMQGrpcService.cs:780](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L780)), so a stream
   with a long timeout polls for that whole time.
2. **A handler is running and the session is empty.** When `outstanding > 0` and the window isn't full, the loop takes
   the empty branch every 200 ms for the handler's whole run. A 60 s handler at prefetch 1 is never in this state,
   because its window is full. At prefetch 10 with one message in hand, however, it makes ≈300 polls. The idle-drain
   timer doesn't apply here, so this can run indefinitely.

## Cost

For `Consume` at a flat 200 ms, [consume-stream-sdks.md](../../proposals/consume-stream-sdks.md) measured about 0.2%
of a core in the gateway and 0.4–0.5% in the workers for each idle stream. That put 200 idle streams at over a core.
Backoff cut it to about ⅛ of a core. Each `ConsumeSession` poll is the same kind of call (`DequeueLocked` on one
actor, plus the lock sweep), so the per-stream cost should be similar. Session churn makes it worse, because every
drained session spends its idle timeout polling at the full rate.

## Proposed fix

Port `Consume`'s backoff to the empty branch of `ConsumeSession`:

- Start at `PollInterval`, double after each empty dequeue up to `MaxIdlePollInterval` (2 s), and reset to
  `PollInterval` on any delivery.
- **Never wait past the next lease renewal.** Renewal runs inside the same loop
  ([DaprMQGrpcService.cs:953](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L953)), so cap each
  wait at the time until `lastRenewalAt + renewIntervalSeconds`. This is the same rule `Consume` applies to lock
  renewal.
- **Never wait past the idle-drain deadline.** When nothing is in flight, cap each wait at
  `emptySince + sessionIdleTimeoutSeconds`, so `SessionDrained` isn't sent up to 2 s late.
- **Wake on Nack.** A nack returns the item to the head of the session. Today, redelivery waits up to 200 ms. With
  backoff, it could wait up to 2 s. Wait on `refillSignal` in the empty branch as well as the full-window branch, and
  release it from the Nack handler, so a nacked item is redelivered straight away.
- Leave the full-window branch alone. It already wakes on acks (ADR 0001).

This is server-only. There are no proto or SDK changes.

## Trade-off

A message that arrives in a session after a lull waits for the next poll. Once the stream has fully backed off, that
is up to 2 s, 1 s on average, instead of up to 200 ms today. That matters more for sessions than for plain queues,
because a session's messages are often related and arrive in bursts with gaps between them. Options:

- **Same 2 s cap as `Consume`.** Simple and consistent. Recommended unless a session workload is latency-sensitive.
- **A lower cap for sessions (for example 1 s).** It keeps most of the CPU saving (4 polls in the first 2 s instead of
  10, then 1 a second) and halves the worst-case latency.

[Notify-on-enqueue](../../proposals/wont-do/notify-on-enqueue.md) removes this trade-off. This fix is the inexpensive step
before it.

## Tests

In [DaprMQGrpcServiceConsumeSessionTests.cs](../../server/tests/DaprMQ.Tests/DaprMQGrpcServiceConsumeSessionTests.cs),
with mocked `IQueueActorInvoker`:

- On an empty session, the gaps between successive `DequeueLocked` calls grow (with `PollInterval` and
  `MaxIdlePollInterval` set small for the test), and stay below the cap.
- A delivery resets the wait to `PollInterval`.
- A lease renewal still happens on time while the stream is fully backed off.
- `SessionDrained` arrives within one `PollInterval` of `session_idle_timeout_seconds`, not up to the cap late.
- A Nack while the stream is backed off causes an immediate `DequeueLocked`.
