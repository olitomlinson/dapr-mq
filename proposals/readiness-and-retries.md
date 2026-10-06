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
| Is at least one worker available? | **Actor-host signal**: `grpc.health.v1`, service `daprmq.DaprMQ.actors`, plus metrics | Monitoring, clients that choose to wait (fixtures, tooling). **Never** K8s readiness |
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

`QueueActorInvoker` ([DaprActorInvoker.cs](../server/src/DaprMQ.Interfaces/DaprActorInvoker.cs))
catches invocation failures, classifies them, and returns a failed result carrying one of two new
`ErrorCode` strings, instead of letting the exception reach the controllers' catch-all. The result
models in [Models.cs](../server/src/DaprMQ.Interfaces/Models.cs) carry `ErrorCode` as a string, as
with `LOCK_EXPIRED` today.

| ErrorCode | HTTP | gRPC | Marker (REST header / gRPC trailer) |
|---|---|---|---|
| `ACTOR_UNAVAILABLE` | `503` + `Retry-After: 1` | `UNAVAILABLE` | `daprmq-delivery: not-delivered` |
| `DELIVERY_UNKNOWN` | `504` | `UNKNOWN` | `daprmq-delivery: unknown` |

The controllers and `DaprMQGrpcService` already switch on `result.ErrorCode`; these are two more
entries in those mappings. A `500` goes back to meaning a bug.

**Actor-to-actor calls need the same treatment.** `SessionCoordinatorActor` already returns
`SESSION_ACTOR_UNAVAILABLE` (REST `502`, gRPC `UNAVAILABLE`) when its call to a session's queue actor
fails, and the SDKs' session consumers retry it. Phase 0 must establish whether that failure is
always "not delivered". If it isn't, it needs splitting the same way.

## 3. Server retries: not delivered only, bounded by the deadline

The gateway retries **not-delivered** failures for every operation. Retries stop at whichever of
these comes first:

- **the caller's deadline:** gRPC `context.Deadline`, or for REST a `daprmq-timeout` request header
  (milliseconds), since HTTP has no standard deadline. The SDKs always send one.
- **the server cap** `ACTOR_RETRY_MAX_SECONDS` (default 30, kept below typical ingress timeouts).
  Without a caller deadline, the cap alone applies.
- **the caller going away** (`RequestAborted` / `context.CancellationToken`).

Backoff starts at 100 ms, doubles, is capped at 2 s, and uses full jitter, so requests queued during
an outage don't all reach the returning workers at the same moment. If the deadline runs out, the
caller gets `ACTOR_UNAVAILABLE`. **Unknown** failures are never retried on the server: only the caller
knows whether its operation is idempotent.

This is the piece that changes behaviour during a full worker outage. Gateways stay ready (section
1), accept requests, and hold each one until a worker returns or its deadline passes. Callers wait
instead of failing straight away.

**Dapr Resiliency** can also retry actor calls, but it decides by Dapr error code and per target,
not by whether a call was delivered, so it would retry unknown dequeues. A Resiliency **circuit
breaker** on the actor targets may still help, by failing fast against a dead worker (open
question 3).

## 4. SDK retries and waiting

One retry policy, specified once in `sdks/testing` and implemented the same way in .NET, Python,
TypeScript and Java:

