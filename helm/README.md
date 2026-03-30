# DaprMQ Helm Chart

This Helm chart deploys DaprMQ, a Dapr actor-based FIFO queue system, to Kubernetes.

## Prerequisites

Before installing this chart, ensure the following requirements are met:

1. **Kubernetes cluster** (v1.24+)
2. **Helm** (v3.0+)
3. **Dapr control plane** installed in cluster (v1.17.0+)
   ```bash
   dapr init -k
   ```
4. **State store database** configured (PostgreSQL, Redis, CosmosDB, etc.)
5. **Dapr state store Component CR** deployed to cluster

### Example: Deploy PostgreSQL State Store Component

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: my-statestore
  namespace: <your-namespace>  # MUST match where you install DaprMQ
spec:
  type: state.postgresql
  version: v2
  metadata:
    - name: connectionString
      value: "host=postgres user=postgres password=secret port=5432 database=actor_state"
    - name: actorStateStore
      value: "true"
    - name: tablePrefix
      value: daprmq_
```

Apply the component:
```bash
kubectl apply -f statestore.yaml -n <your-namespace>
```

**Important Notes:**
- The state store Component CR must be deployed to the **same namespace** where you install this Helm chart. Dapr Components are namespace-scoped.
- **Multiple installations**: Multiple DaprMQ instances in the same namespace can share the same state store Component. The actor runtime handles isolation using the app-id.
- You must specify the state store name when installing the chart - there is no default value.

## Installation

### Quick Start

```bash
# Choose your target namespace
NAMESPACE="my-app-namespace"

# Create namespace (if it doesn't exist)
kubectl create namespace $NAMESPACE

# Deploy state store Component to the same namespace
kubectl apply -f statestore.yaml -n $NAMESPACE

# Install chart (stateStoreName is REQUIRED)
helm install my-daprmq ./helm \
  -n $NAMESPACE \
  --set dapr.stateStoreName=my-statestore
```

> **Namespace Selection**: You must explicitly specify the target namespace using `-n` or `--namespace`. There is no default namespace. Choose a namespace that fits your organization's conventions (e.g., `production`, `app-name`, `dapr-apps`, etc.).

> **State Store Name**: The `dapr.stateStoreName` parameter is **required** and must match the name of your Dapr state store Component deployed in the same namespace.

### Custom Installation

Create a custom `values.yaml`:

```yaml
# Custom image
image:
  registry: myregistry.azurecr.io
  repository: daprmq
  tag: "1.0.0"

# Protocol selection
dapr:
  protocol: "http"  # or "grpc"
  stateStoreName: "my-statestore"

# Scale worker
worker:
  replicaCount: 3
  resources:
    requests:
      cpu: 500m
      memory: 512Mi

# Enable autoscaling
autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 10
```

Install with custom values:
```bash
helm install my-daprmq ./helm -n $NAMESPACE -f my-values.yaml
```

### Multiple Installations Example

You can install multiple DaprMQ instances in the same namespace, sharing a single state store:

```bash
# Example: Two DaprMQ instances in the same namespace sharing a state store

# Deploy one state store Component
kubectl apply -f statestore.yaml -n shared  # name: shared-statestore

# Install first instance
helm install daprmq-app1 ./helm \
  -n shared \
  --set dapr.stateStoreName=shared-statestore

# Install second instance (different release name, same state store)
helm install daprmq-app2 ./helm \
  -n shared \
  --set dapr.stateStoreName=shared-statestore

