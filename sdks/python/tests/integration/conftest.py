"""Integration tests start their own throwaway DaprMQ stack with Testcontainers (no docker
compose, no pre-existing server), mirroring the .NET fixture in
server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs: Postgres, Dapr
placement + scheduler, the DaprMQ API server and a daprd sidecar on one private network with
dynamic host ports.

Prerequisite: the API image must exist locally - build it with `./build-and-test.sh --skip-tests`
from the repo root (default tag `daprmq-api:test`, override with DAPRMQ_API_IMAGE).
"""

from __future__ import annotations

import os
import stat
import subprocess
import tempfile
import time
import uuid
from collections.abc import AsyncIterator, Iterator
from dataclasses import dataclass
from pathlib import Path

import httpx
import pytest
from testcontainers.core.container import DockerContainer
from testcontainers.core.network import Network

from daprmq_client import DaprMQClient

API_IMAGE = os.environ.get("DAPRMQ_API_IMAGE", "daprmq-api:test")
DAPR_VERSION = "1.18.4"
POSTGRES_PASSWORD = "test_password"
COMPONENTS_DIR = Path(__file__).resolve().parents[4] / "server" / "tests" / "DaprMQ.IntegrationTests" / "dapr-components"
STARTUP_TIMEOUT_SECONDS = 90


@dataclass(frozen=True)
class DaprMQServer:
    http_url: str
    grpc_address: str


def _world_writable_dir(prefix: str) -> str:
    path = tempfile.mkdtemp(prefix=prefix)
    os.chmod(path, stat.S_IRWXU | stat.S_IRWXG | stat.S_IRWXO)
    return path


def _wait_for(description: str, probe, timeout: float = STARTUP_TIMEOUT_SECONDS) -> None:
    deadline = time.monotonic() + timeout
    last_error: object = None
    while time.monotonic() < deadline:
        try:
            if probe():
                return
        except Exception as exc:  # noqa: BLE001 - any failure just means "not ready yet"
            last_error = exc
        time.sleep(0.5)
    raise TimeoutError(f"{description} not ready after {timeout}s (last error: {last_error!r})")


@pytest.fixture(scope="session")
def daprmq_server() -> Iterator[DaprMQServer]:
    if subprocess.run(["docker", "info"], capture_output=True).returncode != 0:
        pytest.skip("Docker daemon not available")

    image_check = subprocess.run(["docker", "image", "inspect", API_IMAGE], capture_output=True)
    if image_check.returncode != 0:
        pytest.fail(f"Docker image {API_IMAGE} not found - run ./build-and-test.sh --skip-tests from the repo root first.")

    scheduler_dir = _world_writable_dir("daprmq-scheduler-")
    blobstore_dir = _world_writable_dir("daprmq-blobstore-")
    containers: list[DockerContainer] = []

    def start(container: DockerContainer) -> DockerContainer:
        container.start()
        containers.append(container)
        return container

    with Network() as network:
        try:
            postgres = start(
                DockerContainer("postgres:16.2-alpine")
                .with_env("POSTGRES_DB", "actor_state")
                .with_env("POSTGRES_USER", "postgres")
                .with_env("POSTGRES_PASSWORD", POSTGRES_PASSWORD)
                .with_network(network)
                .with_network_aliases("postgres-db")
            )
            _wait_for(
                "postgres",
                lambda: postgres.exec(["pg_isready", "-U", "postgres", "-d", "actor_state"]).exit_code == 0,
            )

            start(
                DockerContainer(f"daprio/dapr:{DAPR_VERSION}")
                .with_network(network)
                .with_network_aliases("dapr-placement")
                .with_command("./placement -port 50005")
            )
            start(
                DockerContainer(f"daprio/dapr:{DAPR_VERSION}")
                .with_network(network)
                .with_network_aliases("dapr-scheduler")
                .with_volume_mapping(scheduler_dir, "/data/dapr-scheduler", "rw")
                .with_command("./scheduler --port 50006 --log-level info --etcd-data-dir /data/dapr-scheduler")
            )
            time.sleep(2)  # placement/scheduler have no health probe exposed to us; same grace period as the .NET fixture

            api = start(
                DockerContainer(API_IMAGE)
                .with_exposed_ports(5000, 5001)
                .with_network(network)
                .with_network_aliases("api-server")
                .with_env("ASPNETCORE_URLS", "http://+:5000")
                .with_env("REGISTER_ACTORS", "true")
                .with_env("DAPR_HTTP_ENDPOINT", "http://dapr-sidecar:3500")
                .with_env("DAPR_GRPC_ENDPOINT", "http://dapr-sidecar:50001")
                .with_env("Logging__LogLevel__Default", "Warning")
                .with_env("QUEUE_ACTOR_TYPE_NAME", "QueueActor")
                .with_env("HTTP_SINK_ACTOR_TYPE_NAME", "HttpSinkActor")
            )

            start(
                DockerContainer(f"daprio/daprd:{DAPR_VERSION}")
                .with_network(network)
                .with_network_aliases("dapr-sidecar")
                .with_volume_mapping(str(COMPONENTS_DIR), "/tmp/dapr-components", "ro")
                .with_volume_mapping(blobstore_dir, "/tmp/blobstore", "rw")
                .with_command(
                    "./daprd --app-id daprmq-api --app-channel-address api-server --app-port 5000 "
                    "--dapr-http-port 3500 --dapr-grpc-port 50001 "
                    "--placement-host-address dapr-placement:50005 --scheduler-host-address dapr-scheduler:50006 "
                    "--resources-path /tmp/dapr-components --config /tmp/dapr-components/config.yml --log-level info"
                )
            )

            http_url = f"http://localhost:{api.get_exposed_port(5000)}"
            grpc_address = f"localhost:{api.get_exposed_port(5001)}"

            def enqueue_probe() -> bool:
                response = httpx.post(
                    f"{http_url}/queue/readiness-{uuid.uuid4().hex}/enqueue",
                    json={"items": [{"item": {"probe": True}, "priority": 1}]},
                    timeout=5,
                )
                return response.status_code == 200

            _wait_for("DaprMQ API + sidecar (actors registered)", enqueue_probe)

            yield DaprMQServer(http_url=http_url, grpc_address=grpc_address)
        finally:
            for container in reversed(containers):
                container.stop()


@pytest.fixture
async def client(daprmq_server: DaprMQServer) -> AsyncIterator[DaprMQClient]:
    async with DaprMQClient(http_base_url=daprmq_server.http_url, grpc_address=daprmq_server.grpc_address) as c:
        yield c


@pytest.fixture
def queue_id() -> str:
    return f"py-it-{uuid.uuid4().hex}"
