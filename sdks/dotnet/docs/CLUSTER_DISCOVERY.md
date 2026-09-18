# Finding the gateway's HttpBaseAddress / GrpcAddress in-cluster

`DaprMQClient` needs a `HttpBaseAddress` and `GrpcAddress` ([CLIENT_SDK.md](CLIENT_SDK.md#constructing-a-client)). When the target is a `daprmq` Helm release, both point at the same Kubernetes `Service` - just different ports.

## Where the Service comes from

The gateway is a `Deployment` (`gateway.replicaCount` pods, default `3`) fronted by a `ClusterIP` `Service` named `{{ include "daprmq.fullname" . }}-gateway` ([service-gateway.yaml](../../../helm/templates/service-gateway.yaml)), exposing named ports `http` and `grpc` - default `8080`/`8081` ([values.yaml:38-39](../../../helm/values.yaml#L38-L39)), overridable via `gateway.httpPort`/`gateway.grpcPort`.

`daprmq.fullname` ([_helpers.tpl](../../../helm/templates/_helpers.tpl)) is **not** simply `<release-name>-daprmq` - it's:

- `fullnameOverride`, if set on the install, taken verbatim; otherwise
- the release name as-is, if the release name **already contains** `daprmq` (e.g. releases named `daprmq` or `my-daprmq`); otherwise
- `<release-name>-daprmq` (only when the release name doesn't contain `daprmq` at all, e.g. a release named `app1`).

So the gateway Service name depends on the release name you chose:

| Release name | `daprmq.fullname` | Gateway Service |
|---|---|---|
| `daprmq` | `daprmq` | `daprmq-gateway` |
| `my-daprmq` | `my-daprmq` | `my-daprmq-gateway` |
| `app1` | `app1-daprmq` | `app1-daprmq-gateway` |

Confirm the actual name for your install with `kubectl get svc -n <namespace> -l app.kubernetes.io/component=gateway` rather than assuming a pattern - the table above covers the common cases, but `nameOverride`/`fullnameOverride` can change it further.

The Service uses the k8s-default `Cluster` traffic policy, so requests are load-balanced across every ready gateway pod, not just one on the caller's node. The client must still run as a pod **inside the cluster** - there's no Ingress for the gateway (only the dashboard has one).

## Building the URLs

```
HttpBaseAddress = http://<gateway-service-name>.<namespace>.svc.cluster.local:<httpPort>
GrpcAddress     = http://<gateway-service-name>.<namespace>.svc.cluster.local:<grpcPort>
```

Within the same namespace as the client pod, the short form works and is preferred (same resolution, just shorter): `http://<gateway-service-name>:<httpPort>`.

The gateway Service name and `<namespace>` aren't known to the SDK at compile time - they come from how the *consuming* workload is deployed, not from DaprMQ itself. Get them one of:

1. **Env vars set on your own Deployment/Chart** (recommended) - inject at the pod spec level so they travel with the app instead of being hardcoded. Compute the Service name yourself using the table above (a release named `my-daprmq` gives `my-daprmq-gateway`, not `my-daprmq-daprmq-gateway`):
   ```yaml
   env:
   - name: DAPRMQ_GATEWAY_HTTP
     value: "http://my-daprmq-gateway:8080"
   - name: DAPRMQ_GATEWAY_GRPC
     value: "http://my-daprmq-gateway:8081"
   ```
   or pass the already-computed gateway Service name/namespace and format the URLs in code (safer than reconstructing the release-name-to-fullname logic in every consumer):
   ```yaml
   env:
   - name: DAPRMQ_GATEWAY_SERVICE
     value: "my-daprmq-gateway"
   - name: POD_NAMESPACE      # Downward API - always matches where you're actually running
     valueFrom:
       fieldRef:
         fieldPath: metadata.namespace
   ```
   ```csharp
   var gatewaySvc = Environment.GetEnvironmentVariable("DAPRMQ_GATEWAY_SERVICE");
   var ns = Environment.GetEnvironmentVariable("POD_NAMESPACE");
   var options = new DaprMQClientOptions
   {
       HttpBaseAddress = new Uri($"http://{gatewaySvc}.{ns}.svc.cluster.local:8080/"),
       GrpcAddress = $"http://{gatewaySvc}.{ns}.svc.cluster.local:8081"
   };
   ```

2. **Ask the cluster directly** (ad hoc / debugging, not for app startup):
   ```bash
   kubectl get svc -n <namespace> -l app.kubernetes.io/component=gateway
   helm get values <release-name> -n <namespace>   # confirm httpPort/grpcPort if overridden
   ```

3. **From another chart that depends on this one** - read `gateway.httpPort`/`gateway.grpcPort` and the release name as Helm values passed through to your own chart's templates, rather than duplicating the port numbers.

Don't hardcode the `8080`/`8081` defaults in client code - they're a Helm value (`gateway.httpPort`/`gateway.grpcPort`) and can be overridden per-install.
