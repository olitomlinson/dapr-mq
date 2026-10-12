# Admin console

A read-only web console for looking inside DaprMQ: which queues, dead-letter queues, topics and HTTP sinks exist, what
is in them, and what is locked. It is served by `DaprMQ.Operator` when `CONSOLE_ENABLED` is set. The design is in
[proposals/admin-console.md](../proposals/admin-console.md).

## Security

**v1 has no authentication.** Anyone who can reach the console can:

- read every message payload, including offloaded blob payloads;
- see lock ids. A lock id is all it takes to acknowledge, nack or extend that lock through the public API, so a viewer
  who can also reach the gateway can settle someone else's lock.

The console's Service is ClusterIP only. Reach it by port-forward, or put your own authenticating ingress in front of it.
Don't expose it publicly.

## Running it

**docker compose:** `docker compose up` starts the operator with the console at <http://localhost:9100>, and
Prometheus at <http://localhost:9090>.

**Helm:** set `operator.enabled=true` and `console.enabled=true`, then
`kubectl port-forward svc/<fullname>-operator-console 9100:8080`. See [helm/README.md](../helm/README.md#admin-console).

**Developing the app:** `cd console && npm install && npm run dev` serves it on <http://localhost:3100>, proxying
`/api` to an operator at `http://localhost:9100` (override with `CONSOLE_API_URL`). `npm test` runs the component
tests against MSW fixtures.

## What it shows

| Page | Contents | Refresh |
|---|---|---|
| Overview | Counts of queues, topics and sinks; total ready and locked; throughput; top queues by depth; DLQs with messages | Every 5 s |
| Queues | Paged list with ready, locked and DLQ counts, in/out rates and a 1h depth sparkline | Every 5 s |
| Queue → Overview | Config, ready by priority, lease, corrupt state, HTTP sink | Every 5 s |
| Queue → Messages / Dead-letter | Ready messages in queue order, 100 per page, with 1 KB previews | Manual |
| Queue → Locked | Locks with live countdowns, delivery counts and mode | Manual |
| Queue → Sessions | Sessions with depth and lease | Manual |
| Message drawer | The full message (up to 1 MB) as JSON, raw or hex; blob payloads load on request | On open |
| Queue → Metrics | Produced vs consumed, ack/nack/expired/dead-lettered rates, and depth, over 15m-24h | Every 5 s |
| Topics → topic | Subscribers with lag, dispatch rate and circuit-breaker state; publish vs dispatch chart | Every 5 s |
| Sinks | URL, max concurrency, lock TTL and polling interval | Every 5 s |

Polling can be paused from the header. Browse views are never polled, because each page loads whole queue segments on
a worker.

**Browsing never changes the queue.** Every read goes through Dapr's actor-state API from a worker, so it never calls or
activates a queue actor and never takes a turn from Enqueue, Dequeue or Ack. What you see is a snapshot of committed
state: a message can be consumed between listing and opening it, which the drawer reports as "consumed since listed".

**Queues appear once they activate.** The list comes from the catalog ([ARCHITECTURE.md](ARCHITECTURE.md#catalog)),
which a queue joins the first time it activates on a worker. Queues that existed before upgrading appear the next time
they are used.

## Charts

Charts need Prometheus scraping the workers' `/metrics` ([ARCHITECTURE.md](ARCHITECTURE.md#metrics)) and
`PROMETHEUS_URL` on the operator (Helm `console.prometheusUrl`; docker compose includes Prometheus). Without it, the
console says "metrics not configured" and everything else still works.

The operator runs only a fixed set of named PromQL queries (`/api/console/metrics/{name}`); it never accepts PromQL
from the browser. Ranges are 15m, 1h, 6h or 24h at 120 points.

## How it works

```
browser ─► operator  /api/console/*  (also serves the app)
              │  Dapr service invocation (mTLS)
              └─► worker  /internal/admin/*  ─► Dapr actor-state API ─► state store
```

- The operator can't read actor state itself (Dapr keys it by the caller's app-id), so it calls the workers' internal
  admin endpoints, as it does for KEDA's depth reads.
- At most `CONSOLE_MAX_WORKER_CALLS` (default 8) worker calls are in flight per operator pod, however many people are
  browsing.
- List and detail reads are cached for `CONSOLE_CACHE_TTL_MS` (default 10 s), shared by every user. Browse pages are not
  cached.
- A worker's 400, 404 or 410 passes through unchanged. A transport failure or worker error is a 503.

| Operator setting | Default | |
|---|---|---|
| `CONSOLE_ENABLED` | `false` | Serve `/api/console/*` and the app. With it off, the operator is unchanged. |
| `CONSOLE_CACHE_TTL_MS` | `10000` | Shared cache for list and detail reads. `0` disables it. |
| `CONSOLE_MAX_WORKER_CALLS` | `8` | Concurrent worker calls per operator pod. |
| `PROMETHEUS_URL` | unset | Prometheus HTTP API for the charts. Unset, `/api/console/metrics/*` returns 501. |
| `SCALER_ENABLED` | `true` | `false` on a console-only Deployment (Helm `console.separateDeployment`). |
