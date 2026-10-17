# KEDA tests

Two scripts that check DaprMQ's KEDA scaler against a live cluster, one for each way KEDA can scale a consumer.
Both use the current kubectl context, need KEDA and a DaprMQ release installed with `operator.enabled=true`, and
print a PASS/FAIL summary. They exit non-zero if any check fails.

| | `run.sh` | `load.sh` |
|---|---|---|
| KEDA resource | `ScaledJob`, one Kubernetes Job per item | `ScaledObject`, scaling a long-running Deployment |
| Use case | [Long-running work](../README.md#long-running-work) | Ordinary queue consumers under a backlog |
| Worker | [`worker.py`](worker.py) on `python:3.12-alpine`, deployed by the script | The [examples chart](../../helm)'s `<lang>-worker` |
| Extra requirements | None. It creates and deletes its own namespace | The examples chart installed with `autoscale.enabled=true` |

`k8s-deploy-and-test.sh --keda` runs both: `load.sh` in step 10 and `run.sh` in step 11.

## `run.sh`: ScaledJob correctness

Each Job dequeues one item with a 10s lock TTL. It then works for 30s while extending the lock, and acknowledges.
The script checks three things:

1. **One Job per item.** 5 items produce exactly 5 Jobs, each item is processed once, and every lock outlives its
   TTL through `ExtendLock`.
2. **In-flight items aren't counted twice.** With 5 items in flight, enqueuing 3 more starts exactly 3 more Jobs
   (8 in total). Locked items count in the metric, and KEDA's default strategy subtracts running Jobs.
3. **A killed Job's item is redelivered.** If a Job is force-deleted mid-work, its item comes back once the lock
   expires and a later Job completes it.

```bash
examples/keda/test/run.sh [--namespace daprmq] [--release daprmq] [--keda-namespace keda] [--keep]
```

`--namespace` and `--release` identify the DaprMQ release. The scaler address and gateway URL are derived from
them. The test ScaledJobs run in `daprmq-keda-test`, which is deleted afterwards unless you pass `--keep`.

## `load.sh`: ScaledObject autoscaling under load

This puts the examples chart's [autoscale example](../../shared/SCENARIOS.md#autoscale--keda-consumer-autoscaling-dotnet-only-so-far)
under load. Concurrent publishers start one at a time, `--ramp-interval` seconds apart, and each enqueues
`--messages` items to the worker's queue through the DaprMQ gateway. The operator reports the queue depth to KEDA,
which scales the worker out from 0, and every replica consumes as a competing consumer. The script checks five
things:

1. The worker is idle at 0 replicas before the load.
2. Every publish is accepted (HTTP 200).
3. The worker scales out from 0 to at least 2 replicas.
4. At least 2 distinct pods process items.
5. The queue drains and the worker scales back to 0.

It also reports the publish rate, the time to scale out, the peak replica count and the time to drain.

```bash
examples/keda/test/load.sh [options]
```

| Option | Default | Description |
|---|---|---|
| `--publishers N` | `10` | Concurrent publishers once the ramp-up is complete |
| `--ramp-interval N` | `10` | Seconds between starting each publisher |
| `--messages N` | `1000` | Messages each publisher enqueues |
| `--publish-mode MODE` | `single` | `single` sends one message per enqueue call. `batch` sends 100 per call |
| `--timeout N` | `1800` | Seconds to wait for publishing, the drain and the scale-in |
| `--context NAME` | current context | kubectl context |
| `--namespace NS` | found by label | Namespace of the examples release |
| `--language LANG` | `dotnet` | Example language whose worker is scaled. Only `dotnet` implements worker mode so far |
| `--gateway NS/SERVICE` | from the scaler address | DaprMQ gateway Service to publish through |

The script finds the worker's ScaledObject by label, and reads the worker, queue, maximum replica count and
cooldown from it. It derives the gateway from the ScaledObject's scaler address (`<release>-operator.<namespace>`
becomes `<namespace>/<release>-gateway`), so you don't need to pass any namespace or release name. Publishing goes
through a single `kubectl port-forward` to the gateway.

The defaults publish 10,000 messages. With the examples chart's worker defaults (4 replicas at most, a batch of
5 and 200ms of simulated work per item) the drain takes several minutes. `k8s-deploy-and-test.sh --keda` uses
`--publishers 1 --messages 300 --publish-mode batch` to keep the run short.

The examples images are built locally, not published, so on a cluster other than your own you need to build and
load them first. For example, `k8s-deploy-and-test.sh --kind-cluster NAME` loads them into a kind cluster.
