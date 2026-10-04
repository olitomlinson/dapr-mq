# Readiness: replace probe enqueues with the gRPC health protocol

## Context

Nothing on the server says when DaprMQ can actually serve requests, so every client that has to wait
for a fresh stack polls a **real enqueue** until one succeeds:

| Call site | Probe |
|---|---|
| [Perf harness](../sdks/dotnet/perf/DaprMQ.Client.Perf/Program.cs#L122) `WaitForServerAsync` | gRPC enqueue to `perf-probe-{guid}`, every 1 s, up to 2 min |
| [Java fixture](../sdks/java/src/test/java/com/daprmq/client/integration/DaprMQServer.java#L134) | HTTP enqueue to `readiness-{uuid}` |
| [Python fixture](../sdks/python/tests/integration/conftest.py#L141) | HTTP enqueue |
| [TypeScript fixture](../sdks/typescript/tests/integration/daprmqServer.ts#L149) | HTTP enqueue |
| [INTEGRATION_TESTS.md](../sdks/testing/INTEGRATION_TESTS.md#L22) | documents the probe enqueue as the readiness pattern |

The probe works because an enqueue goes through the whole path: API → sidecar → placement → actor host.
It has several costs:

- **It writes data.** Each successful probe leaves a new queue with one item in the state store, and
  nobody cleans it up. In a long-lived environment that is garbage, and any "list queues" view has to
  ignore it.
- **It is a business operation used as a health check.** Rate limits, dedup, auditing or metrics on
  enqueue will all count probes.
- **Every SDK and fixture reimplements it**, each with its own loop, interval and timeout. SDK users get
  no helper at all.
- **It polls.** Readiness is detected up to one interval late, and every attempt before that costs a
  failed actor call.

The server's only health signal doesn't help. `/health`
([Program.cs:273](../server/src/DaprMQ.ApiServer/Program.cs#L273)) returns `healthy` as soon as
Kestrel is listening, before the sidecar or placement is ready. Helm uses it as the readiness probe
([values.yaml](../helm/values.yaml#L62)), so gateway pods are marked Ready, and get traffic, before
they can handle an enqueue.

## Change

Implement the standard [gRPC Health Checking Protocol](https://github.com/grpc/grpc/blob/master/doc/health-checking.md)
(`grpc.health.v1.Health`). Back it with a readiness check that asks the sidecar for its own view of
the actor runtime. The check doesn't call or activate an actor, and it doesn't write state. Clients wait on `Health.Watch`, a server stream, so they are
told when the server becomes ready and don't have to poll for it.

Why the standard protocol rather than a custom frame or RPC:

- Every gRPC ecosystem ships a client for it: `Grpc.HealthCheck` (.NET), `grpcio-health-checking`
  (Python), `grpc-services` (Java) and `grpc-health-check` (Node). Each SDK gets it without generating
  code from our proto.
- Kubernetes supports it natively (`readinessProbe.grpc`), as do `grpcurl`, `grpc_health_probe`, Envoy
  and most load balancers.
- `Watch` is already defined as "stream status changes", which is exactly the wait-until-ready
  semantics we need.

### What "ready" means

The sidecar already reports whether its actor runtime is up. `GET /v1.0/metadata` returns an
`actorRuntime` object, computed in daprd's `actors.RuntimeStatus()` (`pkg/actors/actors.go`):

| Field | Value when ready | Meaning |
|---|---|---|
| `runtimeStatus` | `RUNNING` | The app has finished registering its actor types (none, on a gateway) |
| `placement` | `placement: connected` | The sidecar is connected to placement |
| `hostReady` | `true` | Both of the above, and the actor runtime has started |
| `activeActors[].type` | includes `QueueActor` | This sidecar hosts `QueueActor` (only when `REGISTER_ACTORS=true`) |

What that proves for each topology:

| Instance | `ENABLE_API` / `REGISTER_ACTORS` | Ready when | Same answer as the probe enqueue? |
|---|---|---|---|
| Single process (default, and every SDK fixture) | true / true | sidecar healthy, `hostReady`, hosts `QueueActor` | Yes. The actor type is local. |
| Worker | false / true | sidecar healthy, `hostReady`, hosts `QueueActor` | Yes, for this worker's share of actors. |
| Gateway ([docker-compose](../docker-compose.yml#L76), Helm gateway) | true / false | sidecar healthy, `hostReady` | **No.** See below. |

**Gateway limitation.** A gateway's metadata doesn't list actor types hosted on other pods, so a ready
gateway only knows it can reach placement, not that any worker is hosting `QueueActor`. If every
worker is down or still starting, the gateway reports `SERVING` and enqueues fail. We accept this
rather than activate an actor to prove readiness:

- Workers have their own readiness probe, so Kubernetes and operators can already see when no worker is
  ready.
- Every SDK integration fixture runs the single-process topology, where the check is exact.
- An actor round trip would make every health check an actor call, with a reserved actor id kept
  permanently active, for a case that worker readiness already covers.

### 1. `DaprReadinessHealthCheck` (server)

An `IHealthCheck` in `DaprMQ.ApiServer/Services` with two steps:

1. `DaprClient.CheckOutboundHealthAsync()` (`/v1.0/healthz/outbound`). If the sidecar isn't up,
   return `Unhealthy("sidecar")`. Use the outbound check, not `CheckHealthAsync()` (`/v1.0/healthz`):
   `/healthz` also waits for the app side of the sidecar. In single-process mode that app is this
   process, and the question here is only whether it can call Dapr.
2. `GET {DAPR_HTTP_ENDPOINT}/v1.0/metadata` with a 2 s timeout. Return `Unhealthy("actors")` unless
   `actorRuntime.hostReady` is `true` and, when `REGISTER_ACTORS=true`, `actorRuntime.activeActors`
   includes `QueueActor`. Otherwise return `Healthy`.

Read the metadata over HTTP: neither `DaprClient.GetMetadataAsync()` nor `Dapr.Metadata` exposes
`hostReady` or `placement`. Deserialise only the `actorRuntime` fields that are used.

Register it with the tag `ready`.

### 2. Wire up the health service (server)

In [Program.cs](../server/src/DaprMQ.ApiServer/Program.cs):

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<DaprReadinessHealthCheck>("dapr", tags: ["ready"], timeout: TimeSpan.FromSeconds(3));

builder.Services.AddGrpcHealthChecks(o =>
{
    // "" = whole server; the named service = the DaprMQ API surface
    o.Services.Map("", r => r.Tags.Contains("ready"));
    o.Services.Map("daprmq.DaprMQ", r => r.Tags.Contains("ready"));
});

// Watch is driven by the health-check publisher; defaults are 5 s delay / 30 s period
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.Zero;
    o.Period = TimeSpan.FromSeconds(2);
});

app.MapGrpcHealthChecksService();                                   // outside the enableApi block
app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Tags.Contains("ready") });
```

Notes:

- **Map the health service whether or not `ENABLE_API` is set**, so that workers expose it too. It goes
  outside the `if (enableApi)` block at [Program.cs:262](../server/src/DaprMQ.ApiServer/Program.cs#L262).
- **The publisher period is the Watch latency.** The defaults (first run at 5 s, then every 30 s) would
  make `Watch` slower than today's 1 s poll. 2 s keeps detection quick, and the cost is two local sidecar
  calls per pod every 2 s.
- **Keep `/health` as liveness.** If liveness depended on placement, a placement outage would make
  Kubernetes restart every pod, which makes the outage worse. Liveness stays "the process is up", and
  readiness moves to `/health/ready`.

Package: `Grpc.AspNetCore.HealthChecks` (same 2.68.x line as `Grpc.AspNetCore`).

### 3. `WaitForReadyAsync` (.NET SDK)

Add this to [IDaprMQClient.cs](../sdks/dotnet/src/DaprMQ.Client/IDaprMQClient.cs) and `DaprMQClient`:

```csharp
Task WaitForReadyAsync(CancellationToken ct = default);
```

Implementation:

- Open `Health.Watch(service: "daprmq.DaprMQ")` and return on the first `SERVING`.
- If the server isn't listening yet, the call fails with `Unavailable`. Reconnect with capped backoff
  (250 ms → 2 s) until `ct` is cancelled.
- If the server is too old to have the health service, the call fails with `Unimplemented`. Throw a
  clear `NotSupportedException` instead of retrying forever.
- Callers apply a timeout with `CancelAfter`. The SDK has no built-in deadline.

Package: `Grpc.HealthCheck` in `DaprMQ.Client.csproj` (client stubs only).

Then [`WaitForServerAsync`](../sdks/dotnet/perf/DaprMQ.Client.Perf/Program.cs#L122) in the perf
harness collapses to:

```csharp
using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
readyCts.CancelAfter(TimeSpan.FromMinutes(2));
await client.WaitForReadyAsync(readyCts.Token);
```

### 4. Helm

Point `readinessProbe` at `/health/ready` for both gateway and worker in
[values.yaml](../helm/values.yaml). Leave `livenessProbe` on `/health`. An HTTP probe is used rather
than `grpc:` because the HTTP port is the one already exposed on every pod, and both endpoints run the
same check.

## Alternatives considered

- **A custom `Ping` RPC on the `DaprMQ` service.** It would work, but every client and tool would need
  our proto to call it. The standard protocol gives the same thing plus ecosystem support at no extra
  cost.
- **HTTP/2 PING frames or keepalive.** These only prove the TCP/HTTP2 connection is alive, which
  `Grpc.Net.Client` keepalive already does. They say nothing about the sidecar or actors.
- **gRPC `WaitForReady` call option.** This waits for the channel to connect, not for actors. A probe
  enqueue would still fail with an actor error after the channel is up.
- **An actor round trip (`Ping` on a reserved actor id).** This is the only check that is exact on a
  gateway, and it's what the Dapr .NET SDK's own actor integration tests do
  (`ActorRuntimeHelper.WaitForActorRuntimeAsync`). It was rejected because it activates and keeps an
  actor alive just for health checks, adds an actor method and a reserved id to the public API, and
  puts an actor call (with its own timeout, since daprd holds actor calls until placement is ready) on
  every check. See [Gateway limitation](#what-ready-means) for why the cheaper check is enough.

## Out of scope

- **Python, Java and TypeScript SDK helpers and fixtures.** Once the server ships the health service,
  each SDK can add `wait_for_ready` / `waitForReady` using its stock health client, and its fixture can
  drop the probe enqueue. Track these per SDK. `INTEGRATION_TESTS.md` is updated in the same change as
  the first SDK that switches.
- **Per-queue readiness.** Health covers the server as a whole. Whether a specific queue's actor can be
  placed is still answered by calling it.
- **Cleaning up probe queues already written** to existing state stores.

## Tests (TDD)

Server ([DaprMQ.Tests](../server/tests/DaprMQ.Tests/)):

1. **Integration (HTTP contract):** `grpc.health.v1.Health/Check` with service
   `daprmq.DaprMQ` returns `SERVING` against the running stack, and `/health/ready`
   returns 200.
2. **Health check, mocked dependencies:**
   - sidecar unhealthy → `Unhealthy`, and metadata isn't fetched;
   - metadata request throws or times out → `Unhealthy`;
   - `hostReady: false` → `Unhealthy`;
   - `REGISTER_ACTORS=true` and `QueueActor` missing from `activeActors` → `Unhealthy`;
   - `REGISTER_ACTORS=false`, `hostReady: true`, no actor types → `Healthy`;
   - `REGISTER_ACTORS=true`, `hostReady: true`, `QueueActor` listed → `Healthy`.

.NET SDK ([sdks/dotnet/tests](../sdks/dotnet/tests/)):

3. `WaitForReadyAsync` returns after a `NOT_SERVING` → `SERVING` sequence from a mocked `Watch` stream.
4. It retries through `Unavailable` and then succeeds.
5. It throws `NotSupportedException` on `Unimplemented`.
6. It honours cancellation.
7. **Integration:** the shared integration fixture (`DaprTestEnvironment`, used by both the server
   and .NET SDK suites) replaces its fixed 5 s sleep with a wait on `/health/ready`, and both suites
   pass unchanged. The fixture runs the single-process topology, so this is the end-to-end proof that
   readiness there is no weaker than the enqueue probe. X-04 checks `WaitForReadyAsync` against the
   running stack.

Run `dotnet test` in `server/tests/DaprMQ.Tests`, then `./build-and-test.sh` before committing.

## Verification

- **Cold start, single process:** start the SDK fixture's stack and run `grpcurl -plaintext
  localhost:<grpc port> grpc.health.v1.Health/Watch`. It should stream `NOT_SERVING` and then
  `SERVING`. The first enqueue after `SERVING` should succeed every time (run it 20× in a loop).
- **Cold start, docker compose:** each worker's `/health/ready` should return 200 only after its
  sidecar shows `hostReady` and lists `QueueActor`. Once at least one worker is ready, the first
  enqueue through the gateway should succeed.
- **Worker loss:** stop both workers. Their readiness goes to failing, and the gateway stays `SERVING`,
  as described in the gateway limitation. Stop placement instead, and the gateway's `Watch` should go to
  `NOT_SERVING` within about one publisher period.
- **Perf harness:** startup wait is no slower than today. No `perf-probe-*` queues appear in the state
  store afterwards.