| Failure | Retry? |
|---|---|
| `ACTOR_UNAVAILABLE` / `daprmq-delivery: not-delivered` | yes, any operation |
| connection refused, or a DNS failure before anything was sent | yes, any operation |
| `DELIVERY_UNKNOWN` or a connection lost after sending, for **Enqueue where every item has an `IdempotencyKey`** | yes. The actor de-duplicates on the key, and the markers live 24 h (`IDEMPOTENCY_KEY_TTL_SECONDS`) |
| `DELIVERY_UNKNOWN` for anything else | **no**. Throw a typed `DeliveryUnknownException` (or each language's equivalent) |
| Rejected | no |

Options (names illustrative):

- **`RetryTimeout`**: how long one call may keep retrying, which is also the deadline sent to the
  server. Default 30 s. `0` turns retries off.
- **`AutoIdempotencyKeys`**: generate a key for any enqueued item that has none, so enqueue retries
  are always safe. Off by default, because each key is an extra state write (`idem_{key}`) per item.
  Recommended for producers that can't tolerate duplicates.

**Waiting for the system.** Retries already cover the first call after startup: before the gateway
listens it's "connection refused", and before a worker registers it's `ACTOR_UNAVAILABLE`. Both are retried.
For callers that want to wait *without* sending a real operation (test fixtures, tooling, smoke
checks), each SDK offers `WaitForReady(service)`: it opens `grpc.health.v1.Health/Watch` and returns
on the first `SERVING`. The SDK reconnects while the server isn't listening, and the call is bounded
only by the caller's cancellation. Fixtures wait on `daprmq.DaprMQ.actors` (section 5). That
replaces every probe enqueue, so fixtures stop writing data just to start up.

## 5. Actor-host signal

This answers "is at least one worker available?" for monitoring and for clients that choose to wait.
It **never** feeds K8s readiness.

**Mechanism: Dapr service invocation, not placement.** On a gateway, call a worker-only endpoint
(`GET /internal/actors-ready`, mapped only when `REGISTER_ACTORS=true`, which runs the worker's
instance-readiness check) through the gateway's own sidecar:
`{DAPR_HTTP_ENDPOINT}/v1.0/invoke/{WORKER_APP_ID}/method/internal/actors-ready`.

- **Any 200 proves a worker is up and hosting `QueueActor`.** Invocation reaches one load-balanced
  instance per call, so a failure only rules out that one. Try up to 3 times per check, any success
  wins, and the result is `SERVING` or `NOT_SERVING` on the `daprmq.DaprMQ.actors` health service.
- **It stays inside the Dapr API the gateway already uses:** mTLS and access-control policies apply,
  no control-plane flag is needed, and it works in Docker without shared network namespaces.
- **Placement's `/placement/state` was rejected** for this job, for the costs listed in the context
  section. The one thing it adds is a count of every registered worker. If we need that, publish it
  as a metric from the workers themselves rather than reading the control plane.

Gateways also export metrics: actor-host checks passed and failed, and the retry counts and outcomes
from section 3. That makes "the gateways are holding requests because no worker is available"
visible on a dashboard, rather than discovered through timeouts.

Configuration: `WORKER_APP_ID` (required on gateways; Helm sets `{release}-daprmq-worker`).
docker-compose currently gives gateways and workers the same app-id (`daprmq-service`), so
invocation could land on the gateway itself, where the worker-only route doesn't exist. Compose
should give gateways their own app-id and add it to the components' `scopes` (open question 6).

## 6. Hung workers: Dapr app health checks

One gap remains. A worker whose app hangs, while its sidecar stays connected, keeps its share of
actors, so the queues placed on it fail until Kubernetes' liveness probe restarts it. Dapr's app
health checks (`dapr.io/enable-app-health-check: "true"` and `dapr.io/app-health-check-path:
"/health"` on the worker Deployment) let the sidecar notice that its app is unhealthy. **Phase 0
must confirm** what daprd 1.18 then does for actors: whether it withdraws the actor types from
placement, so their actors are rebalanced onto healthy workers. If it does, enable it in the worker
chart. The probe path must be liveness (`/health`), not `/health/ready`, which depends on the
sidecar and would be circular.

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

**Phase 4: actor-host signal.** Section 5, plus compose app-ids.

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
2. **REST deadline header.** Is `daprmq-timeout` (milliseconds) right, or should we also accept the
   `grpc-timeout` format?
3. **Circuit breaker.** Should we add a Dapr Resiliency circuit breaker on the actor targets,
   alongside the application-level retries?
4. **Retrying Acknowledge.** A retried ack that gets `LockNotFound` might mean the first attempt
   succeeded, or that the lock expired and the item was redelivered. Could the actor tell these apart
   (for example, with a short-lived tombstone of recently acknowledged locks), making ack safely
   retryable?
5. **Health service name.** `daprmq.DaprMQ.actors` is a name we invent, not a real gRPC service.
   Clients only use it as a key, but it should be documented as such.
6. **Compose app-ids.** A separate gateway app-id means adding it to every component's `scopes`. Is
   that acceptable, or should the worker-only route instead return a status that invocation can
   retry past?

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
- **Actor-host signal:** `daprmq.DaprMQ.actors` goes to `NOT_SERVING` when every worker stops and
  back to `SERVING` when one returns. K8s readiness of the gateways does not change.
- **Fixtures:** every SDK fixture starts with `WaitForReady("daprmq.DaprMQ.actors")`, and no
  `probe`/`readiness-*` queues appear in the state store afterwards.
- **Unit tests:**
  - the invoker's classification table, one row per phase-0 failure mode;
  - the controllers' and gRPC service's mappings for the new error codes;
  - each SDK's retry policy matrix, using fake transports.
