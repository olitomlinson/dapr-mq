"""DaprMQ Python consumer example — control-plane HTTP server.

Implements the control-plane contract from examples/shared/API_CONTRACT.md
(health/config/reset/scenarios) on top of aiohttp.web, driving the real
DaprMQ gateway via `daprmq_client.DaprMQClient`. See
examples/shared/SCENARIOS.md for what each scenario actually does.
"""
from __future__ import annotations

import os
import re
from datetime import datetime, timezone
from urllib.parse import urlparse

from aiohttp import web
from daprmq_client import DaprMQClient, DaprMQError

import scenarios
from config import Config, load_default_config, to_json, with_overrides

ROLE = "consumer"

SCENARIO_SUFFIXES = {
    "basic": "basic",
    "ack-deadletter": "ackdlq",
    "priority": "priority",
    "sessions": "sessions",
    "idempotency": "idempotency",
}

SCENARIO_DESCRIPTIONS = {
    "basic": "Dequeues items with ack and acknowledges each one.",
    "ack-deadletter": "Acks/dead-letters/expires items, drains the redelivery and the DLQ.",
    "priority": "Dequeues items, showing fast-lane items surface before normal-priority items.",
    "sessions": "Accepts each session, dequeues/acks its items in order, then releases it.",
    "idempotency": "Dequeues and acknowledges the survivors of the producer's dedup demo.",
}

_GRPC_ADDRESS_RE = re.compile(r"^[^/\s:]+:\d+$")
_QUEUE_PREFIX_RE = re.compile(r"^[A-Za-z0-9_-]+$")


class AppState:
    def __init__(self, config: Config, client: DaprMQClient) -> None:
        self.config = config
        self.client = client
        self.scenario_running = False


def _iso_now() -> str:
    now = datetime.now(timezone.utc)
    return now.strftime("%Y-%m-%dT%H:%M:%S.") + f"{now.microsecond // 1000:03d}Z"


def log(level: str, message: str) -> None:
    print(f"[python] [{ROLE}] {_iso_now()} {level} {message}", flush=True)


def error_response(status: int, code: str, message: str) -> web.Response:
    return web.json_response({"error": {"code": code, "message": message}}, status=status)


def _valid_http_url(value: object) -> bool:
    if not isinstance(value, str) or not value.strip():
        return False
    parsed = urlparse(value)
    return parsed.scheme in ("http", "https") and bool(parsed.netloc)


def _valid_grpc_address(value: object) -> bool:
    if not isinstance(value, str) or not value.strip():
        return False
    return bool(_GRPC_ADDRESS_RE.match(value))


def _valid_queue_prefix(value: object) -> bool:
    return isinstance(value, str) and bool(_QUEUE_PREFIX_RE.match(value))


def _new_client(config: Config) -> DaprMQClient:
    return DaprMQClient(http_base_url=config.http_base_url, grpc_address=config.grpc_address)


routes = web.RouteTableDef()


@routes.get("/health")
async def health(request: web.Request) -> web.Response:
    return web.json_response({"status": "ok", "language": "python", "role": ROLE})


@routes.get("/config")
async def get_config(request: web.Request) -> web.Response:
    state: AppState = request.app["state"]
    return web.json_response(to_json(state.config))


@routes.put("/config")
async def put_config(request: web.Request) -> web.Response:
    state: AppState = request.app["state"]

    try:
        body = await request.json()
    except Exception:
        body = {}
    if not isinstance(body, dict):
        body = {}

    new_http_base_url = body.get("httpBaseUrl")
    new_grpc_address = body.get("grpcAddress")
    new_queue_prefix = body.get("queuePrefix")

    if new_http_base_url is not None and not _valid_http_url(new_http_base_url):
        return error_response(400, "INVALID_CONFIG", "httpBaseUrl must be a non-empty http(s) URL")
    if new_grpc_address is not None and not _valid_grpc_address(new_grpc_address):
        return error_response(400, "INVALID_CONFIG", "grpcAddress must be a non-empty host:port value")
    if new_queue_prefix is not None and not _valid_queue_prefix(new_queue_prefix):
        return error_response(400, "INVALID_CONFIG", "queuePrefix must be a non-empty identifier")

    updated = with_overrides(
        state.config,
        http_base_url=new_http_base_url,
        grpc_address=new_grpc_address,
        queue_prefix=new_queue_prefix,
    )

    old_client = state.client
    new_client = _new_client(updated)
    await old_client.aclose()

    state.client = new_client
    state.config = updated
    log(
        "INFO",
        f"Config updated: httpBaseUrl={updated.http_base_url} grpcAddress={updated.grpc_address} "
        f"queuePrefix={updated.queue_prefix}",
    )
    return web.json_response(to_json(updated))


@routes.post("/reset")
async def reset(request: web.Request) -> web.Response:
    state: AppState = request.app["state"]

    defaults = load_default_config(ROLE)
    old_client = state.client
    new_client = _new_client(defaults)
    await old_client.aclose()

    state.client = new_client
    state.config = defaults
    state.scenario_running = False
    log("INFO", "State reset to defaults")
    return web.json_response({"success": True, "message": "State reset to defaults"})


@routes.get("/scenarios")
async def list_scenarios(request: web.Request) -> web.Response:
    return web.json_response(
        [{"name": name, "role": ROLE, "description": SCENARIO_DESCRIPTIONS[name]} for name in SCENARIO_SUFFIXES]
    )


@routes.post("/scenarios/{name}/run")
async def run_scenario(request: web.Request) -> web.Response:
    name = request.match_info["name"]
    if name not in scenarios.RUNNERS:
        return error_response(404, "UNKNOWN_SCENARIO", f"Unknown scenario '{name}'")

    state: AppState = request.app["state"]
    if state.scenario_running:
        return error_response(409, "SCENARIO_IN_PROGRESS", "A scenario run is already in progress on this pod")

    state.scenario_running = True
    try:
        queue_id = f"{state.config.queue_prefix}-{SCENARIO_SUFFIXES[name]}"
        started_at = _iso_now()
        log("INFO", f"Starting scenario '{name}' (queueId={queue_id})")

        try:
            steps = await scenarios.RUNNERS[name](state.client, queue_id, log)
        except DaprMQError as exc:
            log("ERROR", f"Scenario '{name}' failed: {exc}")
            return error_response(502, "UPSTREAM_ERROR", str(exc))
        except Exception as exc:  # unexpected — still surfaced as an upstream/gateway failure
            log("ERROR", f"Scenario '{name}' failed unexpectedly: {exc!r}")
            return error_response(502, "UPSTREAM_ERROR", str(exc))

        finished_at = _iso_now()
        log("INFO", f"Finished scenario '{name}'")
        return web.json_response(
            {
                "scenario": name,
                "role": ROLE,
                "queueId": queue_id,
                "startedAt": started_at,
                "finishedAt": finished_at,
                "steps": steps,
            }
        )
    finally:
        state.scenario_running = False


def create_app() -> web.Application:
    config = load_default_config(ROLE)
    client = _new_client(config)

    app = web.Application()
    app["state"] = AppState(config, client)
    app.add_routes(routes)

    async def on_cleanup(app: web.Application) -> None:
        await app["state"].client.aclose()

    app.on_cleanup.append(on_cleanup)
    return app


if __name__ == "__main__":
    control_port = int(os.environ.get("CONTROL_PORT", "8080"))
    log("INFO", f"Starting DaprMQ python {ROLE} control-plane on port {control_port}")
    web.run_app(create_app(), host="0.0.0.0", port=control_port, print=None)
