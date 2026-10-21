#!/usr/bin/env python3
"""Where a throughput run's time went, from the DaprMQ release itself.

    diagnostics.py snapshot <context> <namespace> <out.json>
    diagnostics.py diff <before.json> <after.json>      # prints the run's diagnostics as JSON

A snapshot holds, for every gateway pod, its daprd sidecar's /metrics (read through the API server's
pod proxy, so nothing is port-forwarded), and for every gateway and worker container its cgroup
cpu.stat. diff turns two snapshots into:

- actorCalls / actorCallLatencyMs: the gateways' actor method calls made between the snapshots, and
  their mean latency as daprd timed them (dapr_http_server_latency), split into the session
  coordinator and the queue actors (sessions included).
- cpuThrottledPercent: the share of CFS periods in which the worker and gateway containers were
  throttled by their CPU limit. None when a component has no limit.

Counters are cumulative per pod, so a pod that appeared, restarted or went away between the snapshots
is left out rather than counted from zero.
"""
import json
import re
import subprocess
import sys

_LATENCY = re.compile(r'^dapr_http_server_latency_(sum|count)\{(.*)\} (\S+)$')
_LABEL = re.compile(r'(\w+)="([^"]*)"')


def _kubectl(context, *args):
    return subprocess.run(["kubectl", "--context", context, *args], check=True, capture_output=True, text=True).stdout


def snapshot(context, namespace):
    pods = {}
    for component in ("gateway", "worker"):
        names = _kubectl(context, "-n", namespace, "get", "pods", "-l", f"app.kubernetes.io/component={component}",
                         "-o", "jsonpath={.items[*].metadata.name}").split()
        pods[component] = names

    gateways, cgroups = {}, {}
    for pod in pods["gateway"]:
        try:
            gateways[pod] = _kubectl(context, "get", "--raw", f"/api/v1/namespaces/{namespace}/pods/{pod}:9090/proxy/metrics")
        except subprocess.CalledProcessError:
            pass  # a pod that isn't ready yet; it's left out of the diff
    for component, names in pods.items():
        for pod in names:
            try:
                cgroups[f"{component}/{pod}"] = _kubectl(context, "-n", namespace, "exec", pod, "-c", "daprmq", "--",
                                                         "cat", "/sys/fs/cgroup/cpu.stat")
            except subprocess.CalledProcessError:
                pass
    return {"gateways": gateways, "cgroups": cgroups}


def _actor_category(path):
    if "/v1.0/actors/" not in path or not path.endswith("/method"):
        return None
    return "coordinator" if "/SessionCoordinatorActor/" in path else "queueActor" if "QueueActor/" in path else None


def _latency_totals(text):
    """{category: [sum ms, count]} of one gateway's actor method calls."""
    totals = {"coordinator": [0.0, 0.0], "queueActor": [0.0, 0.0]}
    for line in text.splitlines():
        m = _LATENCY.match(line)
        if not m:
            continue
        kind, labels, value = m.groups()
        category = _actor_category(dict(_LABEL.findall(labels)).get("path", ""))
        if category:
            totals[category][0 if kind == "sum" else 1] += float(value)
    return totals


def _cpu_stat(text):
    stat = dict(line.split() for line in text.splitlines() if len(line.split()) == 2)
    return int(stat.get("nr_periods", 0)), int(stat.get("nr_throttled", 0))


def diff(before, after):
    sums = {"coordinator": 0.0, "queueActor": 0.0}
    counts = {"coordinator": 0, "queueActor": 0}
    for pod, text in after["gateways"].items():
        if pod not in before["gateways"]:
            continue
        b, a = _latency_totals(before["gateways"][pod]), _latency_totals(text)
        for category in sums:
            ds, dc = a[category][0] - b[category][0], a[category][1] - b[category][1]
            if dc < 0 or ds < 0:
                continue  # the counter reset: the pod restarted
            sums[category] += ds
            counts[category] += int(dc)

    periods, throttled = {}, {}
    for key, text in after["cgroups"].items():
        if key not in before["cgroups"]:
            continue
        component = key.split("/", 1)[0]
        (bp, bt), (ap, at) = _cpu_stat(before["cgroups"][key]), _cpu_stat(text)
        if ap < bp:
            continue
        periods[component] = periods.get(component, 0) + ap - bp
        throttled[component] = throttled.get(component, 0) + at - bt

    return {
        "actorCalls": counts,
        "actorCallLatencyMs": {c: (round(sums[c] / counts[c], 1) if counts[c] else None) for c in sums},
        "cpuThrottledPercent": {c: (round(100.0 * throttled[c] / periods[c], 1) if periods[c] else None) for c in periods},
    }


def main(argv):
    if len(argv) == 5 and argv[1] == "snapshot":
        with open(argv[4], "w") as f:
            json.dump(snapshot(argv[2], argv[3]), f)
        return 0
    if len(argv) == 4 and argv[1] == "diff":
        with open(argv[2]) as b, open(argv[3]) as a:
            print(json.dumps(diff(json.load(b), json.load(a))))
        return 0
    print(__doc__, file=sys.stderr)
    return 64


if __name__ == "__main__":
    sys.exit(main(sys.argv))
