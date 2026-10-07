# Readiness and retries

## Context

DaprMQ runs in two shapes:

- **Combined** (single process, every SDK test fixture): one process serves the API
  (`ENABLE_API=true`) and hosts the actors (`REGISTER_ACTORS=true`).
- **Split** (Helm, docker-compose): **gateways** serve the API and host no actors.
  **Workers** host the actors and serve no API. Every queue operation is an actor call from a
  gateway's sidecar to a worker's sidecar.

Two problems share one root, which is that nothing tells clients, or Kubernetes, whether DaprMQ
can do its work.

**1. There is no readiness signal.** `/health` returns `healthy` as soon as Kestrel listens, before
the sidecar or placement is up. Helm uses it as the readiness probe, so pods get traffic before
they can serve it. Clients that need to wait for a fresh stack send a **probe enqueue** in a loop
until one succeeds: the perf harness, and the Python, TypeScript and Java test fixtures. Each probe
leaves a queue behind in the state store, and every fixture reimplements the loop.

**2. A worker gap fails every request straight away, with an error that says nothing.** When a
gateway can't reach a worker (a rolling deploy, a crash, a placement hiccup), the caller gets
`500 Internal error: <exception>` (REST) or `INTERNAL` (gRPC). That is the same response a bug gets.
SDK users either fail on every blip or write retry loops, and those loops are unsafe because they
can't tell which failures may be repeated.

### What we learned trying to fix these separately

An earlier attempt (commits `850e72c`, `6013181`, both since reverted) fixed readiness first, and
then made a gateway report ready only while a worker was ready. It found:

- **Gating gateways on workers is the wrong layer.** When every worker is down, every gateway
  leaves the Service at once, so callers get an immediate "connection refused". Retries that wait
  through the outage, within the caller's deadline, are what callers actually want.
- **Requiring *every* worker** would be worse still: each worker becomes a single point of failure
  for the whole API.
- **Finding workers through placement's table** (`GET /placement/state`) works, but has real costs:
  - it needs a control-plane install flag (`--metadata-enabled`);
  - only the Raft leader answers;
  - the gateway has to reach `dapr-system` on an undocumented port;
  - placement records the *sidecar's* IP. That is the app's IP only when they share a network
    namespace (a K8s pod), so docker-compose and the test stack had to be restructured.
- **Simply retrying everything until it works** duplicates enqueues and loses dequeues whenever the
  outcome of the first attempt is unknown.

So the two problems need one design: readiness that only describes what an instance can control,
retries that carry requests across worker gaps safely, and a separate, non-gating signal for whether
any worker is available.

## Design overview

| Concern | Mechanism | Used by |
|---|---|---|
| Is this process up? | `/health` (unchanged: liveness, independent of Dapr) | K8s liveness probe |
| Can this instance do its own job? | **Instance readiness**: `/health/ready` plus `grpc.health.v1`, service `daprmq.DaprMQ` | K8s readiness probes, load balancers |
| Can queue operations be served? | **Operations signal**: `grpc.health.v1` service `daprmq.DaprMQ.operations`, `/health/operations` | Monitoring, clients that choose to wait (fixtures, tooling). **Never** K8s readiness |
| Get a request through a worker gap | **Failure classification** plus **deadline-bounded retries** (server and SDK) | Every request |
| Stop scheduling actors onto a hung worker | **Dapr app health checks** on workers (to verify, below) | Dapr placement |

## 1. Instance readiness

Each instance reports only on what it can affect. Pull it out of rotation and the problem moves
with it, or goes away.

| Instance | Ready when |
|---|---|
| Combined | sidecar healthy, actor runtime `hostReady`, and it hosts `QueueActor` |
| Worker | same as combined |
| Gateway | sidecar healthy and `hostReady` (connected to placement). **Not** dependent on any worker |

The check is an `IHealthCheck` that asks the instance's own sidecar:

1. `DaprClient.CheckOutboundHealthAsync()` (`/v1.0/healthz/outbound`), so that it doesn't wait on the
   app channel. In combined mode the app is this process.
2. `GET {DAPR_HTTP_ENDPOINT}/v1.0/metadata`, read `actorRuntime.hostReady`, and, on actor hosts,
   check that `actorRuntime.activeActors[].type` contains `QueueActor`. Neither `DaprClient` nor
   `Dapr.Metadata` exposes these fields, so this read is plain HTTP. No actor is invoked or
   activated, and nothing is written.

It is served as `/health/ready` and as `grpc.health.v1.Health`, on both the `""` and
`daprmq.DaprMQ` services. Map it on every instance, workers included, and not inside the
`ENABLE_API` block. The health-check publisher period sets how quickly `Watch` notices a change:
use `Delay = 0, Period = 2 s` rather than the defaults (5 s / 30 s). Helm's readiness probes move to
`/health/ready`. Liveness stays on `/health`, so a placement outage doesn't restart every pod.

**Why the gateway doesn't depend on workers:** see the context section. Worker availability is
covered by sections 2–4, not by taking gateways out of rotation.

## 2. Failure classification

Every actor-call failure belongs to exactly one class:

| Class | Meaning | Examples | Retry? |
|---|---|---|---|
| **Not delivered** | The call certainly didn't reach an actor, so it had no side effects | no host for the actor type; placement table not ready; connection refused before sending | **Yes**, any operation, within the deadline |
| **Unknown** | The call may have reached the actor | timeout after sending; connection reset mid-call; worker crashed during the call | Only for idempotent operations (section 4) |
| **Rejected** | The actor ran and answered with a DaprMQ result | empty queue (`IsEmpty`), `LOCK_NOT_FOUND`, `LOCK_EXPIRED`, `SESSION_LOCKED`, `VALIDATION_ERROR` | **Never**. Already mapped (204, 410, 423, 400, …) |

Everything depends on telling "not delivered" apart from "unknown". A failure that is really
unknown but gets treated as not delivered becomes a duplicate or a lost message. So the
classification is built from observed daprd behaviour (phase 0), and **when in doubt, the answer is
unknown**.

Every actor invoker (Queue, Topic, HttpSink, SessionCoordinator, BlobReaper) runs its call through
`ActorCall.RunAsync` ([ActorCall.cs](../server/src/DaprMQ.Interfaces/ActorCall.cs)). That classifies
transport and runtime failures with `ActorCallClassifier`, using the phase-0 table, and throws them
as `ActorCallException` carrying the outcome. Failures inside the actor method itself
(`ActorMethodInvocationException`, `ActorInvokeException`) and caller cancellation pass through
unchanged. Each endpoint's catch-all gains one clause in front, which maps the exception with the
shared helpers in
[DeliveryFailures.cs](../server/src/DaprMQ.ApiServer/Services/DeliveryFailures.cs):
`DeliveryFailureResult` for REST, `DeliveryFailures.ToRpcException` for gRPC. Error bodies gain an
optional `errorCode`, omitted when null.

| ErrorCode | HTTP | gRPC | Marker (REST header / gRPC trailer) |
|---|---|---|---|
| `UNAVAILABLE` | `503` + `Retry-After: 1` | `UNAVAILABLE` | `daprmq-delivery: not-delivered` |
| `DELIVERY_UNKNOWN` | `504` | `UNKNOWN` | `daprmq-delivery: unknown` |

A `500` goes back to meaning a bug. Messages never mention actors.

**Actor-to-actor calls need the same treatment.** `SessionCoordinatorActor` already returns
`SESSION_ACTOR_UNAVAILABLE` (REST `502`, gRPC `UNAVAILABLE`) when its call to a session's queue actor
fails, and the SDKs' session consumers retry it. Phase 0 must establish whether that failure is
always "not delivered". If it isn't, it needs splitting the same way.

