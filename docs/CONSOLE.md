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

**docker compose:** `docker compose up` starts the operator with the console at <http://localhost:9100>. For traffic to look at, `docker compose --profile load up -d loadgen` starts the
[load generator](../tools/loadgen/README.md).

**Helm:** set `operator.enabled=true` and `console.enabled=true`, then
`kubectl port-forward svc/<fullname>-operator-console 9100:8080`. See [helm/README.md](../helm/README.md#admin-console).

**Developing the app:** `cd console && npm install && npm run dev` serves it on <http://localhost:3100>, proxying
`/api` to an operator at `http://localhost:9100` (override with `CONSOLE_API_URL`). `npm test` runs the component
tests against MSW fixtures.

## What it shows

| Page | Contents | Refresh |
|---|---|---|
| Overview | Counts of queues, topics and sinks; total ready and locked; top queues by depth; DLQs with messages | Every 5 s |
| Queues | Paged list with ready, locked and DLQ counts | Every 5 s |
| Queue → Overview | Config, ready by priority, lease, corrupt state, HTTP sink | Every 5 s |
| Queue → Messages / Dead-letter | Ready messages in queue order, 100 per page, with 1 KB previews | Manual |
| Queue → Locked | Locks with live countdowns, delivery counts and mode | Manual |
| Queue → Sessions | Sessions with depth and lease | Manual |
| Message drawer | The full message (up to 1 MB) as JSON, raw or hex; blob payloads load on request | On open |
| Topics → topic | Subscribers with lag and circuit-breaker state | Every 5 s |
| Sinks | URL, max concurrency, lock TTL and polling interval | Every 5 s |
| Scaling | KEDA ScaledObjects on DaprMQ queues: metric, replicas asked for, trend (and with Kubernetes read: replicas, min/max, health) | Every 5 s |
| Queue → Scaling | Replica and metric charts for each ScaledObject scaling on the queue (only on a scaled queue) | Every 5 s |

Polling can be paused from the header. Browse views are never polled, because each page loads whole queue segments on
a worker.

**Browsing never changes the queue.** Every read goes through Dapr's actor-state API from a worker, so it never calls or
activates a queue actor and never takes a turn from Enqueue, Dequeue or Ack. What you see is a snapshot of committed
state: a message can be consumed between listing and opening it, which the drawer reports as "consumed since listed".

**Queues appear once they activate.** The list comes from the catalog ([ARCHITECTURE.md](ARCHITECTURE.md#catalog)),
which a queue joins the first time it activates on a worker. Queues that existed before upgrading appear the next time
they are used.

## Overview

The overview reads every catalogued queue's depth, and its DLQ's, from committed state through the same depth read as
the Queues list, then sums and ranks them. It has no metrics or history: there are no rates or charts.

- **Cost grows with the number of queues:** one catalog read per 1,000 queues and one worker depth call per 50, within
  the console's worker-call limit. The result is cached like other list reads, so it costs the same however many people
  have it open.
- **Capped at 10,000 queues.** Past that, it says the totals cover the first 10,000 only.
- **Session items aren't counted**, as in the Queues list. A session queue's sessions are on its Sessions tab.
- A failed depth read counts that queue as empty and is reported under the tiles.

## Scaling

The Scaling page, the queue's Scaling tab, the overview's KEDA-scaled tile and a topic's Consumers column show KEDA
ScaledObjects (and ScaledJobs) whose triggers use DaprMQ's scaler.

- **Where they come from.** The scaler reports every ScaledObject KEDA asks it about to a registry on the workers
  ([ARCHITECTURE.md](ARCHITECTURE.md#autoscaling-consumers-with-keda)), so one appears within a minute of KEDA's
  first call and drops off about five minutes after KEDA stops calling.
- **History.** Every 15 s the console reads each ScaledObject's queue depth, the same read the scaler makes, and keeps
  an hour of samples in memory. History is per console pod and starts again when the pod restarts. There is no
  Prometheus or metrics pipeline.
- **Asked for by metric** is `ceil(metric ÷ targetValue)`, or 0 at or below `activationValue`: the HPA's formula for
  KEDA's metric, before `minReplicaCount`/`maxReplicaCount`, the HPA's 10% tolerance and scale-down stabilization.
- **Replicas need Kubernetes read access.** KEDA never tells a scaler replica counts. With `KUBERNETES_READ` (Helm
  `console.kubernetesRead`, on by default in the chart; off in docker compose), the console's service account reads each ScaledObject (min/max, `Ready`/`Active`/
  `Fallback`/`Paused` conditions) and its HPA (current and desired replicas), or a ScaledJob and its running Jobs. That
  grant is cluster-wide: `get` on `scaledobjects`, `scaledjobs` and `horizontalpodautoscalers`, `list` on `jobs`.

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
- List, detail and overview reads are cached for `CONSOLE_CACHE_TTL_MS` (default 10 s), shared by every user. Browse pages are not
  cached.
- A worker's 400, 404 or 410 passes through unchanged. A transport failure or worker error is a 503.

| Operator setting | Default | |
|---|---|---|
| `CONSOLE_ENABLED` | `false` | Serve `/api/console/*` and the app. With it off, the operator is unchanged. |
| `CONSOLE_CACHE_TTL_MS` | `10000` | Shared cache for list, detail and overview reads. `0` disables it. |
| `CONSOLE_MAX_WORKER_CALLS` | `8` | Concurrent worker calls per operator pod. |
| `SCALER_ENABLED` | `true` | `false` on a console-only Deployment (Helm `console.separateDeployment`). |
| `SCALING_SAMPLE_INTERVAL_MS` | `15000` | How often the Scaling page's history is sampled. |
| `SCALING_HISTORY_MINUTES` | `60` | History kept per ScaledObject, in memory. |
| `KUBERNETES_READ` | `false` | Read ScaledObjects and HPAs with the pod's service account, for replica counts and health. |
| `SCALER_REGISTRATION_FLUSH_MS` | `10000` | How often the scaler reports new or changed ScaledObjects to the registry. |
