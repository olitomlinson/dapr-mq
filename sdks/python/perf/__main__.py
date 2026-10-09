"""Python SDK performance harness (sdks/testing/PERFORMANCE_TESTS.md). From sdks/python:

    python -m perf --suite pr                  # every pr profile against one throwaway stack
    python -m perf --profile enqueue-ramp --api-replicas 3
    python -m perf --http http://localhost:8002 --grpc localhost:8102 --profile steady-drain

Runs go to <out>/sdk-python; the cross-SDK report and the regression table come from the .NET
PerfReport tool, so `dotnet` must be on PATH for those (the runs are saved regardless).
"""

from __future__ import annotations

import asyncio
import contextlib
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

from daprmq_client import DaprMQClient

from .options import PerfOptions, parse
from .profiles import LOAD_PROFILES, QUEUE_DRAIN_PROFILES, SESSION_DRAIN_PROFILES
from .records import RunContext, RunEnvironment, load_record, queue_drain_record, save, sdk_version, session_drain_record
from .scenarios import LoadScenario, QueueDrainScenario, SessionDrainScenario

REPO_ROOT = Path(__file__).resolve().parents[3]
PERF_REPORT = REPO_ROOT / "sdks" / "testing" / "perf" / "DaprMQ.PerfReport"


def perf_report(*args: str) -> int:
    """Runs the shared .NET report tool; its own output goes straight to the console."""
    try:
        return subprocess.run(["dotnet", "run", "-c", "Release", "--project", str(PERF_REPORT), "--", *args]).returncode
    except FileNotFoundError:
        print("dotnet not found: skipping the report and regression check.", file=sys.stderr)
        return 0


async def run_profiles(options: PerfOptions, client: DaprMQClient, environment: RunEnvironment, scheduler_replicas: int | None) -> tuple[int, list[str]]:
    exit_code = 0
    run_ids: list[str] = []
    for profile in options.profiles:
        print(f"\n=== Profile {profile} ===", flush=True)
        context = RunContext(
            datetime.now(timezone.utc), environment, options.scale(profile), options.api_replicas, scheduler_replicas, sdk_version()
        )
        if profile in LOAD_PROFILES:
            load = LOAD_PROFILES[profile]
            record = load_record(context, profile, load, await LoadScenario(client, load).run())
        elif profile in QUEUE_DRAIN_PROFILES:
            scenario = QUEUE_DRAIN_PROFILES[profile]
            result = await QueueDrainScenario(client, scenario).run()
            record = queue_drain_record(context, profile, scenario, result)
            print_queue_drain(result)
        else:
            scenario = SESSION_DRAIN_PROFILES[profile]
            result = await SessionDrainScenario(client, scenario).run()
            record = session_drain_record(context, profile, scenario, result)
            print_session_drain(result, scenario.max_concurrent_sessions)

        print(f"Run:    {save(options.out_dir, record)}", flush=True)
        run_ids.append(record["runId"])
        # Lost/reordered messages or failed operations make the timings meaningless - fail the run (and CI).
        if not record["checks"]["passed"]:
            print(f"FAILED: {record['checks']['failures']}", flush=True)
            exit_code = 1
    return exit_code, run_ids


async def run(options: PerfOptions) -> int:
    stack = contextlib.nullcontext()
    scheduler_replicas: int | None = None
    if options.http is not None:
        http_url, grpc_address, server = options.http, options.grpc, f"external {options.http}"
    else:
        # The integration fixture's stack, scaled out (tests/integration/stack.py).
        sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
        from tests.integration.stack import API_IMAGE, DaprTopology, require_api_image, start_stack

        problem = require_api_image()
        if problem is not None:
            print(problem, file=sys.stderr)
            return 2
        topology = DaprTopology.perf(options.api_replicas)
        scheduler_replicas = topology.scheduler_replicas
        print(f"Starting Testcontainers stack ({API_IMAGE}, {topology.api_replicas} API replica(s), {scheduler_replicas} schedulers)...", flush=True)
        stack = start_stack(topology)
        server = f"testcontainers {API_IMAGE}"

    with stack as started:
        if started is not None:
            http_url, grpc_address = started.http_url, started.grpc_address
        async with DaprMQClient(http_base_url=http_url, grpc_address=grpc_address) as client:
            await asyncio.wait_for(client.wait_for_ready(), 120)
            environment = RunEnvironment.capture(options.env_label, server)
            exit_code, run_ids = await run_profiles(options, client, environment, scheduler_replicas)

    perf_report("report", "--results", str(options.out_dir))
    check = ["check", "--results", str(options.out_dir), "--runs", ",".join(run_ids), "--baseline-branch", options.baseline_branch]
    regressed = perf_report(*check, *(["--gate"] if options.gate else [])) == 3
    return 3 if exit_code == 0 and regressed else exit_code


def print_session_drain(r: dict, slots: int) -> None:
    p = r["peak"]
    print("\n=== Session drain ===")
    print(f"Seed:                 {r['seedSeconds']:10.1f} s")
    print(f"Wall clock:           {r['wallClockSeconds']:10.1f} s   (ideal {r['idealSeconds']:.0f} s, efficiency {r['efficiency']:.1%})")
    print(f"First message after:  {r['timeToFirstMessageSeconds']:10.2f} s")
    print(f"Peak window:          {p['startSeconds']:.1f}-{p['endSeconds']:.1f} s, {slots} slots, utilisation {p['utilization']:.1%}")
    print(f"Claim latency ms:     p50 {r['claimLatencyMs']['p50']:.0f}  p95 {r['claimLatencyMs']['p95']:.0f}  max {r['claimLatencyMs']['max']:.0f}")
    print(f"Delivery ms:          p50 {r['deliveryLatencyMs']['p50']:.0f}  p95 {r['deliveryLatencyMs']['p95']:.0f}  max {r['deliveryLatencyMs']['max']:.0f}")
    print(f"Streams {r['streams']} (failed claims {r['failedClaims']}, re-claimed sessions {r['sessionsClaimedMoreThanOnce']})")
    print(f"Messages {r['messagesHandled']} (duplicates {r['duplicates']}, missing {r['missing']}, FIFO violations {r['fifoViolations']})", flush=True)


def print_queue_drain(r: dict) -> None:
    ideal = f"   (ideal {r['idealSeconds']:.1f} s, efficiency {r['efficiency']:.1%})" if r["idealSeconds"] is not None else ""
    print("\n=== Queue drain ===")
    print(f"Publish:              {r['seedSeconds']:10.1f} s")
    print(f"Wall clock:           {r['wallClockSeconds']:10.1f} s{ideal}")
    print(f"Throughput:           {r['messagesPerSecond']:10.0f} msg/s")
    print(f"First message after:  {r['timeToFirstMessageSeconds']:10.2f} s")
    print(f"Peak handlers:        {r['peakConcurrentHandlers']:10}")
    print(f"Delivery ms:          p50 {r['deliveryLatencyMs']['p50']:.0f}  p95 {r['deliveryLatencyMs']['p95']:.0f}  max {r['deliveryLatencyMs']['max']:.0f}")
    print(f"Messages {r['messagesHandled']} (duplicates {r['duplicates']}, missing {r['missing']}, order violations {r['orderViolations']})", flush=True)


def main() -> int:
    options = parse(sys.argv[1:])
    options.out_dir.joinpath("sdk-python").mkdir(parents=True, exist_ok=True)
    if options.report_only:
        return perf_report("report", "--results", str(options.out_dir))
    return asyncio.run(run(options))


if __name__ == "__main__":
    sys.exit(main())
