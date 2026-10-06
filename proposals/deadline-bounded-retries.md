# Retry actor calls that never reached an actor, within the caller's deadline

## Context

When a gateway can't reach a worker, the request fails straight away with a generic error.
Every queue operation is an actor call from the gateway to a worker. If no worker is hosting
`QueueActor` (a rolling deploy, a crash, a placement hiccup), the call fails and the caller sees:

- **REST:** `500 {"error": "Internal error: <exception message>"}` ([QueueController.cs](../server/src/DaprMQ.ApiServer/Controllers/QueueController.cs)).
- **gRPC:** `INTERNAL "Internal error: <exception message>"` ([DaprMQGrpcService.cs](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)).

That is the same response a bug gets. A caller can't tell "try again in a second" from "this will
never work", so SDK users either fail on every worker blip or write their own retry loops. Those
loops are unsafe, because they can't tell which failures are safe to repeat.

Readiness probes don't cover this. Today the gateway's probe (`/health`) only shows that the
process is up. Even a probe that understood Dapr would only decide whether a gateway gets traffic.
It wouldn't help a request that is already in flight when a worker goes away, or one arriving during
the few seconds before a probe notices.

The tempting fix is for the gateway to retry every actor call until it succeeds, so callers just
wait. That breaks in two ways:

- **Some failures are ambiguous.** If a call times out or the connection drops mid-call, the
  actor may already have run it. Repeating it then has side effects:
  - **Enqueue:** a duplicate item, unless the item has an idempotency key.
  - **Dequeue / DequeueLocked:** a second item is taken while the first response is lost. A plain
    dequeue loses the first item outright; a locked one leaves it stuck until its lock expires.
  - **Acknowledge, ExtendLock, DeadLetter:** a confusing `LockNotFound`, because the first attempt
    worked.
- **Waiting without a limit makes outages worse.** Requests pile up on the gateways and hold their
  connections. nginx, the ingress or the client times out anyway, while the gateway keeps retrying
  work nobody is waiting for. When the workers come back, they get all of it at once.

This proposal gives callers the "it waits, up to my timeout" behaviour, without those risks.

## Principle: classify every failure, retry only one class

Every actor-call failure falls into exactly one class:

| Class | Meaning | Examples | Retry? |
|---|---|---|---|
| **Not delivered** | The call certainly didn't reach an actor, so it had no side effects | no host registered for the actor type; placement table not ready; connection refused before the request was sent | **Yes**, any operation, within the deadline |
| **Unknown** | The call may have reached the actor | timeout after the request was sent; connection reset mid-call; the worker crashed during the call | Only if the operation is idempotent (below) |
| **Rejected** | The actor ran and answered with a DaprMQ `ErrorCode` | `QueueEmpty`, `Locked`, `LockNotFound`, `ValidationError` | **Never**. Already mapped to 204, 423, 410 and 400 today |

The whole design depends on telling "not delivered" from "unknown" correctly. A failure that is
actually unknown but gets classed as not delivered turns into duplicates or lost messages. So the
classification must be built from observed daprd behaviour, not assumed (phase 0). When in doubt,
the answer is **unknown**.

## Changes

### Phase 0: catalogue daprd's actual errors

Use a Testcontainers stack shaped like production, with a gateway (`REGISTER_ACTORS=false`) in front of a separate
worker, to cause each failure mode, and
record exactly what the gateway's `DaprClient` / actor proxy throws. That means the exception type,
the gRPC status code and the daprd error code:

