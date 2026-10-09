"""Command line of the Python perf harness: the .NET harness's flags where they apply."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path

from .profiles import PROFILES, SUITES, scale_of
from .records import default_env_label

USAGE = "python -m perf [--suite pr|extreme | --profile NAME] [options]"


@dataclass(frozen=True)
class PerfOptions:
    profiles: list[str]
    suite: str | None
    api_replicas: int
    env_label: str
    out_dir: Path
    http: str | None
    grpc: str | None
    gate: bool
    baseline_branch: str
    report_only: bool

    def scale(self, profile: str) -> str:
        return self.suite or scale_of(profile)


def default_out_dir() -> Path:
    """<repo>/perf-results, so every SDK's harness lands in one results root."""
    for directory in Path(__file__).resolve().parents:
        if (directory / "sdks" / "testing" / "PERFORMANCE_TESTS.md").exists():
            return directory / "perf-results"
    return Path("perf-results").resolve()


def parse(args: list[str]) -> PerfOptions:
    parser = argparse.ArgumentParser(prog="python -m perf", usage=USAGE, description="DaprMQ Python SDK performance harness")
    parser.add_argument("--suite", choices=sorted(SUITES), help="run every profile of that scale against one stack")
    parser.add_argument("--profile", help="one profile from sdks/testing/PERFORMANCE_TESTS.md (default: full)")
    parser.add_argument("--api-replicas", type=int, help="API server replicas behind nginx (default 1; extreme 3)")
    parser.add_argument("--env-label", default=default_env_label(), help="series name on the charts (default local-<host>)")
    parser.add_argument("--out", type=Path, default=default_out_dir(), help="results root; runs go to OUT/sdk-python")
    parser.add_argument("--http", help="use an existing server's REST endpoint instead of Testcontainers")
    parser.add_argument("--grpc", help="... and its gRPC endpoint (host:port or http://host:port)")
    parser.add_argument("--gate", action="store_true", help="exit 3 if a metric regresses (default: report only)")
    parser.add_argument("--baseline-branch", default="main", help="compare against recent runs from this branch")
    parser.add_argument("--report", action="store_true", help="only regenerate OUT/report.html")
    ns = parser.parse_args(args)

    if ns.suite and ns.profile:
        parser.error("--profile can't be combined with --suite; the suite fixes the profiles.")
    if ns.profile and ns.profile not in PROFILES:
        parser.error(f"Unknown profile '{ns.profile}'. Known: {', '.join(PROFILES)}.")
    if (ns.http is None) != (ns.grpc is None):
        parser.error("--http and --grpc must be given together.")
    if ns.http is not None and ns.api_replicas is not None:
        parser.error("--api-replicas configures the Testcontainers stack, so it can't be combined with --http/--grpc.")
    if ns.api_replicas is not None and ns.api_replicas < 1:
        parser.error("--api-replicas must be >= 1.")

    grpc = ns.grpc.removeprefix("http://").removeprefix("https://").rstrip("/") if ns.grpc else None
    return PerfOptions(
        profiles=SUITES[ns.suite] if ns.suite else [ns.profile or "full"],
        suite=ns.suite,
        api_replicas=ns.api_replicas or (3 if ns.suite == "extreme" else 1),
        env_label=ns.env_label,
        out_dir=ns.out.resolve(),
        http=ns.http,
        grpc=grpc,
        gate=ns.gate,
        baseline_branch=ns.baseline_branch,
        report_only=ns.report,
    )
