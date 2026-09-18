# DaprMQ Helm Chart - Contributor Guide

This document is for people developing or testing **this chart and the DaprMQ server itself** - spinning up a throwaway local cluster, building the server from source, and iterating quickly. If you just want to install DaprMQ into a real environment, see [README.md](README.md) instead.

## Local Development Cluster (Docker Desktop Kubernetes / kind)

Verified end-to-end against Docker Desktop's built-in Kubernetes with a real Postgres-backed state store - no external database or registry needed.

**1. Postgres, namespace, and the state store Component:** follow [README.md's Prerequisites](README.md#prerequisites) as written - it already uses the recommended `daprmq` namespace and installs Postgres via the bitnami chart, which is exactly what this local setup needs too. Come back here once that's done.

**2. Install from source instead of README's Quick Start:** rather than the packaged/registry image, build the server locally and point the chart at it:

```bash
docker build -t daprmq:dev ./dotnet
helm install daprmq ./helm \
  -n daprmq \
  --set dapr.stateStoreName=statestore \
  --set image.tag=dev \
  --set image.pullPolicy=Never
```

**Docker Desktop Kubernetes** shares your host's Docker image cache directly with the cluster's own container runtime - a plain `docker build` is immediately visible to pods with `imagePullPolicy: IfNotPresent`/`Never`, no push or `kind load`/`minikube image load` step needed. For `kind`/`minikube`, add a `kind load docker-image daprmq:dev` (or `minikube image load daprmq:dev`) between the build and install steps, since those runtimes keep a separate image store from your host's.

Following README.md's recommended namespace/release name here means the [example apps](../examples/README.md) chart works against this install with zero `--set` flags.

## Fast Development Loop

Rebuild the server from source and roll it out in one command - `image.pullPolicy=Never` forces Kubernetes to use the freshly built local image instead of trying (and failing, or silently using a stale cached one) to pull `daprmq:dev` from a registry:

```bash
docker build -t daprmq:dev ./dotnet && helm upgrade --install daprmq ./helm \
  --set image.tag=dev \
  --set image.pullPolicy=Never \
  --set dapr.stateStoreName=statestore
```

This is also the fix if `helm test` or a client starts getting unexpected 400s/missing fields against a deployment that's been running for a while - the currently-loaded image can predate recent server changes (e.g. a `Dequeue`/`DequeueLocked` header or response field added since the image was last built), and re-running this loop against current source resolves it.

## Running the Example Apps Against Your Local Cluster

Once the steps above are up, [`examples/README.md`](../examples/README.md) walks through building and deploying the producer/consumer example apps against this install with zero extra configuration.