## 3. Server retries: not delivered only, within a retry window

The gateway retries **not-delivered** failures for every operation. Two limits apply, and they are
deliberately separate:

| Limit | Bounds | Set by | Default |
|---|---|---|---|
| **Retry window** | How long a call that certainly wasn't delivered is repeated | `daprmq-retry-timeout` (REST header or gRPC metadata, ms), capped by `DELIVERY_RETRY_MAX_SECONDS` | 30 s |
| **Call deadline** | Everything, including a delivered call that is still running | `daprmq-timeout` (REST header, ms) or the gRPC deadline. Optional: none by default | none |

A **delivered call is never cut off by the retry window.** It runs until the caller goes away
(`RequestAborted` / `context.CancellationToken`), its explicit call deadline, or the per-attempt safety
limit `DELIVERY_ATTEMPT_MAX_SECONDS` (default 100 s, for genuinely hung workers).

Why separate (found on the split stack under load): 10,000 parallel acks on one queue wait in line for
the actor's turn. That is slow, but it makes progress. When one limit served both purposes, the slowest
acks were cut off at 30 s and reported `DELIVERY_UNKNOWN`, even though they were about to run (and
probably did, after the cut). Cutting off a delivered call turns a slow success into the one outcome a
caller can't act on safely. A bounded retry window is still right: it limits how long requests pile up
during an outage.

Backoff starts at 100 ms, doubles, is capped at 2 s, and uses full jitter, so requests queued during
an outage don't all reach the returning workers at the same moment. **Unknown** failures are never
retried on the server: only the caller knows whether its operation is idempotent.

How it's built:

- **Retries happen only in API requests, never inside an actor turn.** The invokers are shared with
  actor code, and retrying there would hold an actor's turn. A `DeliveryBudget` (retry deadline,
  optional call deadline, per-attempt limit, and the caller's cancellation) is set for each API
  request in an async-local: by `DeliveryBudgetFilter` (REST, every controller action) and
  `DeliveryBudgetInterceptor` (gRPC unary calls). `ActorCall` retries only when a budget is present.
  Actor callbacks arrive as separate requests, so they never inherit one.
  ([ActorCall.cs](../server/src/DaprMQ.Interfaces/ActorCall.cs),
  [DeliveryBudgetSources.cs](../server/src/DaprMQ.ApiServer/Services/DeliveryBudgetSources.cs))
