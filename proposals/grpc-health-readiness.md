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
(`grpc.health.v1.Health`). Back it with a readiness check that exercises the same path the probe
enqueue does, but without writing state. Clients wait on `Health.Watch`, a server stream, so they are
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

The check has to give the same answer as the probe enqueue, or the startup race comes back. The
deployment topologies differ:

| Instance | `ENABLE_API` / `REGISTER_ACTORS` | Ready when |
|---|---|---|
| Gateway ([docker-compose](../docker-compose.yml#L76), Helm gateway) | true / false | sidecar healthy **and** a QueueActor on some worker answers |
| Worker | false / true | sidecar healthy **and** its own actor types are registered with placement |
| Single process (default) | true / true | both of the above |

On a gateway, the sidecar's `/v1.0/metadata` doesn't list actor types hosted on other pods, so
"placement connected" doesn't prove that an enqueue will land. The only reliable signal is a real actor
round trip.

### 1. `Ping` on the queue actor

Add `Task Ping()` to [IQueueActor.cs](../server/src/DaprMQ.Interfaces/IQueueActor.cs), with a matching
`ActorMethodNames.Ping` constant. It returns immediately and doesn't touch `StateManager`.

The health check calls it on one fixed actor id, `__daprmq-health`. That actor is activated once and
then stays warm, because the check runs every few seconds, which is well inside the idle timeout. The
id is reserved: the controller's id validation rejects it on the public API.

Activation runs the lazy lock sweep, which only reads the lock index. With no state there is nothing to
sweep, so it doesn't save anything. A test should assert this.

### 2. `ActorReadinessHealthCheck` (server)

An `IHealthCheck` in `DaprMQ.ApiServer/Services` with two steps:

1. `DaprClient.CheckHealthAsync()`. If the sidecar isn't up, return `Unhealthy("sidecar")`.
2. `IQueueActorInvoker` → `Ping` on `__daprmq-health`, with a short timeout (2 s). If it throws or
   times out, return `Unhealthy("actors")`. Otherwise return `Healthy`.

Register it with the tag `ready`.

### 3. Wire up the health service (server)

In [Program.cs](../server/src/DaprMQ.ApiServer/Program.cs):

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<ActorReadinessHealthCheck>("actors", tags: ["ready"]);

builder.Services.AddGrpcHealthChecks(o =>
{
    // "" = whole server; the named service = the DaprMQ API surface
    o.Services.Map("", r => r.Tags.Contains("ready"));
    o.Services.Map("daprmq.ApiServer.Grpc.DaprMQ", r => r.Tags.Contains("ready"));
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
  make `Watch` slower than today's 1 s poll. 2 s keeps detection quick, and the cost is one cheap actor
  call per pod every 2 s.
- **Keep `/health` as liveness.** If liveness depended on actors, a placement outage would make
  Kubernetes restart every pod, which makes the outage worse. Liveness stays "the process is up", and
  readiness moves to `/health/ready`.

Package: `Grpc.AspNetCore.HealthChecks` (same 2.68.x line as `Grpc.AspNetCore`).

### 4. `WaitForReadyAsync` (.NET SDK)

Add this to [IDaprMQClient.cs](../sdks/dotnet/src/DaprMQ.Client/IDaprMQClient.cs) and `DaprMQClient`:

```csharp
Task WaitForReadyAsync(CancellationToken ct = default);
```

Implementation:

- Open `Health.Watch(service: "daprmq.ApiServer.Grpc.DaprMQ")` and return on the first `SERVING`.
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

### 5. Helm

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
- **Sidecar metadata (`/v1.0/metadata` → `actorRuntime.placement`).** No actor call is needed, but it
  is wrong on gateways (see [What "ready" means](#what-ready-means)), which is exactly where clients
  connect.

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
   `daprmq.ApiServer.Grpc.DaprMQ` returns `SERVING` against the running stack, and `/health/ready`
   returns 200.
2. **Health check, mocked dependencies:**
   - sidecar unhealthy → `Unhealthy`, and the actor isn't called;
   - `Ping` throws → `Unhealthy`;
   - `Ping` hangs past the timeout → `Unhealthy`;
   - both succeed → `Healthy`.
3. **Actor:** `Ping` on a fresh actor with an empty state dictionary makes no `SetStateAsync` or
   `SaveStateAsync` calls.
4. **Controller:** an enqueue to `__daprmq-health` returns 400.

.NET SDK ([sdks/dotnet/tests](../sdks/dotnet/tests/)):

5. `WaitForReadyAsync` returns after a `NOT_SERVING` → `SERVING` sequence from a mocked `Watch` stream.
6. It retries through `Unavailable` and then succeeds.
7. It throws `NotSupportedException` on `Unimplemented`.
8. It honours cancellation.
9. **Integration:** the SDK integration fixture uses `WaitForReadyAsync` in place of its probe, and
   the existing suite passes unchanged. This is the end-to-end proof that readiness is no weaker than
   the enqueue probe.

Run `dotnet test` in `server/tests/DaprMQ.Tests`, then `./build-and-test.sh` before committing.

## Verification

- **Cold start:** `docker compose up` from nothing, and run `grpcurl -plaintext localhost:8102
  grpc.health.v1.Health/Watch`. It should stream `NOT_SERVING` and then `SERVING`. The first enqueue
  after `SERVING` should succeed every time (run it 20× in a loop).
- **Worker loss:** stop both workers. The gateway's `Watch` should go to `NOT_SERVING` within about
  one publisher period plus the 2 s ping timeout, and return to `SERVING` once a worker is back.
- **Perf harness:** startup wait is no slower than today. No `perf-probe-*` queues appear in the state
  store afterwards.
