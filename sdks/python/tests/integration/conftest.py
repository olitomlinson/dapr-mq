"""Integration tests start their own throwaway DaprMQ stack with Testcontainers (no docker
compose, no pre-existing server) - see stack.py, which the perf harness shares.

Prerequisite: the API image must exist locally - build it with `./build-and-test.sh --skip-tests`
from the repo root (default tag `daprmq-api:test`, override with DAPRMQ_API_IMAGE).
"""

from __future__ import annotations

import uuid
from collections.abc import AsyncIterator, Iterator

import pytest

from daprmq_client import DaprMQClient

from .stack import DaprMQServer, require_api_image, start_stack


@pytest.fixture(scope="session")
def daprmq_server() -> Iterator[DaprMQServer]:
    problem = require_api_image()
    if problem == "Docker daemon not available":
        pytest.skip(problem)
    if problem is not None:
        pytest.fail(problem)

    with start_stack() as server:
        yield server


@pytest.fixture
async def client(daprmq_server: DaprMQServer) -> AsyncIterator[DaprMQClient]:
    async with DaprMQClient(http_base_url=daprmq_server.http_url, grpc_address=daprmq_server.grpc_address) as c:
        yield c


@pytest.fixture
def queue_id() -> str:
    return f"py-it-{uuid.uuid4().hex}"
