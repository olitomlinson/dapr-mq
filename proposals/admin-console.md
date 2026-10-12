# Admin console

**Status:** proposed. Delivered as five PRs (see [Delivery](#delivery)).

## Context

DaprMQ has no way to see what exists or what is inside it:

- **Nothing lists queues or topics.** Queues are Dapr virtual actors that exist only by id, and
  Dapr actor state has no key enumeration ([QueueActor.cs](../server/src/DaprMQ/QueueActor.cs)).
- **Nothing browses messages without consuming them.** `IQueueActor` has only consuming operations,
  so the only way to see what is in a DLQ is to dequeue from it.
- **Nothing records throughput.** The only meter is `DaprMQ.Delivery`
  ([ActorCall.cs](../server/src/DaprMQ.Interfaces/ActorCall.cs)), and nothing exports it.
- **`dashboard/` is a test playground**, built for enqueue/dequeue and WireMock. It is not an admin
  tool.

## Proposal

A read-only React admin console that:

- lists queues, DLQs, topics, subscriptions and HTTP sinks, with their configuration;
- browses ready, locked and dead-lettered messages, and opens any message's payload;
- charts production, consumption, ack, nack, expiry and dead-letter rates, plus depth over time;
- overlays KEDA's target and actual consumer counts on the charts of KEDA-scaled queues.

**Decisions:**

- Queues, topics and sinks are discovered through a registry actor, `CatalogActor`.
- Metrics come from OpenTelemetry, with Prometheus as the time-series store.
- The console is a new `console/` app. `dashboard/` stays as the dev playground.
- v1 is read-only and has no auth. The console Service is ClusterIP-only and reached by
  port-forward or the operator's own ingress auth.

### Architecture

The backend lives in `DaprMQ.Operator`. Despite the name it is a control-plane service, not a
Kubernetes operator. It already has the pattern the console needs. Dapr keys actor state by the
caller's app-id, so only workers can read `QueueActor` state, and the operator reads it by calling
worker-internal endpoints over Dapr service invocation
([WorkerDepthClient.cs](../server/src/DaprMQ.Operator/Scaling/WorkerDepthClient.cs),
[QueueDepthEndpoint.cs](../server/src/DaprMQ.ApiServer/Endpoints/QueueDepthEndpoint.cs)).

```
browser ─► operator  /api/console/*  (also serves the SPA)
              │  Dapr service invocation (mTLS)
              ├─► worker  /internal/admin/*  ─► Dapr actor-state API ─► Postgres
              └─► Prometheus  (named range queries only)
```

**The console never calls or activates a queue actor.** Every read goes through Dapr's actor-state
API ([QueueActorStateReader.cs](../server/src/DaprMQ/QueueActorStateReader.cs)), which reads
committed rows directly. Browsing never takes an actor's turn and never contends with
Enqueue, Dequeue or Ack. Postgres readers do not block writers. What the console shows is a snapshot
of committed state.

## 1. Catalog (registry)

### `CatalogActor`

- **Sharding.** 64 shards, `catalog-{n}`, where n is a stable FNV-1a hash of the entity id.
  `string.GetHashCode` is randomised per process, so it can't be used.
- **Entries:** `CatalogEntry { Id, Kind: Queue | DeadLetter | Topic | Sink, ParentId?, RegisteredAt }`.
- **Storage** uses the segmented pattern: append-only `entries_seg_{n}` keys of 100 entries each,
  plus `metadata` holding the segment count.
- **Duplicates.** On activation the shard loads its ids into an in-memory set, so registering a
  known id costs no write. At 100k entities a shard holds about 1.6k entries, or 16 segments.
- **`RegisterBatchAsync(entries)`** writes only new ids, in one `SaveStateAsync`.
- **Reads.** The console reads shards through the state reader and never calls the actor.
- Follow the existing invoker pattern: add a `CatalogActorInvoker` and `ActorMethodNames`
  constants.

### Registration, off the actor's turn

Registration never adds a cross-actor call to an actor's turn:

1. The actor writes its id to a worker-local channel (`CatalogRegistrar`) without blocking.
   `QueueActor.OnActivateAsync` does this on every activation, and so does
   `TopicActor.OnActivateAsync`. `HttpSinkActor` does it on register.
2. The registrar keeps an in-memory set of ids it has already sent in this process, and drops
   repeats.
3. A background service sends batches, at most one call per shard per second.

No flag is persisted in actor metadata.

- **Queues that already exist** appear the next time they activate. Activating once per process
  costs one channel write.
- **A lost batch** (for example, a worker crash) is retried the next time that actor activates,
  because nothing records that it was sent.

**Kinds:**

- An id ending `-deadletter` registers as `DeadLetter`, with its parent queue as `ParentId`.
- Session actors (`-session-`) are skipped. Sessions are listed from the SessionCoordinator
  directory instead.
- Subscriptions are not catalogued. They come from each topic's `SubscriberIds` and dispatch
  cursors, so they are always current.

There is no deregistration in v1, because queues are never deleted. An empty queue shows as empty.

## 2. Worker read-only admin endpoints

- **Routes:** `server/src/DaprMQ.ApiServer/Endpoints/AdminReadEndpoints.cs`, mapped beside
  `MapQueueDepthEndpoint` whenever actors are registered.
- **Logic:** `AdminReadService` in `server/src/DaprMQ/`.
- **Models:** `server/src/DaprMQ.Interfaces/AdminModels.cs`.
- **State reader:** `IQueueActorStateReader` gains a generic `ReadStateAsync<T>(actorType, actorId,
  key)`. Today's `ReadMetadataAsync` becomes a call to it.

| Route (GET) | Returns |
|---|---|
| `/internal/admin/catalog?kind=&cursor=&limit=` | Merged entries from all shards, read in parallel, paged |
| `/internal/admin/queues/{id}` | Per-priority count and head/tail segments, `LockCount`, `NextSequence`, config (segment size, dedup), session lease, `ErrorMessage` (the corrupt flag), DLQ depth, sink config if `{id}-sink` exists, global `MaxDeliveryCount` |
| `/internal/admin/queues/{id}/messages?priority=&cursor=&limit=` | Ready items walked head to tail through `queue_{p}_seg_{n}`. Each item: `Sequence`, `DeliveryCount`, a 1 KB payload preview, whether it is a blob reference, and a location cursor `{p}:{seg}:{idx}`. `limit` ≤ 100. |
| `/internal/admin/queues/{id}/messages/{location}?sequence=` | Full item, capped at 1 MB. **410** if the item at that location no longer has that sequence (consumed or moved). |
| `/internal/admin/queues/{id}/locks?cursor=&limit=` | Walks `LockExpiryBuckets` → `locks_exp_{b}` → `{lockId}-lock`, or `locks_session` on a session actor. Each lock: id, created and expiry times, priority, sequence, delivery count, competing-consumer mode, preview. `limit` ≤ 100. |
| `/internal/admin/queues/{id}/locks/{lockId}` | The full locked item |
| `/internal/admin/queues/{id}/sessions?cursor=&limit=` | One page of the coordinator directory, with each session's depth and lease. The directory is never read in full. |
| `/internal/admin/topics/{id}` | Subscribers, each subscriber's lag (`NextSequence − cursor`), circuit-breaker state, generation, publish-log counts |
| `/internal/admin/objects/{ref}` | Resolves a `BlobReferenceEnvelope` through `IObjectStore`, capped at 1 MB. Called only when the user clicks "load payload". |

A DLQ is browsed as `queues/{id}-deadletter/messages`, so it needs no route of its own.

## 3. Metrics (OpenTelemetry → Prometheus)

### Counters

**Meter `DaprMQ.Queue`:**

- Counters: `daprmq.queue.enqueued`, `dequeued`, `acknowledged`, `nacked`, `deadlettered` and
  `lock_expired`. Each is incremented only after `SaveStateAsync` succeeds, using a `TagList`
  so no allocation happens.
- Tags: `queue` and `kind` (`queue` or `deadletter`). A session actor is tagged with its parent
  queue plus `session=true`, so session ids never become labels.

**Meter `DaprMQ.Topic`:** `daprmq.topic.published` and `daprmq.topic.dispatched{subscriber}`.

**Cardinality** is configurable through `DAPRMQ_METRICS_QUEUE_LABEL`:

- `all`: one series per queue.
- `none`: aggregate counters only.
- `allowlist`: per-queue series only for the queues listed.

The OTel cardinality limit is set explicitly rather than left at the SDK default.

### Depth gauge, from worker memory with no extra I/O

The worker holds a singleton `HostedQueueDepths`: a `ConcurrentDictionary` of queue →
`(ready, locked, updatedAt)`. It is injected into `QueueActor`'s constructor.

- **Updates.** `QueueActor` overrides `OnPostActorMethodAsync`, which Dapr runs only after a method
  completes successfully. It reads `metadata` from the StateManager's cache (no I/O) and records
  the sum of `Queues[p].Count` and the `LockCount`. `OnActivateAsync` records it too. That's one
  hook instead of the 19 `SaveStateAsync` call sites.
- **Deactivation keeps the last value.** Idle queues deactivate, and a stuck queue is the one most
  worth seeing. Its depth can change only inside the actor, so the last value stays true until the
  actor activates again.
- **Export.** Observable gauges `daprmq.queue.depth{queue,state}` and
  `daprmq.queue.depth_updated{queue}`. Session actors are summed under their parent queue.
- **Two workers reporting one queue.** After a rebalance, the old worker's entry is stale. The
  operator's query keeps only the series with the newest `depth_updated` for each queue.
- **Restarts.** A restarted worker forgets its idle queues until they next activate. That leaves a
  gap in depth history only: current depth in the UI always comes from a state read.

This gauge is for history only. It is **not** suitable for KEDA: a missing entry would read as 0
and scale consumers to zero (see [the KEDA sessions issue](../docs/issues/keda-sessions-depth-read-is-unbounded.md)
for the scaler's own fix).

### Export

- Add `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.Prometheus.AspNetCore` to the
  ApiServer.
- Map `/metrics` on workers and gateways, including the existing `DaprMQ.Delivery` meter.
- OTLP export is enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

### Operator Prometheus client

- **Route:** `/api/console/metrics/{name}?scope=&id=&range=&step=` runs a fixed set of named PromQL
  range queries, such as `rate(daprmq_queue_enqueued_total{queue="X"}[1m])`. Arbitrary PromQL is
  never accepted.
- **No `PROMETHEUS_URL` configured:** the route returns **501**, and the UI shows "metrics not
  configured".

## 4. KEDA scaling overlay

The charts of KEDA-scaled queues and subscriptions show the target and actual number of consumer
instances.

### Linking a queue to its ScaledObject

No Kubernetes API access is needed. Every KEDA call into the scaler carries
`ScaledObjectRef { name, namespace, scalerMetadata }`
([externalscaler.proto](../server/src/DaprMQ.Operator/Protos/externalscaler.proto)), and
`scalerMetadata.queueId` names the queue.

- `DaprMQExternalScaler` records the link on every call, in an in-memory `ScalerBindingRegistry`
  with a last-seen time.
- A link expires after 5 minutes without a call, so deleted ScaledObjects drop out. One queue may
  have several ScaledObjects.
- The operator exports two gauges:
  - an info gauge, `daprmq_scaler_binding{queue, mode, namespace, scaled_object, target_value, activation_value} 1`;
  - `daprmq_scaler_metric_value{queue, mode, scaled_object}`, the value last reported to KEDA.

### Target and actual replicas

These come from kube-state-metrics, which ships with kube-prometheus-stack. KEDA names its HPA
`keda-hpa-{scaledObject}`.

| Series | Source |
|---|---|
| Target | `kube_horizontalpodautoscaler_status_desired_replicas` |
| Actual | `kube_deployment_status_replicas` (or the StatefulSet equivalent), joined through `kube_horizontalpodautoscaler_info{scaletargetref_kind,scaletargetref_name}` |
| Min/max band | `kube_horizontalpodautoscaler_spec_{min,max}_replicas` |

- **Why actual doesn't come from the HPA:** its `current_replicas` never shows 0, because an HPA's
  minimum is 1 even while KEDA holds the workload at 0.
- **ScaledJobs** have no HPA. They are charted as active jobs, labelled "jobs".
- **Custom HPA names** (`advanced.horizontalPodAutoscalerConfig.name`) are supported through an
  optional `hpaName` scaler metadata key.
- **Without kube-state-metrics**, the chart shows only an estimated target,
  `ceil(metric_value / target_value)`, and omits the actual series.

`/api/console/metrics/scaling?queue=&range=` returns the links and these series.

## 5. Operator console API and static hosting

- **`Console/WorkerAdminClient.cs`** makes GET calls to the worker app-id over Dapr service
  invocation, the same way `DaprWorkerDepthClient` does.
- **`Console/ConsoleEndpoints.cs`** is a thin `/api/console/*` mirror of section 2, plus metrics.
  - A worker's 404 or 410 passes through unchanged. A transport failure maps to 503.
  - About 8 worker calls can be in flight at once, so console load stays small and predictable.
  - A response cache is shared by all users: 10 s for list and detail reads. Browse pages are not
    cached.
- **The SPA** is served from `wwwroot` (`UseStaticFiles`, falling back to `index.html`) on the
  existing HTTP port.
- **Gating.** Everything is behind `CONSOLE_ENABLED`. With it off, the operator is unchanged.
- **Image.** `Dockerfile.operator` gains a node stage that builds `console/` into `wwwroot`.
- **Isolation from KEDA.** The console can run as its own Deployment of the same image
  (`console.separateDeployment`, with the scaler disabled there). Then console load can never
  delay KEDA's `GetMetrics`.

## 6. `console/` app

**Stack:**

- Vite, React 18 and TypeScript, matching `dashboard/`.
- react-router and TanStack Query.
- Recharts for charts, CSS Modules for styling.
- Vitest, Testing Library and MSW for tests.

**Refresh:**

- **Polled** (every 5 s by default, and pausable): list, detail and metrics views.
- **Manual refresh only:** message, lock and session browsing. Those views are never polled.

**Pages:**

- **Overview:** counts of queues and topics, total ready and locked items, DLQs with messages, total
  throughput, and the top queues by depth and by DLQ depth.
- **Queues:** a filterable table of id, kind, ready, locked, DLQ, and in/out rates, with a depth
  sparkline. DLQs are folded under their parent queue, and subscription queues link to their topic.
- **Queue detail**, in tabs:
  - **Overview:** config, priority breakdown, lease and corrupt state, sink.
  - **Messages** and **Dead-letter:** paged browsing, filterable by priority.
  - **Locked:** lock countdowns and delivery counts.
  - **Sessions:** paged.
  - **Metrics:** rates, depth over time, and the KEDA overlay.
- **Message drawer:** JSON, raw or hex views. Shows sequence, delivery count and lock info. Has a
  "load payload" button for blob references. A 410 shows as "consumed since listed".
- **Topics** and **topic detail:** subscribers with lag, circuit-breaker state, publish and dispatch
  rates. Each subscriber links to its subscription queue, `{topic}-sub-{sub}`.
- **Sinks:** URL, max concurrency, lock TTL and current polling interval.

### Wireframes (rough)

**App shell**
```
+----------------------------------------------------------------------------------+
| DaprMQ Console                     [ search queues/topics...      ]  ⟳ 5s [⏸]    |
+--------------+-------------------------------------------------------------------+
| ▸ Overview   |                                                                   |
|   Queues     |                    (page content)                                 |
|   Topics     |                                                                   |
|   Sinks      |                                                                   |
| ──────────── |                                                                   |
| Prometheus ● |                                                                   |
| Workers  ● 3 |                                                                   |
+--------------+-------------------------------------------------------------------+
```

**Overview**
```
+-------------------------------------------------------------------------------+
| [ Queues 42 ]  [ Topics 6 ]  [ Ready 12,480 ]  [ Locked 311 ]  [ DLQ ⚠ 27 ]   |
+-------------------------------------------------------------------------------+
| Throughput (msg/s)                                    [15m|1h|6h|24h]          |
|  400 ┤        ╭─╮          ── enqueued                                        |
|  200 ┤ ╭──────╯ ╰───╮╭──   ── dequeued                                        |
|    0 ┼─╯            ╰╯     ── dead-lettered                                   |
+---------------------------------------+---------------------------------------+
| Top queues by depth                   | DLQs with messages                    |
| orders            8,210  ▁▂▄▆█        | orders-deadletter        19           |
| emails            2,004  ▂▂▂▃▂        | payments-deadletter       8           |
+---------------------------------------+---------------------------------------+
```

**Queues list**
```
+------------------------------------------------------------------------------------+
| Queues                [ filter... ] [Kind: All ▾] [☐ hide empty] [☐ hide subs]     |
+------------------------------------------------------------------------------------+
| Queue ▾          | Kind  | Ready | Locked | DLQ | In/s | Out/s | Depth (1h)        |
|------------------+-------+-------+--------+-----+------+-------+-------------------|
| orders           | queue | 8,210 |    120 |  19 |  210 |   180 | ▁▂▃▄▆█            |
|  └ orders-dlq    | dlq   |    19 |      0 |   – |    0 |     0 | ▁▁▁▁▂▂            |
| news-sub-audit   | sub ↗ |    12 |      0 |   0 |    3 |     3 | ▁▁▁▁▁▁            |
|                                                  « 1 2 3 »   showing 1-50 of 142   |
+------------------------------------------------------------------------------------+
```

**Queue detail: Overview tab**
```
+-----------------------------------------------------------------------------------+
| ‹ Queues / orders                                     [ ⚠ CORRUPT banner if set ] |
| [Overview] [Messages 8,210] [Locked 120] [Dead-letter 19] [Sessions] [Metrics]    |
+---------------------------------------+-------------------------------------------+
| Segment size        100               | P0 (fast) ███░░░░░░░   1,210              |
| Dedup               on                | P1        ████████░░   6,500              |
| Max delivery count  10 (global)       | P2        █░░░░░░░░░     500              |
| Next sequence       1,048,221         | Locked: 120   DLQ: 19 → view              |
+---------------------------------------+-------------------------------------------+
| HTTP sink: https://svc/orders   concurrency 8   lock TTL 30s   interval 2s         |
+-----------------------------------------------------------------------------------+
```

**Queue detail: Messages tab** (the Dead-letter tab has the same layout)
```
+-----------------------------------------------------------------------------------+
| [Priority: All ▾]    Snapshot at 12:04:31  (items may have moved)        [Refresh]|
+-----------------------------------------------------------------------------------+
| Seq       | Pri | Deliveries | Payload preview                         | Size     |
| 1,040,011 |  0  |     0      | {"orderId":"A-991","total":42.1,...     | 312 B    |
| 1,040,015 |  1  |     2      | {"orderId":"A-993","total":8.0,...      | 298 B    |
| 1,040,016 |  1  |     0      | 📦 blob reference                        | 4.2 MB   |
|                                                         [ Load next 100 ▸ ]       |
+-----------------------------------------------------------------------------------+
```

**Queue detail: Locked tab**
```
| Lock id   | Seq       | Pri | Deliveries | Expires in    | Mode      | Preview |
| 7f3a…c21  | 1,039,990 |  1  |     1      | ██████░ 24s   | competing | {"or…   |
| 91be…004  | 1,039,991 |  1  |     3      | █░░░░░░  4s ⚠ | competing | {"or…   |
```

**Message drawer**
```
+-----------------------------------------+
| Message  seq 1,040,015            [✕]   |
| Queue: orders   Priority: 1             |
| Deliveries: 2 / 10   Location: 1:412:7  |
| [JSON] [Raw] [Hex]              [Copy]  |
| { "orderId": "A-993", "total": 8.0 }    |
| blob ref → [ Load payload (4.2 MB) ]    |
| 410 → "Consumed since listed"           |
+-----------------------------------------+
```

**Queue detail: Metrics tab, with the KEDA overlay**
```
+-----------------------------------------------------------------------------------+
| [15m|1h|6h|24h]                                                                   |
| Produced vs consumed (msg/s)          | Ack / Nack / Expired / DLQ (msg/s)        |
| Depth (ready / locked / DLQ)          ▁▂▄▆█▇▅▃▂▁                                  |
| Consumers  scaledobject: orders-consumer (ns: shop)  target 10/instance           |
|   8 ┤ - - - - - - - - - - - - - - - - - - - max                                   |
|   6 ┤        ┌──────┐            ── desired (KEDA/HPA)                            |
|   4 ┤     ┌──┘  ┌───┴──┐         ── actual replicas                               |
|   0 ┼─────┘─────┘      └───────  ░░ min                                           |
+-----------------------------------------------------------------------------------+
```

**Topic detail**
```
+-----------------------------------------------------------------------------------+
| ‹ Topics / news     generation 4   publish log: 1,204 entries                     |
| Publish vs dispatch (msg/s)                                    [15m|1h|6h|24h]    |
| Subscriber | Queue             | Lag | Dispatched/s | Breaker                     |
| audit      | news-sub-audit ↗  |   0 |      40      | ● closed                    |
| search     | news-sub-search ↗ | 120 |      31      | ○ open (2m, 5 errors)       |
+-----------------------------------------------------------------------------------+
```

## 7. Helm and compose

- **`helm/values.yaml`** gains a `console` section:
  - `enabled`, which fails the render unless `operator.enabled` is also set;
  - `prometheusUrl`;
  - `separateDeployment`;
  - an optional `ingress`;
  - an `operator-console` ClusterIP Service on the HTTP port.

  It also gains `metrics.serviceMonitor.enabled`, plus scrape annotations on workers and gateways.
- **`docker-compose.yml`** gains a `prometheus` service and the operator with `CONSOLE_ENABLED=true`,
  on port 9100.
- **Docs:** a new `docs/CONSOLE.md`, and a metrics section in `docs/ARCHITECTURE.md`. Both state the
  security posture: anyone who can reach the console Service can read message payloads.

## Performance impact on the broker

| Touch point | Impact | Why |
|---|---|---|
| Catalog registration | 🟢 | A non-blocking channel write per actor activation. Batched, at most one call per shard per second, and no write for ids a shard already knows. |
| OTel counters in actors | 🟢 | In-memory and lock-free, with no allocation. Increments happen after the save, on the same turn. |
| Depth gauge | 🟢 | A dictionary write per actor method, from cached state. No I/O. |
| Metric cardinality | 🟢 / 🟡 | Series count is queues × counters × workers. Bounded by `DAPRMQ_METRICS_QUEUE_LABEL` and an explicit cardinality limit. |
| Queue list and detail reads | 🟢 | One to three key reads per queue, behind a shared 10 s cache, with at most 8 calls in flight. |
| Message browse | 🟡 | Reading one 100-item segment loads the full payloads, even though only 1 KB previews go back. With large inline payloads that is real I/O and GC pressure on worker pods. User-initiated, paged and never polled. Blob offload keeps segments small. |
| Lock browse | 🟢 / 🟡 | One read per lock (the actor-state API has no bulk get), at most 100 per page, never polled. |
| Sessions browse | 🟢 | One page of the directory at a time, never the whole directory. |
| Sidecar Postgres pool | 🟢 | Console reads share connections with actor writes, but the 8-call limit bounds them. |
| Operator shared with the KEDA scaler | 🟢 | The concurrency limit applies, and the console can optionally run as its own Deployment. |
| KEDA overlay, Prometheus queries | 🟢 | Run in the operator and Prometheus only. They don't touch the broker. |

These ratings come from how the state is laid out, not from measurements. Message browse, lock
browse and the shared Postgres pool should be measured with the perf harness under sustained load
once the worker endpoints exist.

## Testing (TDD order, per CLAUDE.md)

1. **Integration test (HTTP contract):** after an enqueue, `/api/console/queues` lists the queue and
   `/messages` returns its items, without changing depth.
2. **Unit tests** in `server/tests/DaprMQ.Tests/`:
   - `CatalogActorTests`: a known id causes no write, a new segment rolls over at 100 entries, the
     id set loads on activation.
   - `CatalogRegistrarTests`: repeats are dropped, batches are grouped by shard, the
     one-per-second rate holds.
   - Registration hooks in `QueueActorTests` and `TopicActorTests`: sessions are skipped, DLQs
     get a `ParentId`.
   - `AdminReadServiceTests`: segment paging, the lock walk, 410 on a sequence mismatch.
   - `AdminReadEndpointsTests`.
   - `QueueActorMetricsTests`: uses `MeterListener` to show nothing is counted when a save fails.
   - `HostedQueueDepthsTests`.
   - `OperatorConsoleEndpointsTests`: mocked `WorkerAdminClient`, the concurrency limit, the cache.
   - `PrometheusQueryClientTests`: named queries only, 501 when unconfigured, the scaling joins.
   - `OperatorScalerBindingRegistryTests`.
3. **Console:** Vitest component tests for each page against MSW fixtures.
4. **KEDA kind CI:** add kube-state-metrics, and assert that the scaling series appear after a
   scale-up.

## Verification

- `dotnet test` in `server/tests/DaprMQ.Tests/` passes, and `./build-and-test.sh` passes before each
  commit.
- `cd console && npm test && npm run build` passes.
- **Compose:**
  1. Fill the system with test data:
     - enqueue to several queues, including some priority 0 items;
     - lock-dequeue some items;
     - nack one item past `MaxDeliveryCount`;
     - publish to a topic with two subscribers.
  2. Open the console at `http://localhost:9100` and confirm:
     - every queue, the DLQ and the topic are listed;
     - the counts match;
     - payloads open;
     - lock countdowns tick;
     - subscriber lag shows.
  3. Run a producer loop and confirm the rate charts move.
  4. Confirm browsing doesn't change depth.
- **Helm**, on the docker-desktop cluster: install with `operator.enabled=true,console.enabled=true`,
  port-forward the console Service, and repeat the smoke test.

## Delivery

Each PR is usable on its own:

1. **Catalog:** `CatalogActor`, `CatalogRegistrar`, the registration hooks and
   `/internal/admin/catalog`.
2. **Worker admin read endpoints.**
3. **Browsing:** the operator console API, static hosting and the browse UI.
4. **Charts:** OTel metrics, the depth gauge, Prometheus and the charts.
5. **The KEDA scaling overlay.**

## Open questions

1. **Authentication.** v1 relies on network access to the Service. Should OIDC or bearer-token
   auth come before any write action is added (DLQ redrive, purge, lock release)?
2. **Payload redaction.** Should there be a per-queue switch that hides payloads from the console
   for queues carrying sensitive data?
3. **Catalog pruning.** Queues are never deleted today. If a delete operation is added, the catalog
   will need a matching deregistration.