# Result:
# - daprmq-app1-daprmq-worker and daprmq-app1-daprmq-gateway (using shared-statestore)
# - daprmq-app2-daprmq-worker and daprmq-app2-daprmq-gateway (using shared-statestore)
# Both installations are isolated by their unique app-ids
```

## Configuration

### Global Settings

| Parameter | Description | Default |
|-----------|-------------|---------|
| `image.registry` | Container registry | `""` |
| `image.repository` | Image repository | `daprmq` |
| `image.tag` | Image tag | `latest` |
| `image.pullPolicy` | Image pull policy | `IfNotPresent` |

### Dapr Configuration

| Parameter | Description | Default |
|-----------|-------------|---------|
| `dapr.protocol` | Dapr sidecar protocol (`http` or `grpc`) | `http` |
| `dapr.logLevel` | Dapr sidecar log level | `info` |
| `dapr.stateStoreName` | **REQUIRED:** Name of Dapr state store Component | `""` (must be set) |

**Important:** The `dapr.protocol` setting controls which protocol the **gateway** uses:
- `http`: Gateway uses REST API on port 8080 for external communication
- `grpc`: Gateway uses gRPC on port 8081 for external communication (lower latency)

**Note:**
- **Worker nodes always use HTTP (port 8080)** for Dapr communication - this is required for actor registration and invocation
- **Gateway nodes use the configured protocol** (HTTP or gRPC) - this controls how external clients communicate with the gateway
- The DaprMQ application **always exposes both ports** (8080 for HTTP, 8081 for gRPC) on all nodes
- Health checks always use port 8080 (HTTP) regardless of the protocol setting

### Worker Configuration

| Parameter | Description | Default |
|-----------|-------------|---------|
| `worker.enabled` | Enable worker deployment | `true` |
| `worker.replicaCount` | Number of actor host replicas | `2` |
| `worker.httpPort` | HTTP REST API port (always exposed) | `8080` |
| `worker.grpcPort` | gRPC API port (always exposed) | `8081` |
| `worker.resources.requests.cpu` | CPU request | `250m` |
| `worker.resources.requests.memory` | Memory request | `256Mi` |
| `worker.resources.limits.cpu` | CPU limit | `500m` |
| `worker.resources.limits.memory` | Memory limit | `512Mi` |

### Gateway Configuration

| Parameter | Description | Default |
|-----------|-------------|---------|
| `gateway.enabled` | Enable gateway deployment | `true` |
| `gateway.replicaCount` | Number of gateway replicas | `1` |
| `gateway.httpPort` | HTTP REST API port (always exposed) | `8080` |
| `gateway.grpcPort` | gRPC API port (always exposed) | `8081` |
| `gateway.resources.*` | Resource limits (same as worker) | See values.yaml |

### Autoscaling Configuration

| Parameter | Description | Default |
|-----------|-------------|---------|
| `autoscaling.enabled` | Enable HorizontalPodAutoscaler | `false` |
| `autoscaling.minReplicas` | Minimum replicas | `2` |
| `autoscaling.maxReplicas` | Maximum replicas | `10` |
| `autoscaling.targetCPUUtilizationPercentage` | Target CPU percentage | `70` |

## Usage

### Access the Gateway

The gateway exposes both HTTP REST and gRPC APIs simultaneously on different ports.

**HTTP REST API (port 8080):**
```bash
# Port forward to gateway HTTP port
kubectl port-forward -n <namespace> deployment/<release-name>-gateway 8080:8080

# Test push operation
curl -X POST http://localhost:8080/queue/my-queue/push \
  -H "Content-Type: application/json" \
  -d '{"item":{"message":"hello"},"priority":1}'

# Test pop operation
curl -X POST http://localhost:8080/queue/my-queue/pop
```

**gRPC API (port 8081):**
```bash
# Port forward to gateway gRPC port
kubectl port-forward -n <namespace> deployment/<release-name>-gateway 8081:8081

# Test with grpcurl
grpcurl -plaintext -d '{"queue_id":"my-queue","item_json":"{\"message\":\"hello\"}","priority":1}' \
  localhost:8081 daprmq.DaprMQ/Push
```

**Note:** Both APIs are always available regardless of the `dapr.protocol` setting. The protocol setting only affects which API Dapr uses for actor invocations.

### Run Tests

```bash
helm test daprmq -n <namespace>
```

### Verify Deployment

```bash
# Check pods (should show 2/2 containers - app + Dapr sidecar)
kubectl get pods -n <namespace>

# Check Dapr sidecar annotations
kubectl describe pod -n <namespace> -l app.kubernetes.io/component=worker

# View logs
kubectl logs -n <namespace> -l app.kubernetes.io/component=worker -c daprmq

