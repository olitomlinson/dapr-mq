# DaprMQ Python Example - DaemonSet Pattern

This example demonstrates how to consume the DaprMQ HTTP API from a Python application running in Kubernetes. It showcases using a Kubernetes Service with `internalTrafficPolicy: Local` to connect to the node-local DaprMQ gateway.

## What This Demonstrates

- **Kubernetes Service Discovery**: Connect via Service with node-local routing
- **DaprMQ HTTP API**: Push and pop operations via REST
- **DaemonSet Pattern**: Connect to DaprMQ gateway running as DaemonSet
- **E2E Testing**: Verify complete DaprMQ installation

## Architecture

```
┌─────────────────────┐
│   External User     │
│                     │
└──────────┬──────────┘
           │ NodePort
           │
┌──────────▼──────────┐
│  Python Example App │
│  (This Example)     │
│                     │
│  PORT: 5000         │
└──────────┬──────────┘
           │ Kubernetes Service
           │ (internalTrafficPolicy: Local)
           │
┌──────────▼──────────┐
│  DaprMQ Gateway     │
│  (DaemonSet)        │
│                     │
│  PORT: 8080         │
└─────────────────────┘
```

## Prerequisites

1. **Kubernetes cluster** running with kubectl access
2. **Dapr installed** on the cluster ([dapr.io/getting-started](https://docs.dapr.io/getting-started/))
3. **DaprMQ installed** (see below)
4. **Docker** for building the example image

## Installation

### Step 1: Install DaprMQ

```bash
# From the repository root
helm install daprmq ./helm \
  --set dapr.stateStoreName=statestore
```

> **Note**: Replace `statestore` with your actual Dapr state store component name.

Verify the gateway is running:

```bash
kubectl get pods -l app.kubernetes.io/component=gateway -o wide
kubectl get svc daprmq-gateway
```

The gateway Service uses `internalTrafficPolicy: Local` to route traffic to the node-local DaemonSet pod.

### Step 2: Build the Example Docker Image

```bash
cd examples/daemonset/app
docker build -t daprmq-example:latest .
```

> **For Kubernetes**: If using kind, minikube, or remote cluster, you may need to:
> - **kind**: `kind load docker-image daprmq-example:latest`
> - **minikube**: `eval $(minikube docker-env)` then rebuild
> - **Remote cluster**: Push to your registry and update `deployment.yaml` image field

### Step 3: Update Service Name

Edit `examples/daemonset/k8s/deployment.yaml` and update the `GATEWAY_SERVICE` environment variable to match your Helm release name:

```yaml
- name: GATEWAY_SERVICE
  value: "<your-release-name>-gateway"  # e.g., "my-daprmq-gateway"
```

To find your service name:
```bash
kubectl get svc -l app.kubernetes.io/component=gateway
```

### Step 4: Deploy the Example

```bash
kubectl apply -f examples/daemonset/k8s/
```

Check deployment status:

```bash
kubectl get pods -l app=daprmq-example
kubectl logs -l app=daprmq-example
```

### Step 5: Access the Application

**Option A: Port Forward (Recommended for local clusters)**

For Docker Desktop, minikube, or kind:

```bash
kubectl port-forward svc/daprmq-example 5000:5000
```

Then access at `http://localhost:5000`

**Option B: NodePort (For cloud clusters)**

```bash
# Get the NodePort
export NODEPORT=$(kubectl get svc daprmq-example -o jsonpath='{.spec.ports[0].nodePort}')

# Get a node IP
export NODE_IP=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}')

echo "Example app available at: http://$NODE_IP:$NODEPORT"
```

## Usage

### Web Interface

Open `http://localhost:5000` (if using port-forward) or `http://$NODE_IP:$NODEPORT` (if using NodePort) in your browser to see the API documentation.

### Push an Item

```bash
curl -X POST http://localhost:5000/push \
  -H "Content-Type: application/json" \
  -d '{
    "item": {
      "task": "process-order",
      "orderId": "12345",
      "data": "example payload"
    },
    "priority": 1
  }'
```

**Response:**
```json
{
  "success": true,
  "message": "Pushed 1 items to queue demo-queue",
  "itemsPushed": 1
}
```

### Pop an Item

```bash
curl -X POST http://localhost:5000/pop
```

**Response:**
```json
{
  "items": [
    {
      "item": {
        "task": "process-order",
        "orderId": "12345",
        "data": "example payload"
      },
      "priority": 1
    }
  ]
}
```

**Empty Queue Response:**
```json
{
  "items": [],
  "message": "Queue empty"
}
```

### Health Check

```bash
curl http://localhost:5000/health
```

**Response:**
```json
{
  "status": "healthy",
  "gateway": "connected",
  "gateway_url": "http://daprmq-gateway:8080",
  "queue_id": "demo-queue"
}
```

## How It Works

### Service with internalTrafficPolicy: Local

The DaprMQ gateway runs as a DaemonSet (one pod per node) and is exposed via a Kubernetes Service with `internalTrafficPolicy: Local`:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: daprmq-gateway
spec:
  type: ClusterIP
  internalTrafficPolicy: Local
  ports:
    - port: 8080
      targetPort: 8080
  selector:
    app.kubernetes.io/component: gateway
```

With `internalTrafficPolicy: Local`, Kubernetes routes traffic to a pod on the same node as the client, guaranteeing node-local communication.

Benefits:
- Lower latency (node-local routing)
- Standard Kubernetes Service discovery
- No need for hostPort or downward API
- More portable and production-ready

### API Transformation

The example app provides a simplified API and transforms requests to the DaprMQ format:

**Your Request:**
```json
{
  "item": {"task": "hello"},
  "priority": 1
}
```

**DaprMQ Request:**
```json
{
  "items": [
    {
      "item": {"task": "hello"},
      "priority": 1
    }
  ]
}
```

## Troubleshooting

### Pod Not Starting

```bash
kubectl describe pod -l app=daprmq-example
kubectl logs -l app=daprmq-example
```

**Common issues:**
- Image not found: Rebuild and ensure image is available in cluster
- Gateway unreachable: Verify DaprMQ gateway service exists

### Gateway Unreachable

Verify gateway service is accessible from the pod:

```bash
kubectl exec -it deployment/daprmq-example -- curl http://daprmq-gateway:8080/health
```

Check service configuration:

```bash
kubectl get svc daprmq-gateway -o yaml | grep internalTrafficPolicy
```

### Health Check Failing

The `/health` endpoint tests gateway connectivity. If it returns 503:

1. Verify DaprMQ gateway is running:
   ```bash
   kubectl get pods -l app.kubernetes.io/component=gateway
   ```

2. Check gateway service exists:
   ```bash
   kubectl get svc daprmq-gateway
   ```

3. Test gateway from within the cluster:
   ```bash
   kubectl run -it --rm debug --image=curlimages/curl --restart=Never -- curl http://daprmq-gateway:8080/health
   ```

### Cannot Access via NodePort

- **Local Cluster**: Docker Desktop, minikube, and kind don't expose NodePort by default. Use port-forward instead:
  ```bash
  kubectl port-forward svc/daprmq-example 5000:5000
  ```
- **Firewall**: Ensure NodePort range (30000-32767) is open
- **Cloud Provider**: Some cloud providers block NodePort by default

## Production Considerations

⚠️ **This is a demo example, NOT production-ready.** For production:

### What to Change

1. **Use ClusterIP + Ingress** instead of NodePort
   ```yaml
   spec:
     type: ClusterIP  # Change from NodePort
   ```
   Add proper Ingress with TLS.

2. **Already using Service Discovery** ✅
   The example uses Kubernetes Service with `internalTrafficPolicy: Local`

3. **Add Authentication**
   - API keys or OAuth2
   - mTLS between services
   - Rate limiting

4. **Improve Error Handling**
   - Retry logic with exponential backoff
   - Circuit breaker pattern
   - Dead letter queue handling

5. **Add Observability**
   - Prometheus metrics endpoint
   - Structured JSON logging
   - Distributed tracing

6. **Security Hardening**
   - Run as non-root user
   - Read-only root filesystem
   - Network policies
   - Resource limits

7. **Use Production WSGI Server**
   ```dockerfile
   # Replace Flask dev server with gunicorn
   CMD ["gunicorn", "-w", "4", "-b", "0.0.0.0:5000", "app:app"]
   ```

8. **Environment Configuration**
   - Use ConfigMaps for configuration
   - Use Secrets for sensitive data
   - Make queue ID configurable

## API Reference

### GET /

Returns HTML documentation page.

### GET /health

Health check endpoint that verifies gateway connectivity.

**Response (200 OK):**
```json
{
  "status": "healthy",
  "gateway": "connected",
  "gateway_url": "http://daprmq-gateway:8080",
  "queue_id": "demo-queue"
}
```

**Response (503 Service Unavailable):**
```json
{
  "status": "degraded",
  "gateway": "unreachable",
  "gateway_url": "http://daprmq-gateway:8080",
  "error": "Connection refused"
}
```

### POST /push

Push an item to the queue.

**Request Body:**
```json
{
  "item": { ... },      // Required: any JSON object
  "priority": 1         // Optional: integer >= 0, default 1
}
```

**Response (200 OK):**
```json
{
  "success": true,
  "message": "Pushed 1 items to queue demo-queue",
  "itemsPushed": 1
}
```

**Error Responses:**
- `400 Bad Request`: Invalid request body
- `503 Service Unavailable`: Gateway unreachable
- `504 Gateway Timeout`: Gateway timeout

### POST /pop

Pop an item from the queue.

**Request Body:** None

**Response (200 OK, items available):**
```json
{
  "items": [
    {
      "item": { ... },
      "priority": 1
    }
  ]
}
```

**Response (200 OK, queue empty):**
```json
{
  "items": [],
  "message": "Queue empty"
}
```

**Error Responses:**
- `503 Service Unavailable`: Gateway unreachable
- `504 Gateway Timeout`: Gateway timeout

## Learn More

- [DaprMQ Documentation](../../README.md)
- [DaprMQ API Reference](../../docs/API_REFERENCE.md)
- [DaprMQ Architecture](../../docs/ARCHITECTURE.md)
- [Kubernetes Downward API](https://kubernetes.io/docs/concepts/workloads/pods/downward-api/)
- [Dapr Documentation](https://docs.dapr.io/)