1. no workers ever started;
2. every worker stopped while idle;
3. a worker killed partway through a long call (a large bulk dequeue);
4. placement stopped;
5. a worker sidecar up, with its app hung (the app's port accepts connections but never answers).

The output is a classification table checked into the invoker's tests, one row per mode. Every
later phase is built on it, and a Dapr upgrade that changes an error shape fails a test rather than
quietly changing what gets retried.

### Phase 1: surface the class (server, no retries yet)

In `QueueActorInvoker` ([DaprActorInvoker.cs](../server/src/DaprMQ.Interfaces/DaprActorInvoker.cs)),
catch invocation failures and classify them using the phase-0 table. Return two new `ErrorCode`
values ([Models.cs](../server/src/DaprMQ.Interfaces/Models.cs)) instead of letting the exception
reach the controllers' catch-all:

| ErrorCode | HTTP | gRPC | Response marker |
|---|---|---|---|
| `Unavailable` (not delivered) | `503` + `Retry-After: 1` | `UNAVAILABLE` | `daprmq-delivery: not-delivered` |
| `DeliveryUnknown` | `504` | `UNKNOWN` | `daprmq-delivery: unknown` |

The marker goes in a response header for REST and in trailers for gRPC. It tells SDKs which rules
apply without parsing messages. Controllers keep using the existing "check `ErrorCode.HasValue`
first" pattern; these are two more entries in the mapping.

This phase is useful on its own. Callers and dashboards can finally tell a worker outage from a bug,
and the controllers' 500s go back to meaning bugs.

### Phase 2: bounded retry of not-delivered calls (server)

The gateway retries **not-delivered** failures for every operation, because a call that never
reached an actor is always safe to repeat. Retries stop at whichever comes first:

- **the caller's deadline.** gRPC: `context.Deadline`. REST: a `daprmq-timeout` request header
  (milliseconds), since HTTP has no standard deadline. The SDKs send it from their own per-call
  timeout.
- **the server's cap,** `ACTOR_RETRY_MAX_SECONDS`, default 30. Without a caller deadline, the cap
  alone applies. It stops a request with no deadline from holding a connection indefinitely.
- **the caller going away** (`HttpContext.RequestAborted` / `context.CancellationToken`).

Backoff: exponential from 100 ms, capped at 2 s, with full jitter, so the requests queued during an
outage don't all hit the returning workers at the same moment. If the deadline runs out, the caller
gets the phase-1 `Unavailable` response. **Unknown** failures are never retried on the server: the
server can't know whether the caller has an idempotency key it trusts, so it returns
`DeliveryUnknown` and leaves the decision to the SDK.

Streams (`ConsumeSession`) are out of scope. `SessionQueueConsumer` already reconnects on its own
loop.

**Dapr Resiliency, considered and not chosen as the main mechanism.** Dapr's Resiliency resource
can retry actor calls by target. It decides from Dapr's error codes, though, not from whether the
call was delivered, and a policy applies to all methods of a target. That would retry ambiguous
dequeues. Its **circuit breaker** may still be worth adding, so a gateway stops sending calls to a
dead worker sooner. That's an open question below.

### Phase 3: deadline-bounded retries in the SDKs

Each SDK gets one retry policy, specified once in `sdks/testing` and implemented the same way in
.NET, Python, TypeScript and Java:

| Failure | Retry? |
|---|---|
| `Unavailable` / `daprmq-delivery: not-delivered` | yes, any operation |
| Connection refused, or a DNS failure before anything was sent | yes, any operation |
| `DeliveryUnknown`, or a connection lost after sending, for **Enqueue where every item has an `IdempotencyKey`** | yes. The actor de-duplicates on the key, and the markers last 24 h (`IDEMPOTENCY_KEY_TTL_SECONDS`), far longer than any retry |
| `DeliveryUnknown` for anything else | **no**. Throw a typed `DeliveryUnknownException` (or each language's equivalent) |
| Rejected (`QueueEmpty`, `Locked`, …) | no, as today |

Options (names are illustrative):

- `RetryTimeout`: how long one call may keep retrying, which is also the deadline sent to the
  server. Default 30 s. `0` turns retries off.
- `AutoIdempotencyKeys`: generate a key for any enqueued item that doesn't have one, so enqueue
  retries are always safe. **Off by default**: each key costs an extra state write
  (`idem_{key}`) per item, and that cost should be chosen, not imposed. Documented as the
  recommended setting for producers that can't tolerate duplicates.

The user experience is that calls wait through a worker restart, up to `RetryTimeout`. They fail
with a clear, typed error if it takes longer, and they never repeat an operation that might have
already happened.

### Relationship to readiness

Readiness and retries do different jobs, and readiness is left to its own proposal. Readiness moves
traffic off a broken gateway and holds new pods back until they can serve. Retries carry in-flight
requests across a short worker gap. One constraint for that proposal: if gateway readiness required a
live worker, then during a full worker outage every gateway would leave the Service. Callers would get
an immediate connection refused instead of waiting through the outage within their deadline.

## Out of scope

- **Waiting without a limit.** Every wait ends at the caller's deadline or the server cap.
- **Retrying unknown outcomes of non-idempotent operations.** That needs exactly-once semantics
  (for example, idempotency keys on dequeue and ack), which is a separate design.
- **`ConsumeSession` streams.** They reconnect in the consumer, as today.

## Open questions

1. **The phase-0 catalogue.** Exactly which daprd errors mean "not delivered" in Dapr 1.18? In
   particular, does daprd's own wait for the placement table time out with a distinct code?
2. **REST deadline header.** Is `daprmq-timeout` right? Should we also accept the
   `grpc-timeout`-style format?
3. **Server cap default.** 30 s matches typical ingress timeouts. It should stay below them, so the
   gateway gives up before the proxy in front of it does.
4. **Circuit breaker.** Add a Dapr Resiliency circuit breaker on the actor targets, alongside the
   application-level retries?
5. **Retrying Acknowledge.** A retried ack that gets `LockNotFound` could mean the first attempt
   worked, or that the lock expired and the item was redelivered. Can the actor tell these apart
   (for example, from a short-lived tombstone of recently acknowledged locks), so ack becomes
   safely retryable?

## Verification

Integration tests on a gateway + worker stack (as in phase 0):

- **Recovery within the deadline:** stop the workers, enqueue with a 20 s deadline, start the
  workers 5 s later. The enqueue succeeds and the queue holds exactly one item.
- **Outage longer than the deadline:** stop the workers and leave them down. The call returns
  `503` / `UNAVAILABLE` after the deadline, not earlier and not much later.
- **Unknown outcome of a dequeue:** kill the worker partway through a bulk dequeue. The caller gets
  `DeliveryUnknown`, there is no retry, and the item count afterwards shows one attempt.
- **Unknown outcome of a keyed enqueue:** cause the same kind of failure during an enqueue whose
  items have idempotency keys. The SDK retries, and the queue holds each item exactly once.
- **Caller cancels:** the server stops retrying when the caller cancels (assert from gateway logs or
  metrics).

Unit tests:

- the invoker's classification table, one row per phase-0 failure mode;
- the controllers' and gRPC service's mappings for the two new error codes;
- each SDK's retry policy matrix, using fake transports.

## Rollout

Phase 0 and phase 1 together are a small, safe change: error responses become accurate, and nothing
retries yet. Phase 2 is server-only and helps every client, including raw REST and gRPC callers.
Phase 3 follows per SDK, tracked as rows in the SDK integration matrix.
