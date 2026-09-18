# DaprMQ Example Apps

Runnable producer/consumer examples for each DaprMQ client SDK — dotnet, java, python, typescript. Each language gets a producer container and a consumer container (8 containers total), deployed together via a single Helm chart. Every container logs its activity to stdout and exposes a small HTTP control API to view/override its DaprMQ connection settings, run a demo scenario on demand, and reset its own state.

See [`shared/API_CONTRACT.md`](shared/API_CONTRACT.md) for the exact control-plane API and [`shared/SCENARIOS.md`](shared/SCENARIOS.md) for what each scenario actually does.

## Prerequisites

- A `daprmq` release (the main chart at [`../helm`](../helm)) already installed in your target cluster/namespace. These examples don't install DaprMQ itself. Don't have one yet? [`helm/CONTRIBUTOR_GUIDE.md`'s Local Development Cluster section](../helm/CONTRIBUTOR_GUIDE.md#local-development-cluster-docker-desktop-kubernetes--kind) walks through a Postgres-backed install on Docker Desktop Kubernetes in a few commands - the exact setup these examples were verified end-to-end against.
- Docker, and a way to get locally-built images into your cluster (see below).
- Helm 3.0+.

## 1. Build the images

None of the four SDKs are published to a package registry, so every example image is built **from the repo root** so its Dockerfile can copy the SDK source directly from `sdks/<lang>/`:

```bash
./examples/shared/build-images.sh
```

This builds all 8 images (`daprmq-examples-<lang>-<role>:0.1.0`). To build one by hand instead:

```bash
# always from the repo root - not from examples/<lang>/<role>/
docker build -f examples/dotnet/producer/Dockerfile -t daprmq-examples-dotnet-producer:0.1.0 .
```

Then get the images into your cluster - which step you need depends on whether your cluster shares your host's Docker image cache:

**Docker Desktop Kubernetes (no extra step at all):** it runs on the same Docker Engine your `docker build` above just used, so the images are already visible to it. Just make sure `image.pullPolicy` stays `IfNotPresent` (the chart's default) so Kubernetes doesn't try to pull from a registry instead - skip straight to [step 2](#2-install-the-chart).

**`kind`/`minikube` (separate image store from your host):**

```bash
for img in $(docker images --filter "reference=daprmq-examples-*" --format '{{.Repository}}:{{.Tag}}'); do
  kind load docker-image "$img"   # or: minikube image load "$img"
done
```

**A local Docker registry** your cluster can pull from (e.g. `docker run -d --restart=always -p 5000:5000 --name registry registry:2`, or the one `kind`'s [local-registry guide](https://kind.sigs.k8s.io/docs/user/local-registry/) sets up):

```bash
./examples/shared/push-images.sh                       # builds + pushes to localhost:5000
REGISTRY=my-registry:5000 ./examples/shared/push-images.sh   # or a different host/port
```

then point the chart at it:

```bash
helm install daprmq-examples ./examples/helm -n <namespace> \
  --set image.registry=localhost:5000 \
  --set image.pullPolicy=Always
```

## 2. Install the chart

```bash
helm install daprmq-examples ./examples/helm -n <namespace>
```

No `--set` flags needed if your `daprmq` release's name contains `"daprmq"` (e.g. `daprmq`, `my-daprmq`) and lives in the same namespace — the chart computes the gateway Service name the same way the main chart's own templates do. Otherwise:

```bash
helm install daprmq-examples ./examples/helm -n <namespace> \
  --set daprmq.gatewayServiceName=<actual-gateway-service-name> \
  --set daprmq.namespace=<namespace-daprmq-is-installed-in>
```

## 3. Drive a scenario

Port-forward a producer and its matching consumer (separate terminals):

```bash
kubectl port-forward -n <namespace> svc/daprmq-examples-dotnet-producer 8080:8080
kubectl port-forward -n <namespace> svc/daprmq-examples-dotnet-consumer 8081:8080
```

Check config and health, then run a scenario on the producer, followed by the same scenario on the consumer:

```bash
curl http://localhost:8080/health
curl http://localhost:8080/config
curl -X POST http://localhost:8080/scenarios/basic/run

curl -X POST http://localhost:8081/scenarios/basic/run
```

Watch both pods' logs side by side to see the full narrative:

```bash
kubectl logs -f -n <namespace> deploy/daprmq-examples-dotnet-producer
kubectl logs -f -n <namespace> deploy/daprmq-examples-dotnet-consumer
```

Swap `dotnet` for `java`, `python`, or `typescript` to try the same scenario against another SDK — each language's pair uses its own queue-id prefix (`examples-<language>`), so all 8 pods can run against the same DaprMQ install without interfering with each other.

## Scenarios

| Scenario | What it shows | Producer does | Consumer does |
|---|---|---|---|
| `basic` | Enqueue/dequeue | Enqueues 3 plain items | Dequeues (with ack) and immediately acknowledges each |
| `ack-deadletter` | Acknowledgements + deadletters | Enqueues 3 items tagged `ack`/`deadletter`/`expire` | Acks one, dead-letters one, lets one lock expire and redeliver, then inspects the DLQ |
| `priority` | Fast lane vs normal | Enqueues 3 normal-priority items, then 3 fast-lane items | Dequeues all 10 in one call — fast-lane items surface first despite being enqueued later |
| `sessions` | Session ordering + exclusivity | Enqueues 2 items each to `session-a` and `session-b` | Accepts the first session via "any available" mode, the second via a targeted `sessionId`, dequeuing/acking/releasing each in turn |
| `idempotency` | Message deduplication | Enqueues an item, a duplicate reusing its idempotency key, then a distinct item | Dequeues and acknowledges the 2 survivors, confirming the duplicate was silently dropped |

Full request/response detail: [`shared/SCENARIOS.md`](shared/SCENARIOS.md).

## Notes

- **`POST /reset`** clears a pod's own in-memory state (cached lock/session ids, config overrides) — it does **not** delete anything from the real DaprMQ queues (there's no delete-queue API). Re-running a scenario after a reset may pick up leftover items from earlier runs; that's expected.
- **`PUT /config`** lets you point a running pod at a different DaprMQ gateway or queue prefix without redeploying — handy for pointing an example at a second `daprmq` release, or for local testing against `docker-compose`.
- Run `helm test daprmq-examples -n <namespace>` to confirm all 8 services answer `/health`.

## Troubleshooting

- **A consumer scenario returns `502 UPSTREAM_ERROR`, but the producer's own scenario succeeded**: check whether the `daprmq` gateway itself is running a stale image. A `DequeueLocked` call against an old server binary can silently behave like a plain dequeue (no `lockId` in the response), which then makes the following `Acknowledge`/`DeadLetter` call fail with a 400 that surfaces here as `UPSTREAM_ERROR`. Rebuild and redeploy the server per [helm/CONTRIBUTOR_GUIDE.md's Fast Development Loop](../helm/CONTRIBUTOR_GUIDE.md#fast-development-loop), then retry.
- **Confirmed working configuration**: all 4 scenarios were run end-to-end for all 4 languages against Docker Desktop Kubernetes, with the main chart's server rebuilt from current source (`image.tag=dev`, `pullPolicy=Never`) and a Postgres state store installed via the bitnami chart, per [helm/CONTRIBUTOR_GUIDE.md's Local Development Cluster section](../helm/CONTRIBUTOR_GUIDE.md#local-development-cluster-docker-desktop-kubernetes--kind).
