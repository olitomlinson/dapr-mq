# DaprMQ Python Example - DaemonSet Pattern

This example demonstrates how to consume the DaprMQ HTTP API from a Python application running in Kubernetes. It showcases using the Kubernetes downward API to connect to DaprMQ gateway via `hostPort` on the same node.

## What This Demonstrates

- **Kubernetes Downward API**: Inject node IP to locate node-local services
- **DaprMQ HTTP API**: Push and pop operations via REST
- **DaemonSet Pattern**: Connect to DaprMQ gateway running as DaemonSet with hostPort
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
           │ Uses NODE_IP env var
           │ via downward API
           │
┌──────────▼──────────┐
│  DaprMQ Gateway     │
│  (DaemonSet)        │
│                     │
│  hostPort: 8091     │
└─────────────────────┘
```

## Prerequisites

1. **Kubernetes cluster** running with kubectl access
2. **Dapr installed** on the cluster ([dapr.io/getting-started](https://docs.dapr.io/getting-started/))
3. **DaprMQ installed** with hostPorts enabled (see below)
4. **Docker** for building the example image

## Installation

### Step 1: Install DaprMQ with hostPorts Enabled

The example requires DaprMQ gateway to expose hostPort 8091 for node-local access:

```bash
# From the repository root
helm install daprmq ./helm \
  --set dapr.stateStoreName=statestore \
  --set gateway.httpHostPort=8091
```

> **Note**: Replace `statestore` with your actual Dapr state store component name.

Verify the gateway is running with hostPorts:

```bash
kubectl get pods -l app.kubernetes.io/component=gateway -o wide
kubectl get pods -l app.kubernetes.io/component=gateway -o jsonpath='{.items[0].spec.containers[0].ports}'
```

You should see hostPort 8091 in the output.

### Step 2: Build the Example Docker Image

```bash
cd examples/daemonset/app
docker build -t daprmq-example:latest .
```

> **For Kubernetes**: If using kind, minikube, or remote cluster, you may need to:
> - **kind**: `kind load docker-image daprmq-example:latest`
> - **minikube**: `eval $(minikube docker-env)` then rebuild
> - **Remote cluster**: Push to your registry and update `deployment.yaml` image field

### Step 3: Deploy the Example

```bash
kubectl apply -f examples/daemonset/k8s/
```

Check deployment status:

```bash
kubectl get pods -l app=daprmq-example
kubectl logs -l app=daprmq-example
```

### Step 4: Get Access URL

```bash
# Get the NodePort
export NODEPORT=$(kubectl get svc daprmq-example -o jsonpath='{.spec.ports[0].nodePort}')

# Get a node IP
export NODE_IP=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}')

echo "Example app available at: http://$NODE_IP:$NODEPORT"
```

## Usage

### Web Interface

Open `http://$NODE_IP:$NODEPORT` in your browser to see the API documentation.

### Push an Item

```bash
curl -X POST http://$NODE_IP:$NODEPORT/push \
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
curl -X POST http://$NODE_IP:$NODEPORT/pop
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
curl http://$NODE_IP:$NODEPORT/health
```

**Response:**
```json
{
  "status": "healthy",
  "gateway": "connected",
  "gateway_url": "http://10.244.0.1:8091",
  "queue_id": "demo-queue"
}
```

## How It Works

### Kubernetes Downward API

The deployment uses the downward API to inject the node's IP address as an environment variable:

```yaml
env:
- name: NODE_IP
  valueFrom:
    fieldRef:
      fieldPath: status.hostIP
```

This allows the Python app to construct the gateway URL: `http://{NODE_IP}:8091`

### Why hostPort?

The DaprMQ gateway runs as a DaemonSet (one pod per node). By enabling `hostPort`, the gateway binds to the node's network interface, allowing node-local applications to connect directly without going through the Kubernetes service network.

Benefits:
- Lower latency (no service proxy)
- Simpler networking for node-local communication
- Demonstrates Kubernetes networking patterns

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
- Gateway unreachable: Verify DaprMQ gateway hostPort is configured

### Gateway Unreachable

Check if NODE_IP is injected correctly:

```bash
kubectl exec -it deployment/daprmq-example -- env | grep NODE_IP
```

Verify gateway is accessible from the pod:

```bash
kubectl exec -it deployment/daprmq-example -- curl http://$NODE_IP:8091/health
```

### Health Check Failing

The `/health` endpoint tests gateway connectivity. If it returns 503:

1. Verify DaprMQ gateway is running:
   ```bash
   kubectl get pods -l app.kubernetes.io/component=gateway
   ```

2. Check gateway has hostPort 8091:
   ```bash
   kubectl get pods -l app.kubernetes.io/component=gateway -o yaml | grep hostPort
   ```

3. Test gateway directly from your machine:
   ```bash
   curl http://<node-ip>:8091/health
   ```

### Cannot Access via NodePort

- **Firewall**: Ensure NodePort range (30000-32767) is open
- **Cloud Provider**: Some cloud providers block NodePort by default
- **Alternative**: Use `kubectl port-forward svc/daprmq-example 5000:5000` for local testing

## Production Considerations

⚠️ **This is a demo example, NOT production-ready.** For production:

### What to Change

1. **Use ClusterIP + Ingress** instead of NodePort
   ```yaml
   spec:
     type: ClusterIP  # Change from NodePort
   ```
   Add proper Ingress with TLS.

2. **Use Service Discovery** instead of hostPort
   ```python
   # Connect via Kubernetes service
   GATEWAY_URL = "http://daprmq-gateway:8080"
   ```
   This is more portable and standard.

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
  "gateway_url": "http://10.244.0.1:8091",
  "queue_id": "demo-queue"
}
```

**Response (503 Service Unavailable):**
```json
{
  "status": "degraded",
  "gateway": "unreachable",
  "gateway_url": "http://10.244.0.1:8091",
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
