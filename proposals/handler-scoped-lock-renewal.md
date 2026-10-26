# Renew only the locks a handler is working on

**Status:** proposed. Follows the KEDA load tests of 2026-10-18 (`examples/keda/test/load.sh`). It's
related to [per-queue-ack-aggregator.md](per-queue-ack-aggregator.md), which covers a different cost:
acks, not renewals.

## Context

### What the server does today

A Consume stream keeps up to `prefetch_count` messages delivered and locked. Every `lock_ttl / 3`, it
renews **every** delivered lock that hasn't been settled
([DaprMQGrpcService.cs](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs),
`RenewLocksAsync`). That includes messages sitting in the client's buffer that no handler has picked
up. The protocol documents this: "The server renews each outstanding lock every lock_ttl_seconds/3
for as long as the stream stays open" (`ConsumeStart.lock_ttl_seconds` in
[daprmq.proto](../server/src/DaprMQ.ApiServer/Protos/daprmq.proto)).

All five SDK `QueueConsumer`s default to `MaxActiveMessages = 100` (the window) and
`MaxConcurrentHandlers = 0` (unlimited).
- **With the defaults,** all 100 delivered messages run at once, so nothing sits idle and the window
  is busy work.
- **Once an application caps its handlers below the window,** the excess waits in the buffer. The
  server then renews those locks indefinitely, however long they wait.

### What that cost in the load test

On the kind cluster:
- **Setup:** 81 workers × 1 handler with a window of 100 held about 8,100 locks, renewed every 10s,
  which is about 810 lock renewals a second, each a state read plus a write.
- **Capacity:** the queue actor managed about 490 state reads a second in total, for everything.
- **What happened:**
  - Renewals fell behind, and locks expired.
  - Acks queued behind the renewals, timed out and expired too.
  - In one sampled minute the actor completed **0** `AcknowledgeBatch` calls.
- **Outcome:** the 100,000-message test never drained.
- **The same test with a window of 5:** it drained in 733s, with 0 settle timeouts.

Most of those 8,100 renewals kept messages locked that no handler would reach for minutes. Each
stream was handling about 1.7 messages a second, so a message waited about 60 seconds in its buffer.

### What Azure Service Bus does

- **Prefetch defaults to 0.**
- **A prefetched message is locked when it's fetched,** and its lock isn't renewed while it waits in
  the buffer. Only messages handed to a handler get automatic lock renewal, for up to
  `MaxAutoLockRenewalDuration` (5 minutes by default).
- **A lock that lapses in the buffer** makes the message available to other receivers. The stale copy
  then fails when the client tries to settle it.
- **Sizing guidance:** keep prefetch below what a client can process within the lock duration, about
  `lock duration ÷ 3 × messages per second`.

