"""Control-plane configuration for the DaprMQ Python producer example.

Reads env vars once at process startup to build the *default* config; a `PUT
/config` call can override it in-memory until the next `POST /reset`. See
examples/shared/API_CONTRACT.md for the exact contract this mirrors.
"""
from __future__ import annotations

import os
from dataclasses import dataclass, replace

LANGUAGE = "python"


@dataclass
class Config:
    http_base_url: str
    grpc_address: str
    queue_prefix: str
    language: str
    role: str
    source: str  # "default" | "override"


def load_default_config(role: str) -> Config:
    return Config(
        http_base_url=os.environ.get("DAPRMQ_HTTP_BASE_URL") or "http://localhost:8002",
        grpc_address=os.environ.get("DAPRMQ_GRPC_ADDRESS") or "localhost:8003",
        queue_prefix=os.environ.get("DAPRMQ_QUEUE_PREFIX") or f"examples-{LANGUAGE}",
        language=LANGUAGE,
        role=role,
        source="default",
    )


def with_overrides(
    config: Config,
    *,
    http_base_url: str | None,
    grpc_address: str | None,
    queue_prefix: str | None,
) -> Config:
    return replace(
        config,
        http_base_url=http_base_url if http_base_url is not None else config.http_base_url,
        grpc_address=grpc_address if grpc_address is not None else config.grpc_address,
        queue_prefix=queue_prefix if queue_prefix is not None else config.queue_prefix,
        source="override",
    )


def to_json(config: Config) -> dict:
    return {
        "httpBaseUrl": config.http_base_url,
        "grpcAddress": config.grpc_address,
        "queuePrefix": config.queue_prefix,
        "language": config.language,
        "role": config.role,
        "source": config.source,
    }