# View Dapr sidecar logs
kubectl logs -n <namespace> -l app.kubernetes.io/component=worker -c daprd
```

## Architecture

### Components

- **Worker** (`{release-name}-daprmq-worker`): Deployment with `REGISTER_ACTORS=true`
  - Hosts the actual queue actors
  - All replicas share the same Dapr app-id (derived from release name) for actor distribution
  - Scalable via HorizontalPodAutoscaler
  - Default: 2 replicas
  - Example: Release `my-app` creates app-id `my-app-daprmq-worker`

- **Gateway** (`{release-name}-daprmq-gateway`): Deployment with `REGISTER_ACTORS=false`
  - Entry point for external requests
  - Separate Dapr app-id from worker (derived from release name)
  - Routes requests to worker via Dapr service invocation
  - Default: 1 replica
  - Example: Release `my-app` creates app-id `my-app-daprmq-gateway`

### Dapr Integration

Each pod is automatically injected with a Dapr sidecar via annotations:

```yaml
dapr.io/enabled: "true"
dapr.io/app-id: "{release-name}-daprmq-worker"  # or "{release-name}-daprmq-gateway"
dapr.io/app-port: "8080"
dapr.io/app-protocol: "http"  # or "grpc"
dapr.io/config: "{release-name}-dapr-config"
```

The Dapr sidecar injector automatically creates Kubernetes Services for each app-id, so no explicit Service manifests are needed.

## Best Practices

### Namespace Strategy

This Helm chart is namespace-agnostic and follows Kubernetes best practices:

- **No default namespace**: You must explicitly specify the namespace at install time
- **Namespace isolation**: Each installation can live in its own namespace
- **Component co-location**: Deploy the Dapr state store Component to the same namespace as the chart
- **Multiple installations**: You can install multiple instances in the same namespace or different namespaces

Example multi-environment setup (different namespaces):
```bash
# Development environment
helm install daprmq-dev ./helm \
  -n development \
  --set dapr.stateStoreName=statestore

# Staging environment
helm install daprmq-staging ./helm \
  -n staging \
  --set dapr.stateStoreName=statestore

# Production environment
helm install daprmq-prod ./helm \
  -n production \
  --set dapr.stateStoreName=statestore
```

Example multi-tenant setup (same namespace, shared state store):
```bash
# Deploy shared state store
kubectl apply -f statestore.yaml -n multi-tenant

# Tenant A
helm install tenant-a-daprmq ./helm \
  -n multi-tenant \
  --set dapr.stateStoreName=statestore

# Tenant B
helm install tenant-b-daprmq ./helm \
  -n multi-tenant \
  --set dapr.stateStoreName=statestore

# Both tenants share the state store but are isolated by app-id
```

### Release Naming

- **Release names are unique identifiers**: Choose descriptive release names as they become part of the Dapr app-id and actor type
- **App-id format**: `{release-name}-daprmq-worker` and `{release-name}-daprmq-gateway`
- **Actor type format**: `{release-name}-daprmq-QueueActor`
- **Example**: Release name `my-app` creates:
  - App-ids: `my-app-daprmq-worker` and `my-app-daprmq-gateway`
  - Actor type: `my-app-daprmq-QueueActor`

## Upgrading

### Upgrade the Release

```bash
helm upgrade daprmq ./helm -n <namespace> -f my-values.yaml
```

### Scaling Worker

```bash
# Manual scaling
kubectl scale deployment daprmq-workers --replicas=5 -n <namespace>

# Or enable autoscaling
helm upgrade daprmq ./helm -n <namespace> --set autoscaling.enabled=true
```

## Troubleshooting

### Pods Not Starting

Check Dapr control plane:
```bash
kubectl get pods -n dapr-system
```

Verify sidecar injection:
```bash
kubectl describe pod -n <namespace> <pod-name>
```

### State Store Issues

Verify the state store Component exists:
```bash
kubectl get components -n <namespace>
```

Check connectivity to database:
```bash
kubectl logs -n <namespace> <pod-name> -c daprd
```

### Actor Distribution

Check actor placement:
```bash
# View actor activation logs
kubectl logs -n <namespace> -l app.kubernetes.io/component=worker -c daprmq | grep "Actor activated"

# Check Dapr placement service
kubectl logs -n dapr-system -l app=dapr-placement
```

### Health Check Failures

Test health endpoint directly (always on HTTP port 8080):
```bash
kubectl exec -n <namespace> <pod-name> -c daprmq -- curl http://localhost:8080/health
```

**Note:** The `/health` endpoint is only available on the HTTP port (8080), not the gRPC port (8081). Kubernetes liveness and readiness probes are configured to use port 8080.

## Uninstallation

```bash
# Uninstall the release
helm uninstall daprmq -n <namespace>

# Optionally delete the namespace
kubectl delete namespace daprmq
```

## Support

- GitHub: https://github.com/olitomlinson/dapr-mq
- Issues: https://github.com/olitomlinson/dapr-mq/issues
- Dapr Docs: https://docs.dapr.io/