Sources:
- [Performance best practices](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-performance-improvements)
- [Azure/azure-sdk-for-java#20380](https://github.com/Azure/azure-sdk-for-java/issues/20380)
- [JasperFx/wolverine#4331](https://github.com/JasperFx/wolverine/issues/4331)

## Proposal

Make lock renewal on a Consume stream **follow handlers, not delivery**. The server renews a lock only
once the client says a handler has started its message. Locks still waiting in the buffer are not
renewed.

The SDKs opt in, so existing clients are unaffected.

### Protocol

```proto
message ConsumeStart {
  // ...existing fields...

  // RENEW_DELIVERED (default, today's behaviour): renew every delivered, unsettled lock.
  // RENEW_STARTED: renew only locks named in a Started frame; the rest run out their TTL.
  LockRenewal lock_renewal = 5;

  // With RENEW_STARTED: stop renewing a started lock after this many seconds (0 = no limit). The
  // equivalent of Service Bus's MaxAutoLockRenewalDuration, so a stuck handler can't hold a message
  // forever.
  int32 max_renewal_seconds = 6;
}

message ConsumeRequest {
  oneof payload {
    // ...existing...
    ConsumeStarted started = 5;   // client -> server: handlers began these messages
  }
}

message ConsumeStarted {
  repeated string lock_ids = 1;
}
```

- **`Started` is batched.** One frame carries every message whose handler started since the last one,
  so 1ms handlers don't produce a frame per message.
- **A new response payload, `ConsumeRevoked { repeated string lock_ids }`, is optional.** See
  "Unstarted locks" below.

### Server (gateway)

- `outstanding` records, for each lock, whether a handler has started it. The renewal tick renews only
  the started ones.
- **On `Started`:** mark each lock as started.
  - If a lock has less than one renewal interval left, renew it now, without waiting for the next tick.
  - If it has already lapsed, send `SettleFailed(LOCK_EXPIRED)` straight away, so the handler finds out
    before it does the work.
- **Unstarted locks that reach their expiry** are dropped from `outstanding`. That frees their window
  slot, so the stream refills; without this, a stream whose buffer lapsed would stall at a full
  window. Whatever then happens to the message is the choice under "Unstarted locks".
- **Settling and the end of a stream** are unchanged.

### SDKs (all five `QueueConsumer`s)

- Send `lock_renewal = RENEW_STARTED` when `MaxConcurrentHandlers > 0`. With unlimited handlers,
  every delivered message starts immediately, so the two modes behave the same.
- When a handler picks up a message, add its lock id to the next `Started` frame. Flush on a short tick
  (say 20ms), or immediately when the buffer was empty.
- **Before running a handler,** drop any buffered message whose lock has lapsed, or will lapse within
  a safety margin. Its window slot is already free on the server, and running it would only fail at
  ack. The expiry is computed from the time the client received the message plus the TTL, so it
  doesn't depend on the server and client clocks agreeing.
- `StrictOrder` (a window of 1 and one handler) is unaffected: the message is started as soon as it's
  delivered.

### Unstarted locks: expire or release

This is the main design choice. A buffered message that a handler never reaches has to go back to the
queue somehow.

| | **A. Let it expire** (as Service Bus does) | **B. Release it before it expires** |
|---|---|---|
| Mechanism | Its lock lapses, and the actor's lazy sweep restores the item to its position | Shortly before expiry (say at 80% of the TTL), the gateway returns all of a stream's unstarted locks in one call and sends the client `ConsumeRevoked` |
| Actor work | One sweep read and restore per item, plus the index | One new `ReleaseLockBatch` turn per batch: restore to position, no expiry index |
| `DeliveryCount` | **Increments**, so messages that are never processed can still be dead-lettered once they pass `MaxDeliveryCount` | Unchanged: a release isn't a delivery attempt |
| Time until another consumer can have it | Up to the full TTL, plus the sweep interval | About 80% of the TTL |
| New surface | None on the actor | A new actor method, `ReleaseLockBatch`, and the `ConsumeRevoked` frame |

**Recommendation: B.** A's `DeliveryCount` inflation is a correctness problem, not just a performance
one. A consumer with an oversized window could dead-letter healthy messages it never tried to
process. Service Bus has this behaviour too, and it's a known pitfall. B also hands buffered work to
other consumers sooner.

## Pros

- **Renewal cost follows active work, not window size.** It scales with handlers × streams. With 81
  streams × 1 handler that's 81 locks to renew, not 8,100, which is about 8 renewals a second instead
  of 810.
- **An oversized window stops being dangerous.** A misconfigured consumer costs some redelivery
  churn, not a collapsed queue.
- **Fairer distribution between consumers.** Today a slow replica can hold up to a full window of
  messages locked indefinitely. Under this proposal, messages it doesn't start go back to the queue
  for faster replicas.
- **Parity with Service Bus semantics,** which users of `QueueConsumer` will expect: its option names
  (`MaxActiveMessages`, `MaxConcurrentHandlers`) already follow Azure's.
- **Stuck handlers are bounded** by `max_renewal_seconds`. Today a hung handler's lock is renewed for
  as long as its stream stays open.
- **It's opt-in at the protocol level.** Existing clients keep today's behaviour.

## Cons

- **It turns renewals into redelivery churn; it doesn't remove the cost.** A window permanently larger
  than throughput × TTL now cycles unstarted messages: deliver, release or expire, deliver again. Per
  message that's still actor work: a dequeue and a release about every 24s, against a renewal every
  10s today. That's cheaper, but it isn't free, and the only real fix is still a correctly sized
  window.
- **A protocol change across the server and all five SDKs.** It adds a new request frame, two
  `ConsumeStart` fields and a response frame, plus `ReleaseLockBatch` on the actor under option B,
  with tests for each.
- **More moving parts on the hot path.** There's per-lock started state, the release timing, and the
  race between a `Started` frame and an expiry. Each is small, but each is a new way to lose a
  message's lock unexpectedly.
- **Handlers can lose their message just before starting it.** A message whose lock lapsed is dropped
  without running, or is revoked. Today it would always run. Applications that assume "delivered
  means I'll get to handle it" may need adjusting. That assumption was never guaranteed, but in
  practice today it holds.
- **An extra frame per batch of handler starts,** though `Started` batching keeps it small.
- **Tuning moves to the user.** Choosing the window now trades buffer churn against idle handlers,
  where today it only trades memory. The Service Bus formula helps, but it has to be documented and
  explained.

## Alternatives

1. **Better defaults and guidance only.** For example, when `MaxConcurrentHandlers > 0`, default
   `MaxActiveMessages` to about 2 × handlers, and document the Service Bus sizing formula. This is
   cheap and covers the load-test failure, since the window becomes small automatically. It doesn't
   protect against an explicitly oversized window, and it doesn't stop a slow replica holding
   messages.
2. **A server-side cap.** Clamp each stream's effective window to what renewals can sustain, or to a
   multiple of a new `max_concurrent_handlers` field. That protects every client without any SDK
   change, but the server can only guess at a good value.
3. **Cheaper renewals.** Keep each lock's expiry in the index rather than in the lock row, so a renewal
   is a small write and not a read and rewrite of the whole row. That cuts the cost per renewal but
   doesn't change how many there are, and it touches lock state everywhere.
4. **Client-side renewal.** The SDK sends explicit renew frames for the locks it's working on. That's
   equivalent to this proposal, but with more traffic and more renewal logic in five SDKs. Marking a
   message as started once, and letting the server renew it, is simpler.

## Recommendation

1. **Now:** alternative 1, smarter SDK defaults and documented window sizing. It's a small change,
   and it fixes the case that actually failed.
2. **Next:** this proposal with option B, if users need large windows with capped handlers, or if
   hoarding by slow replicas shows up in practice. Build it behind `RENEW_STARTED`, in the .NET SDK
   first, then port it to the other SDKs.

## Plan (for the proposal itself)

1. **Server, test-first:**
   - `ConsumeStart.lock_renewal`, and the `Started` frame
   - renew only started locks; on `Started`, renew a lock immediately if it's close to expiry
   - drop unstarted locks from `outstanding` at expiry, and refill
   - `max_renewal_seconds`
2. **Option B:** add `ReleaseLockBatch` to the actor (restore to position, no `DeliveryCount`
   increment, deindexed), plus the gateway's release-at-80% timer and the `ConsumeRevoked` frame.
3. **The .NET `QueueConsumer`:** send `Started`, drop lapsed or revoked messages before running a
   handler, and opt in when `MaxConcurrentHandlers > 0`.
4. **Measurement,** an A/B on kind with the queue reset and the actor hosts restarted before each run.
   Use:
   - 81 × 1 with window 100, which collapses today
   - 81 × 1 with window 5
   - 81 × 10 with window 20

   For each, record drain time, `ExtendLockBatch` calls and locks renewed a minute, releases or
   expiries, `DeliveryCount` at the end and settle timeouts.
5. **Port** to Go, Java, Python and TypeScript once the .NET results justify it.