- **Each attempt is bounded by the call deadline, or the safety limit** (phase-0 finding 4: not the
  actor client's 100 s default alone). An attempt cut off this way is reported as `DELIVERY_UNKNOWN`,
  because it may have reached the actor.
- **No new attempt starts with less than 6 s of retry time left.** daprd takes about 5 s to report "no
  host" (finding 3), so a later attempt would only report "unavailable" after the window had passed.
  The caller gets the last `UNAVAILABLE` instead.
- **A call deadline that has already passed** gets `UNAVAILABLE` without an attempt.
- **Metrics** (meter `DaprMQ.Delivery`): `daprmq.delivery.retries`, and
  `daprmq.delivery.failures{outcome=not-delivered|unknown}`.
- **An invalid `daprmq-timeout` or `daprmq-retry-timeout`** (not a positive integer) gets a `400`.

This is the piece that changes behaviour during a full worker outage. Gateways stay ready (section
1), accept requests, and hold each one until a worker returns or the retry window passes. Callers wait
instead of failing straight away.

Not yet addressed: the fan-in itself. Thousands of concurrent calls on one queue still queue at the
actor. A bulk acknowledge, or per-queue concurrency limits at the gateway (rejecting early with `503`
+ `Retry-After` instead of queueing), would need their own design (open question 9).

**Dapr Resiliency** can also retry actor calls, but it decides by Dapr error code and per target,
not by whether a call was delivered, so it would retry unknown dequeues. A Resiliency **circuit
breaker** on the actor targets may still help, by failing fast against a dead worker (open
question 3).

## 4. SDK retries and waiting

One retry policy, specified once in `sdks/testing` and implemented the same way in .NET, Python,
TypeScript and Java:

| Failure | Retry? |
|---|---|
| `UNAVAILABLE` / `daprmq-delivery: not-delivered` | yes, any operation |
| connection refused, or a DNS failure before anything was sent | yes, any operation |
| `DELIVERY_UNKNOWN` or a connection lost after sending, for **Enqueue where every item has an `IdempotencyKey`** | yes. The actor de-duplicates on the key, and the markers live 24 h (`IDEMPOTENCY_KEY_TTL_SECONDS`) |
| `DELIVERY_UNKNOWN` for anything else | **no**. Throw a typed `DeliveryUnknownException` (or each language's equivalent) |
| Rejected | no |

Options (names illustrative):

- **`RetryTimeout`**: how long one call may keep retrying a DaprMQ that can't serve it, sent to the
  server as `daprmq-retry-timeout`. It never cuts a delivered call short (section 3). Default 30 s.
  `0` turns retries off.
- **`AutoIdempotencyKeys`**: generate a key for any enqueued item that has none, so enqueue retries
  are always safe. Off by default, because each key is an extra state write (`idem_{key}`) per item.
  Recommended for producers that can't tolerate duplicates.

**What user code sees.** Failures that aren't retried, or whose retries run out, reach the caller
as typed exceptions derived from the existing `DaprMQException`, so existing `catch` blocks keep
working. Public names never mention actors, which are an implementation detail: the same applies to
the wire codes above.

| Server sends | .NET SDK throws | Meaning |
|---|---|---|
| `UNAVAILABLE` (`503` / gRPC `UNAVAILABLE`), after the SDK's own retries run out at `RetryTimeout` | `DaprMQUnavailableException` | Definitely didn't happen. Always safe to repeat later |
| `DELIVERY_UNKNOWN` (`504` / gRPC `UNKNOWN`) | `DeliveryUnknownException` (with the operation, the queue id and, for enqueue, the items' idempotency keys) | May or may not have happened |

Python, TypeScript and Java use the equivalent in each language's error style, defined once in
`sdks/testing`. What a caller can do with `DeliveryUnknownException` depends on the operation, and
each SDK's docs say so:

- **Enqueue without keys:** re-send and accept a possible duplicate, or check downstream. With keys,
  the SDK retries itself, so this never reaches user code.
- **Dequeue with a lock:** don't re-send. If the first call ran, the items are locked to a lock id
  the caller never received. They return to the queue when the lock expires, with their delivery
  count raised.
- **Plain dequeue (no lock):** the item may be gone. This is why unknown outcomes are never retried
  automatically. Callers that can't lose items this way should use locked dequeues.
- **Acknowledge / ExtendLock / DeadLetter / Nack:** re-sending is effectively safe. If the first call
  worked, the re-send gets `LockNotFound`, which here means success (open question 4).

**Waiting for the system.** Retries already cover the first call after startup: before the gateway
listens it's "connection refused", and before a worker registers it's `UNAVAILABLE`. Both are retried.
For callers that want to wait *without* sending a real operation (test fixtures, tooling, smoke
checks), each SDK offers `WaitForReady(service)`: it opens `grpc.health.v1.Health/Watch` and returns
on the first `SERVING`. The SDK reconnects while the server isn't listening, and the call is bounded
only by the caller's cancellation. Fixtures wait on `daprmq.DaprMQ.operations` (section 5). That
replaces every probe enqueue, so fixtures stop writing data just to start up.

## 5. Operations signal

`daprmq.DaprMQ.operations` (gRPC health) and `GET /health/operations` (HTTP) answer "can queue
operations be served?" for monitoring and for clients that choose to wait. The name says what
callers care about rather than how it's built: actors and workers are implementation details. It
**never** feeds K8s readiness.

- **An instance hosting the queues** (combined, or a worker) reports its own instance readiness:
  it serves the operations itself.
- **A gateway asks its workers**, through Dapr service invocation rather than placement
  ([OperationsHealthCheck.cs](../server/src/DaprMQ.ApiServer/Services/OperationsHealthCheck.cs)).
  It calls `GET /internal/operations-ready` through its own sidecar
  (`{DAPR_HTTP_ENDPOINT}/v1.0/invoke/{WORKER_APP_ID}/method/internal/operations-ready`). That route
  runs the worker's instance readiness and is mapped only where queues are hosted, so a gateway can
  never answer it for itself.
  - **Any 200 proves a worker can serve.** Invocation reaches one load-balanced instance per call,
    so a failure only rules out that one. Up to 3 attempts (2 s each); any success wins.
  - The check is registered under its own `operations` tag, which readiness never includes.
  - **It stays inside the Dapr API the gateway already uses:** mTLS and access-control policies
    apply, no control-plane flag is needed, and it works in Docker without shared network
    namespaces.
- **Placement's `/placement/state` was rejected** for this job, for the costs listed in the context
  section. The one thing it adds is a count of every registered worker. If we need that, publish it
  as a metric from the workers themselves rather than reading the control plane.

Together with the section 3 retry metrics, this makes "the gateways are holding requests because no
worker is available" visible on a dashboard, rather than discovered through timeouts.

Configuration: `WORKER_APP_ID` is required on gateways, which refuse to start without it. Helm sets
it to `{release}-daprmq-worker`. docker-compose now gives the gateway its own app-id
(`daprmq-gateway`, added to the object store's `scopes`), so invoking the workers (`daprmq-service`)
never reaches the gateway.

## 6. Hung workers: Dapr app health checks

One gap remains. A worker whose app hangs, while its sidecar stays connected, keeps its share of
actors, so the queues placed on it fail until Kubernetes' liveness probe restarts it. Dapr's app
health checks (`dapr.io/enable-app-health-check: "true"` and `dapr.io/app-health-check-path:
"/health"` on the worker Deployment) let the sidecar notice that its app is unhealthy. **Phase 0
must confirm** what daprd 1.18 then does for actors: whether it withdraws the actor types from
placement, so their actors are rebalanced onto healthy workers. If it does, enable it in the worker
chart. The probe path must be liveness (`/health`), not `/health/ready`, which depends on the
sidecar and would be circular.

## Phase 0 findings: what daprd 1.18.4 actually does

Recorded on the split test stack by the opt-in diagnostic
[ActorFailureCatalogue.cs](../server/tests/DaprMQ.IntegrationTests/Diagnostics/ActorFailureCatalogue.cs)
(`DAPRMQ_FAILURE_CATALOGUE=<out.md> dotnet test --filter ActorFailureCatalogue`). Each row is what
the gateway's actor proxy threw (`QueueController`'s catch-all logs it); the caller saw a `500` with
the message in every failure case.

| Mode | Gateway exception | After | Stored? | Class |
|---|---|---|---|---|
| Workers stopped, called immediately or after placement dropped them | `Dapr.DaprApiException`: *error invoke actor method: failed to lookup actor: api error: code = FailedPrecondition desc = did not find address for actor 'QueueActor/<id>'* | 5.0 s (daprd's own lookup wait) | no | **not delivered** |
| Worker app **or** worker sidecar SIGKILLed while the gateway was still sending a 10 000-item enqueue | `HttpRequestException`: *Error while copying content to a stream* → `IOException`: *Unable to write data to the transport connection: Broken pipe* | ~1 s | 0 of 10 000 | not delivered in practice (the request body never finished sending); see below |
| Worker app hung (paused), its sidecar up | `TaskCanceledException`: *HttpClient.Timeout of 100 seconds elapsing* → `HttpIOException`: *The response ended prematurely* | 100 s | — | **unknown** (the call reached the worker) |
| Placement stopped (actor active, or new) | `TaskCanceledException`: *HttpClient.Timeout of 100 seconds* → `SocketException (125)` | 100 s | — | **unknown** (indistinguishable from a hang). Instance readiness does go unhealthy |
| Kubernetes, workers starting while placement rebalances (found by `k8s-deploy-and-test.sh`) | `DaprApiException`: *failed to invoke target <ip>:50002 after 5 retries … remote actor moved* | ~6 s | no | **not delivered**: the target sidecar refused the call because placement had moved the actor |
| Kubernetes, gateway restarting: its own sidecar not accepting yet | `HttpRequestException`: *Connection refused (localhost:3500)* (inner `SocketException` ConnectionRefused) | immediate | — | **not delivered**: the connection never opened |

What this changes in the design:

1. **The not-delivered signatures are precise.** `DaprApiException` whose message contains
   `failed to lookup actor` / `did not find address for actor` or `remote actor moved`, and a refused
   connection to the gateway's own sidecar. Everything else defaults to unknown.
2. **Request-body write failures** (`HttpRequestException` wrapping a *write* `IOException`) mean
   the body never fully reached the gateway's own sidecar, so the actor can't have run it, and the
   catalogue shows nothing stored. Treating them as not delivered is reasonable but is a judgement
   call, because it relies on daprd and the actor runtime reading the whole body before invoking.
   Start with **unknown**, and revisit with more evidence (open question 7).
3. **daprd already waits 5 s** before reporting "no address". A server retry loop adds to that, so
   the first retry happens about 5 s in, and a 30 s cap means roughly 5 attempts.
4. **The gateway's actor calls time out after 100 s** (the `Dapr.Actors` HttpClient default), well
   past the proposed 30 s server cap and most ingress timeouts. Phase 3 must bound each actor call by
   the caller's remaining deadline (pass a `CancellationToken`, or lower the actor client's timeout).
   Otherwise a hung worker or a lost placement holds requests for 100 s, whatever the deadline.
5. **A placement outage looks like a hang, not "no address".** Retries can't help there. Readiness
   (section 1) takes the gateway out of rotation instead, which is the division of work this
   proposal intends.

## Phases

**Phase 0: test topology and daprd catalogue (prerequisite).**

- Give `DaprTestEnvironment` a split topology: gateway(s) in front of worker(s), each with its own
  sidecar and app-id. Select it with `DAPRMQ_TEST_TOPOLOGY=split`, and run CI's server suite both
  combined and split.
- On that stack, cause each failure mode and record what the gateway's `DaprClient` throws
  (exception type, gRPC status, daprd error code):
  1. no workers ever started;
  2. every worker stopped while idle;
  3. a worker killed partway through a bulk dequeue;
  4. placement stopped;
  5. a worker's app hung while its sidecar is up.

  The output is the section 2 classification table, checked into the invoker's tests.
- Confirm the app-health behaviour (section 6).

**Phase 1: instance readiness.** The section 1 check, `/health/ready` and gRPC health, and Helm
probes moved to `/health/ready`.

**Phase 2: classification.** The two error codes, the delivery marker, and the controller and gRPC
mappings. Callers can now tell an outage from a bug.

**Phase 3: server retries.** Section 3, with retry metrics.

**Phase 4: operations signal.** Section 5, plus compose app-ids.

**Phase 5: SDKs.** The retry policy and `WaitForReady` in all four SDKs, tracked as rows in the SDK
integration matrix. Fixtures and the perf harness stop sending probe enqueues.

Each phase can ship on its own. Phases 1 and 2 are low-risk. Phase 3 is where behaviour changes for
every caller.

## Out of scope

- **Waiting without a limit.** Every wait ends at the caller's deadline, the server cap, or
  cancellation.
- **Retrying unknown outcomes of non-idempotent operations.** That needs exactly-once semantics, for
  example idempotency keys on dequeue and ack, which is a separate design.
- **Per-queue readiness.** Whether a specific queue's actor can be activated is answered by calling
  it.
- **`ConsumeSession` streams.** `SessionQueueConsumer` already reconnects on its own loop.

## Open questions

1. **The phase-0 catalogue.** Which daprd 1.18 errors mean "not delivered"? In particular, does
   daprd's own wait for the placement table time out with a distinct code?
2. **REST deadline headers.** `daprmq-timeout` (call deadline) and `daprmq-retry-timeout` (retry
   window), both in milliseconds. Should we also accept the
   `grpc-timeout` format?
3. **Circuit breaker.** Should we add a Dapr Resiliency circuit breaker on the actor targets,
   alongside the application-level retries?
4. **Retrying Acknowledge.** A retried ack that gets `LockNotFound` might mean the first attempt
   succeeded, or that the lock expired and the item was redelivered. Could the actor tell these apart
   (for example, with a short-lived tombstone of recently acknowledged locks), making ack safely
   retryable?
5. **Health service name.** *Decided:* `daprmq.DaprMQ.operations`. It's a name we invent, not a
   real gRPC service; clients only use it as a key, and it's documented as such.
6. **Compose app-ids.** *Decided:* the compose gateway has its own app-id, added to the object store's
   `scopes` (the state store has none). Original question: a separate gateway app-id means adding it to every component's `scopes`. Is
   that acceptable, or should the worker-only route instead return a status that invocation can
   retry past?

7. **Request-body write failures.** Classify `HttpRequestException` from a failed request-body
   write as not delivered (phase 0 finding 2)? That needs confidence that no part of a partially sent
   request is ever acted on.

8. **Existing names that mention actors.** `ActorNotFoundException` (404) and
   `SessionActorUnavailableException` / `SESSION_ACTOR_UNAVAILABLE` (502) already expose actors in the
   public API. Renaming them breaks callers, so it's a separate change; it could keep the old names as
   aliases for one release.

9. **Fan-in on one queue.** Thousands of concurrent calls on one queue wait in line at its actor.
   Should there be a bulk acknowledge, or per-queue concurrency limits at the gateway that reject early
   with `503` + `Retry-After` rather than queueing? *Proposed:* a batch acknowledge, in [batch-acknowledge.md](batch-acknowledge.md).

## Verification

All of the following run on the split test topology unless noted.

- **Instance readiness:**
  - combined and worker instances report `SERVING` only once `QueueActor` is hosted;
  - a gateway reports `SERVING` with no worker started;
  - stopping placement makes everything `NOT_SERVING` within one publisher period.
- **Recovery within the deadline:** stop the workers, enqueue with a 20 s deadline, start the
  workers 5 s later. The enqueue succeeds and the queue holds exactly one item. The gateway stays
  ready throughout.
- **Outage longer than the deadline:** returns `503` / `UNAVAILABLE` at the deadline, not earlier
  and not much later.
- **Unknown outcome of a dequeue:** kill a worker partway through a bulk dequeue. The caller gets
  `DELIVERY_UNKNOWN`, there is no retry, and the item count afterwards shows exactly one attempt.
- **Unknown outcome of a keyed enqueue:** the SDK retries, and each item is stored exactly once.
- **Operations signal:** `daprmq.DaprMQ.operations` goes to `NOT_SERVING` when every worker stops and
  back to `SERVING` when one returns. K8s readiness of the gateways does not change.
- **Fixtures:** every SDK fixture starts with `WaitForReady("daprmq.DaprMQ.operations")`, and no
  `probe`/`readiness-*` queues appear in the state store afterwards.
- **Unit tests:**
  - the invoker's classification table, one row per phase-0 failure mode;
  - the controllers' and gRPC service's mappings for the new error codes;
  - each SDK's retry policy matrix, using fake transports.
