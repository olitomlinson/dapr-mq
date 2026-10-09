"""Schema-2 run records (sdks/testing/perf/result.schema.json), built exactly as
sdks/dotnet/perf/DaprMQ.Client.Perf/RunRecords.cs builds them, and saved in the shared layout:
<out>/sdk-python/runs/<runId>.json in full, <out>/sdk-python/history.jsonl without the timeline."""

from __future__ import annotations

import json
import os
import platform
import socket
import subprocess
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from .profiles import LoadParams, QueueDrainParams, SessionDrainParams

SCHEMA_VERSION = 2
SDK = "python"


@dataclass(frozen=True)
class RunEnvironment:
    label: str
    git_sha: str | None
    git_branch: str | None
    git_dirty: bool
    os: str
    cpu_count: int
    runtime: str
    server: str

    @staticmethod
    def capture(label: str, server: str) -> RunEnvironment:
        return RunEnvironment(
            label=label,
            git_sha=os.environ.get("GITHUB_SHA") or _git("rev-parse", "HEAD"),
            git_branch=os.environ.get("GITHUB_HEAD_REF") or os.environ.get("GITHUB_REF_NAME") or _git("rev-parse", "--abbrev-ref", "HEAD"),
            git_dirty=bool(_git("status", "--porcelain", "--untracked-files=no")),
            os=platform.platform(),
            cpu_count=os.cpu_count() or 1,
            runtime=f"{platform.python_implementation()} {platform.python_version()}",
            server=server,
        )


def _git(*args: str) -> str | None:
    try:
        result = subprocess.run(["git", *args], capture_output=True, text=True)
        return result.stdout.strip() if result.returncode == 0 else None
    except OSError:
        return None


def default_env_label() -> str:
    return f"local-{socket.gethostname().split('.')[0].lower()}"


@dataclass(frozen=True)
class RunContext:
    """Everything about a run that isn't its scenario or result."""

    timestamp: datetime
    environment: RunEnvironment
    scale: str
    api_replicas: int
    scheduler_replicas: int | None
    sdk_version: str | None


def sdk_version() -> str | None:
    try:
        from importlib.metadata import version

        return version("daprmq-client")
    except Exception:  # noqa: BLE001 - not installed as a distribution
        return None


def _envelope(context: RunContext, profile: str, scenario_id: str, name: str, key: str, params: dict, metrics: dict, failures: list[str]) -> dict:
    env = context.environment
    return {
        "schemaVersion": SCHEMA_VERSION,
        "runId": f"{context.timestamp:%Y%m%dT%H%M%SZ}_{SDK}_{env.label}_{profile}",
        "timestampUtc": context.timestamp.isoformat(),
        "sdk": {"name": SDK, "version": context.sdk_version, "runtime": env.runtime},
        "environment": {
            "label": env.label, "gitSha": env.git_sha, "gitBranch": env.git_branch, "gitDirty": env.git_dirty,
            "os": env.os, "cpuCount": env.cpu_count,
        },
        "topology": {
            "apiReplicas": context.api_replicas, "schedulerReplicas": context.scheduler_replicas,
            "loadBalancer": context.api_replicas > 1, "server": env.server,
        },
        "scale": context.scale,
        "scenario": {"id": scenario_id, "name": name, "profile": profile, "key": key, "params": params},
        "metrics": metrics,
        "checks": {"passed": not failures, "failures": failures},
    }  # fmt: skip


@dataclass
class LoadResult:
    steps: list[dict]
    timelines: list[dict]
    drained: bool
    sample_errors: list[str]


def load_record(context: RunContext, profile: str, load: LoadParams, result: LoadResult) -> dict:
    # A ramp's headline is its best step; ops/messages/errors cover every step.
    best = max(result.steps, key=lambda s: s["messagesPerSecond"])
    errors = sum(s["errors"] for s in result.steps)
    metrics = {
        "opsPerSecond": best["opsPerSecond"],
        "messagesPerSecond": best["messagesPerSecond"],
        "latencyMs": best["latencyMs"],
        "ops": sum(s["ops"] for s in result.steps),
        "messages": sum(s["messages"] for s in result.steps),
        "errors": errors,
    }

    failures = []
    if errors > 0:
        failures.append(f"{errors} errors" + (f" (first: {result.sample_errors[0]})" if result.sample_errors else ""))
    if result.drained:
        failures.append("a queue drained before the window ended: raise seedPerQueue")

    run = _envelope(context, profile, load.id, load.scenario, load.key, load.params(), metrics, failures)
    run["steps"] = result.steps
    series_names = ["opsPerSecond", "messagesPerSecond", "latencyP50Ms", "latencyP95Ms", "latencyP99Ms", "errors", "concurrency"]
    run["timeline"] = {
        "bucketMs": 1000,
        "series": {name: [v for t in result.timelines for v in t[name]] for name in series_names},
    }
    return run


def session_drain_record(context: RunContext, profile: str, scenario: SessionDrainParams, result: dict) -> dict:
    metrics = dict(result)
    busy = metrics.pop("busySlotsTimeline")
    bucket_ms = metrics.pop("timelineBucketMs")

    failures = []
    if metrics["missing"] > 0:
        failures.append(f"{metrics['missing']} messages missing")
    if metrics["fifoViolations"] > 0:
        failures.append(f"{metrics['fifoViolations']} FIFO violations")

    run = _envelope(context, profile, "P-04", "session-drain", scenario.key, scenario.params(), metrics, failures)
    run["timeline"] = {"bucketMs": bucket_ms, "series": {"busySlots": busy}}
    return run


def queue_drain_record(context: RunContext, profile: str, scenario: QueueDrainParams, result: dict) -> dict:
    metrics = dict(result)
    per_second = metrics.pop("messagesPerSecondTimeline")
    busy = metrics.pop("busyHandlersTimeline")

    failures = []
    if metrics["missing"] > 0:
        failures.append(f"{metrics['missing']} messages missing")
    # Above a window of 1, handlers legitimately start messages out of queue order.
    if scenario.strict_order and metrics["orderViolations"] > 0:
        failures.append(f"{metrics['orderViolations']} order violations")

    run = _envelope(context, profile, "P-05", "queue-drain", scenario.key, scenario.params(), metrics, failures)
    run["timeline"] = {"bucketMs": 1000, "series": {"messagesPerSecond": per_second, "busyHandlers": busy}}
    return run


def save(root: Path, run: dict) -> Path:
    sdk_dir = root / f"sdk-{run['sdk']['name']}"
    runs_dir = sdk_dir / "runs"
    runs_dir.mkdir(parents=True, exist_ok=True)

    run_path = runs_dir / f"{run['runId']}.json"
    run_path.write_text(json.dumps(run, indent=2))

    summary = {k: v for k, v in run.items() if k != "timeline"}
    with open(sdk_dir / "history.jsonl", "a") as history:
        history.write(json.dumps(summary, separators=(",", ":")) + "\n")
    return run_path
