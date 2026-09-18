# Example apps — control-plane HTTP contract

This is the HTTP API that every one of the 8 example containers (producer + consumer, ×4 languages) implements **identically**. It is the control plane used to drive the demo interactively — it is separate from, and sits alongside, each app's use of the DaprMQ SDK client to talk to the actual DaprMQ gateway.

All 8 containers listen on `controlPort` (default `8080`, set via the `CONTROL_PORT` env var / Helm `controlPort` value).

## Logging

Every container logs to stdout in this format:

```
[<language>] [<role>] <ISO8601 UTC timestamp> <LEVEL> <message>
```

Example: `[dotnet] [producer] 2026-09-17T10:00:00.123Z INFO Enqueued item 1/3 to queue examples-dotnet-basic (priority=1)`

`<language>` ∈ `dotnet | java | python | typescript`, `<role>` ∈ `producer | consumer`, `<LEVEL>` ∈ `INFO | WARN | ERROR`.

## Error envelope

Every non-2xx response from a route below (other than a bare 404 from unknown routing) is:

```json
{ "error": { "code": "STRING_CODE", "message": "human readable message" } }
```

## Routes

### `GET /health`

Always `200` once the process is accepting connections. Used for k8s liveness/readiness probes.

```json
{ "status": "ok", "language": "dotnet", "role": "producer" }
```

### `GET /config`

Returns the container's current DaprMQ connection config.

```json
{
  "httpBaseUrl": "http://daprmq-gateway:8080",
  "grpcAddress": "daprmq-gateway:8081",
  "queuePrefix": "examples-dotnet",
  "language": "dotnet",
  "role": "producer",
  "source": "default"
}
```

`source` is `"default"` (env-derived, untouched) or `"override"` (a `PUT /config` has been applied since the last `POST /reset`).

`grpcAddress` is a bare `host:port` (no scheme) — match whatever your SDK's gRPC constructor arg expects.

### `PUT /config`

Body — all fields optional, omitted fields keep their current value:

```json
{ "httpBaseUrl": "http://...", "grpcAddress": "host:port", "queuePrefix": "my-prefix" }
```

Effect: rebuild the in-process DaprMQ SDK client using the new `httpBaseUrl`/`grpcAddress`. `queuePrefix` changes which queue ids subsequent `POST /scenarios/{name}/run` calls target (see SCENARIOS.md).

- `200` → the full updated config object (same shape as `GET /config`, now `"source": "override"`).
- `400` → error envelope, `code: "INVALID_CONFIG"`, if a supplied value is empty or malformed (e.g. not a valid URL).

### `POST /reset`

No body.

Effect:
1. Clears any in-memory scenario bookkeeping this pod has accumulated (cached lock ids, session ids, lease ids from prior runs).
2. Reverts `/config` to the environment-variable-derived defaults, discarding any `PUT /config` override.

Does **not** touch queue contents in DaprMQ itself — there is no delete-queue API. Items enqueued by prior scenario runs remain in the real queues; re-running a scenario after `/reset` may see leftover items from earlier runs mixed in with new ones. This is expected and documented in the top-level README, not something to special-case.

- `200` → `{ "success": true, "message": "State reset to defaults" }`

### `GET /scenarios`

Returns the 4 scenario descriptors (identical `name`s on every pod; `role` and `description` describe what *this* pod does for that scenario).

```json
[
  { "name": "basic",          "role": "producer", "description": "Enqueues 3 plain items." },
  { "name": "ack-deadletter", "role": "producer", "description": "Enqueues 3 items tagged ack/deadletter/expire." },
  { "name": "priority",       "role": "producer", "description": "Enqueues normal-priority items, then fast-lane items." },
  { "name": "sessions",       "role": "producer", "description": "Enqueues items across two sessions." }
]
```

### `POST /scenarios/{name}/run`

`{name}` ∈ `basic | ack-deadletter | priority | sessions | idempotency`. Request body ignored if present (reserved).

Runs **synchronously** — blocks until the scenario's bounded sequence of DaprMQ calls completes (worst case ~11s, for the deliberate lock-expiry wait in `ack-deadletter`), logging every step to stdout as it happens.

- `200`:
```json
{
  "scenario": "ack-deadletter",
  "role": "consumer",
  "queueId": "examples-dotnet-ackdlq",
  "startedAt": "2026-09-17T10:00:00.000Z",
  "finishedAt": "2026-09-17T10:00:12.500Z",
  "steps": [
    { "action": "dequeue", "detail": "dequeued 3 items, lockIds=[...]" },
    { "action": "acknowledge", "detail": "acked item outcome=ack" },
    { "action": "deadletter", "detail": "dead-lettered item outcome=deadletter -> dlqId=examples-dotnet-ackdlq-deadletter" },
    { "action": "wait", "detail": "waiting 11s for outcome=expire item's lock to expire" },
    { "action": "dequeue", "detail": "dequeued expired item again via redelivery" }
  ]
}
```
  Every `steps[]` entry has exactly `action` (string) and `detail` (free-text string) — that shape is the fixed part of the contract.
- `404` → `code: "UNKNOWN_SCENARIO"` for a bad `{name}`.
- `409` → `code: "SCENARIO_IN_PROGRESS"` if a run is already in flight on this pod. Each pod holds one simple in-process mutex/lock so two overlapping runs on the same pod can't interleave.
- `502` → `code: "UPSTREAM_ERROR"` wrapping any unexpected error/status from the DaprMQ gateway call (also logged to stdout with the underlying detail).

**Empty-queue tolerance**: if a consumer-side scenario dequeues and finds nothing (e.g. the producer's scenario hasn't run yet), this is *not* an error. Return `200` with a `steps` entry noting it, e.g. `{"action":"dequeue","detail":"queue empty — run the producer's scenario first"}`, and finish normally.

## Config sources (env vars → defaults)

Each container reads these env vars at startup to build its **default** config (before any `PUT /config` override):

| Env var | Purpose | Example (set by Helm chart) |
|---|---|---|
| `LANGUAGE` | fixed per image, used in logs/health | `dotnet` |
| `ROLE` | fixed per image, used in logs/health | `producer` |
| `CONTROL_PORT` | control-plane HTTP port | `8080` |
| `DAPRMQ_HTTP_BASE_URL` | DaprMQ gateway HTTP base URL | `http://daprmq-gateway:8080` |
| `DAPRMQ_GRPC_ADDRESS` | DaprMQ gateway gRPC address (`host:port`, no scheme) | `daprmq-gateway:8081` |
| `DAPRMQ_QUEUE_PREFIX` | default queue-id prefix | `examples-dotnet` |

For **local, non-Helm runs** (e.g. plain `docker run` against a docker-compose DaprMQ instance), default `DAPRMQ_HTTP_BASE_URL`/`DAPRMQ_GRPC_ADDRESS` to `http://localhost:8002` / `localhost:8003` if the env vars are unset, matching the SDKs' own doc examples. `DAPRMQ_QUEUE_PREFIX` defaults to `examples-{language}` if unset.
