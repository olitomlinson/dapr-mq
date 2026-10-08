# Autoscaling consumers with KEDA

Scale your own consumer Deployments on a DaprMQ queue's depth, including down to zero when the queue is empty.
DaprMQ's operator implements KEDA's [external scaler](https://keda.sh/docs/latest/scalers/external/) contract; KEDA
asks it for the queue's depth and drives an HPA on your Deployment. How the metric is read:
[ARCHITECTURE.md](../../docs/ARCHITECTURE.md#autoscaling-consumers-with-keda).

## 1. Install KEDA

```bash
helm repo add kedacore https://kedacore.github.io/charts
helm repo update kedacore
helm upgrade --install keda kedacore/keda -n keda --create-namespace --wait
```

## 2. Enable the DaprMQ operator

Add `operator.enabled=true` to your DaprMQ release (it needs `worker.enabled`, the default):

```bash
helm upgrade --install daprmq ./helm -n daprmq --reuse-values --set operator.enabled=true
kubectl get deploy,svc -n daprmq -l app.kubernetes.io/component=operator   # wait for READY
```

Other operator values: [helm/README.md](../../helm/README.md#operator-keda-consumer-autoscaling).

## 3. Work out `scalerAddress`

```
<fullname>-operator.<namespace>:<operator.grpcPort>
```

`<fullname>` is the release name when it contains `daprmq`, otherwise `<release>-daprmq` (or `fullnameOverride`).
Release `daprmq` in namespace `daprmq` gives `daprmq-operator.daprmq:8081`, which is what
[consumer-scaledobject.yaml](consumer-scaledobject.yaml) uses. To confirm:

```bash
kubectl get svc -n <namespace> | grep operator
```

## 4. Make your consumer scale-safe

- **Messages mode: enable competing consumers.** By default a queue serves one lock at a time, so a second replica's
  locked dequeue gets `423 Locked` and does nothing. Set `AllowCompetingConsumers` (every SDK exposes it, see each
  SDK's `docs/CLIENT_SDK.md`) so each replica holds its own locks.
- **Sessions mode:** each replica accepts sessions as usual; scaling out just means more sessions processed at once.
- **Finish in-flight work on SIGTERM.** KEDA scales in by terminating pods. Acknowledge (or nack) whatever has
  already been dequeued before exiting, and keep `terminationGracePeriodSeconds` longer than one batch takes. Locks a
  pod abandons aren't lost; the items return to the queue when their locks expire, but they're redelivered later and
  their `DeliveryCount` goes up.
- **Size lock TTLs to the work.** Locked items count towards the metric, so a pod that dies holding long locks keeps
  replicas up until those locks expire and are swept.

## 5. Apply a ScaledObject

Copy [consumer-scaledobject.yaml](consumer-scaledobject.yaml), then set `scaleTargetRef.name`, `scalerAddress` and
`queueId`. Put it in the **same namespace as the consumer Deployment**.

```bash
kubectl apply -n <consumer-namespace> -f consumer-scaledobject.yaml
```

Trigger metadata (all strings):

| Field | Default | Meaning |
|---|---|---|
| `queueId` | required | Queue to measure |
| `mode` | `messages` | `messages`: ready + locked items. `sessions`: number of sessions holding any items |
| `targetValue` | `10` | Metric per replica the HPA aims for (replicas ≈ metric / targetValue) |
| `activationValue` | `0` | Scales up from zero once the metric is above this |
| `includeDeadLetter` | `false` | Messages mode only: also count `{queueId}-deadletter` |

Use trigger `type: external-push` to scale up from zero as soon as items arrive, or `type: external` to pick changes
up only every `pollingInterval`.

## 6. Verify

```bash
kubectl get scaledobject -n <consumer-namespace>          # READY and ACTIVE columns
kubectl get hpa -n <consumer-namespace>                   # keda-hpa-<name>, current/target metric
kubectl get deploy <consumer> -n <consumer-namespace> -w  # replicas move 0 → N → 0
```

Enqueue some items: the Deployment scales up, drains the queue, and scales back to `minReplicaCount` about
`cooldownPeriod` seconds after the queue is empty.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| ScaledObject `READY=False` | Wrong `scalerAddress`, or the operator isn't running. Check `kubectl describe scaledobject <name>` and the KEDA operator logs (`kubectl logs -n keda deploy/keda-operator`) |
| Replicas held at `fallback.replicas` | The scaler can't read depth. A failed read is an error, never 0. Check the operator pods and their Dapr sidecars (`kubectl logs <operator-pod> -c daprd`) and that the DaprMQ workers are healthy |
| Extra replicas sit idle; only one consumes | Competing consumers not enabled (step 4) |
| Never scales back to zero | Items are still locked (a crashed pod's locks count until they expire) or `minReplicaCount` > 0. With `includeDeadLetter: "true"`, dead letters count too |
| Scales up only on the polling interval | Trigger type is `external`, not `external-push` |

## End-to-end demo

The examples chart can deploy a ready-made KEDA-scaled worker (dotnet only) to try this out; see
[examples/README.md](../README.md#keda-autoscaling-demo), or run `./k8s-deploy-and-test.sh --keda` from the repo root.
