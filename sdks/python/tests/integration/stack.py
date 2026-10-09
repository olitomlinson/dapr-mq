"""The throwaway DaprMQ stack the integration tests and the perf harness start with Testcontainers,
mirroring the .NET fixture in server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs:
Postgres, Dapr placement + scheduler, the DaprMQ API server and a daprd sidecar on one private
network with dynamic host ports.

`DaprTopology.perf(n)` scales it out as sdks/testing/PERFORMANCE_TESTS.md#scales-and-topology
describes: a 3-member scheduler HA cluster and n API replicas (each with its own sidecar), behind an
nginx load balancer when n > 1.

Prerequisite: the API image must exist locally - build it with `./build-and-test.sh --skip-tests`
from the repo root (default tag `daprmq-api:test`, override with DAPRMQ_API_IMAGE).
"""

from __future__ import annotations

import os
import stat
import subprocess
import tempfile
import time
from collections.abc import Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path

import httpx
from testcontainers.core.container import DockerContainer
from testcontainers.core.network import Network

API_IMAGE = os.environ.get("DAPRMQ_API_IMAGE", "daprmq-api:test")
DAPR_VERSION = "1.18.4"
POSTGRES_PASSWORD = "test_password"
COMPONENTS_DIR = Path(__file__).resolve().parents[4] / "server" / "tests" / "DaprMQ.IntegrationTests" / "dapr-components"
STARTUP_TIMEOUT_SECONDS = 90
SCHEDULER_PORT = 50006
LOAD_BALANCER_ALIAS = "api-lb"


@dataclass(frozen=True)
class DaprTopology:
    """Shape of the stack: the default is the single-everything integration stack."""

    api_replicas: int = 1
    scheduler_replicas: int = 1

    def __post_init__(self) -> None:
        if self.api_replicas < 1 or self.scheduler_replicas < 1:
            raise ValueError("api_replicas and scheduler_replicas must be >= 1.")
        if self.scheduler_replicas % 2 == 0:
            raise ValueError("etcd needs an odd scheduler member count.")

    @staticmethod
    def perf(api_replicas: int) -> DaprTopology:
        return DaprTopology(api_replicas=api_replicas, scheduler_replicas=3)

    @property
    def load_balanced(self) -> bool:
        return self.api_replicas > 1

    @property
    def scheduler_ha(self) -> bool:
        return self.scheduler_replicas > 1

    @staticmethod
    def api_server_alias(replica: int) -> str:
        return f"api-server-{replica}"

    @staticmethod
    def sidecar_alias(replica: int) -> str:
        return f"dapr-sidecar-{replica}"

    def scheduler_alias(self, member: int) -> str:
        return f"dapr-scheduler-{member}" if self.scheduler_ha else "dapr-scheduler"

    @property
    def scheduler_host_address(self) -> str:
        return ",".join(f"{self.scheduler_alias(i)}:{SCHEDULER_PORT}" for i in range(self.scheduler_replicas))

    def scheduler_command(self, member: int) -> list[str]:
        """One member of the HA cluster (embedded etcd, ephemeral data dir)."""
        initial_cluster = ",".join(
            f"{self.scheduler_alias(i)}=http://{self.scheduler_alias(i)}:2380" for i in range(self.scheduler_replicas)
        )
        alias = self.scheduler_alias(member)
        return [
            "./scheduler", "--port", str(SCHEDULER_PORT), "--log-level", "info",
            "--id", alias,
            "--etcd-initial-cluster", initial_cluster,
            "--etcd-client-listen-address", "0.0.0.0",
            "--etcd-data-dir", "/tmp/etcd",
            "--override-broadcast-host-port", f"{alias}:{SCHEDULER_PORT}",
        ]  # fmt: skip

    def nginx_config(self) -> str:
        """REST on 5000, gRPC (h2c) on 5001, with 1 h timeouts so ConsumeSession streams survive."""

        def servers(port: int) -> str:
            return "".join(f"    server {self.api_server_alias(i)}:{port};\n" for i in range(self.api_replicas))

        return (
            "worker_processes auto;\n"
            "events { worker_connections 8192; }\n"
            "http {\n"
            "  access_log off;\n"
            f"  upstream api_rest {{\n{servers(5000)}    keepalive 256;\n  }}\n"
            f"  upstream api_grpc {{\n{servers(5001)}    keepalive 256;\n  }}\n"
            "  server {\n"
            "    listen 5000;\n"
            "    location / {\n"
            "      proxy_pass http://api_rest;\n"
            "      proxy_http_version 1.1;\n"
            '      proxy_set_header Connection "";\n'
            "      proxy_read_timeout 1h;\n"
            "      client_max_body_size 0;\n"
            "    }\n"
            "  }\n"
            "  server {\n"
            "    listen 5001 http2;\n"
            "    http2_max_concurrent_streams 10000;\n"
            "    client_body_timeout 1h;\n"
            "    client_max_body_size 0;\n"
            "    location / {\n"
            "      grpc_pass grpc://api_grpc;\n"
            "      grpc_read_timeout 1h;\n"
            "      grpc_send_timeout 1h;\n"
            "    }\n"
            "  }\n"
            "}\n"
        )


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


def _operations_ready(http_url: str) -> bool:
    # The daprmq.DaprMQ.operations signal over HTTP: queue operations can be served.
    try:
        return httpx.get(f"{http_url}/health/operations", timeout=5).status_code == 200
    except httpx.HTTPError:
        return False


def require_api_image() -> str | None:
    """None when Docker and the API image are available, else why not."""
    if subprocess.run(["docker", "info"], capture_output=True).returncode != 0:
        return "Docker daemon not available"
    if subprocess.run(["docker", "image", "inspect", API_IMAGE], capture_output=True).returncode != 0:
        return f"Docker image {API_IMAGE} not found - run ./build-and-test.sh --skip-tests from the repo root first."
    return None


@contextmanager
def start_stack(topology: DaprTopology = DaprTopology(), image: str = API_IMAGE) -> Iterator[DaprMQServer]:
    """Starts the stack, waits until every API replica can serve queue operations, and yields the
    endpoints the SDK should use (the load balancer's when there is one)."""
    scheduler_dir = _world_writable_dir("daprmq-scheduler-")
    blobstore_dir = _world_writable_dir("daprmq-blobstore-")
    nginx_conf: str | None = None
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
            # HA members keep etcd in the container (ephemeral); the single scheduler keeps the
            # bind-mounted data dir. Members only reach quorum together, so none is waited on alone.
            for member in range(topology.scheduler_replicas):
                scheduler = (
                    DockerContainer(f"daprio/dapr:{DAPR_VERSION}")
                    .with_network(network)
                    .with_network_aliases(topology.scheduler_alias(member))
                )
                if topology.scheduler_ha:
                    scheduler = scheduler.with_command(topology.scheduler_command(member))
                else:
                    scheduler = scheduler.with_volume_mapping(scheduler_dir, "/data/dapr-scheduler", "rw").with_command(
                        "./scheduler --port 50006 --log-level info --etcd-data-dir /data/dapr-scheduler"
                    )
                start(scheduler)
            time.sleep(2)  # placement/scheduler have no health probe exposed to us; same grace period as the .NET fixture

            apis: list[DockerContainer] = []
            for replica in range(topology.api_replicas):
                api_alias, sidecar_alias = topology.api_server_alias(replica), topology.sidecar_alias(replica)
                # Replica 0 also answers to the historical single-instance aliases.
                legacy = replica == 0
                apis.append(
                    start(
                        DockerContainer(image)
                        .with_exposed_ports(5000, 5001)
                        .with_network(network)
                        .with_network_aliases(*([api_alias, "api-server"] if legacy else [api_alias]))
                        .with_env("ASPNETCORE_URLS", "http://+:5000")
                        .with_env("REGISTER_ACTORS", "true")
                        .with_env("DAPR_HTTP_ENDPOINT", f"http://{sidecar_alias}:3500")
                        .with_env("DAPR_GRPC_ENDPOINT", f"http://{sidecar_alias}:50001")
                        .with_env("Logging__LogLevel__Default", "Warning")
                        .with_env("QUEUE_ACTOR_TYPE_NAME", "QueueActor")
                        .with_env("HTTP_SINK_ACTOR_TYPE_NAME", "HttpSinkActor")
                    )
                )
                start(
                    DockerContainer(f"daprio/daprd:{DAPR_VERSION}")
                    .with_network(network)
                    .with_network_aliases(*([sidecar_alias, "dapr-sidecar"] if legacy else [sidecar_alias]))
                    .with_volume_mapping(str(COMPONENTS_DIR), "/tmp/dapr-components", "ro")
                    .with_volume_mapping(blobstore_dir, "/tmp/blobstore", "rw")
                    .with_command(
                        f"./daprd --app-id daprmq-api --app-channel-address {api_alias} --app-port 5000 "
                        "--dapr-http-port 3500 --dapr-grpc-port 50001 "
                        f"--placement-host-address dapr-placement:50005 --scheduler-host-address {topology.scheduler_host_address} "
                        "--resources-path /tmp/dapr-components --config /tmp/dapr-components/config.yml --log-level info"
                    )
                )

            # Every replica on its own, not just through the load balancer (which answers as soon as one is up).
            for api in apis:
                url = f"http://localhost:{api.get_exposed_port(5000)}"
                _wait_for(f"DaprMQ API + sidecar at {url} (queue operations servable)", lambda url=url: _operations_ready(url))

            front = apis[0]
            if topology.load_balanced:
                # nginx resolves its upstreams at startup, so it goes last, once every replica's alias exists.
                fd, nginx_conf = tempfile.mkstemp(prefix="daprmq-nginx-", suffix=".conf")
                with os.fdopen(fd, "w") as f:
                    f.write(topology.nginx_config())
                os.chmod(nginx_conf, 0o644)
                front = start(
                    DockerContainer("nginx:1.27-alpine")
                    .with_exposed_ports(5000, 5001)
                    .with_network(network)
                    .with_network_aliases(LOAD_BALANCER_ALIAS)
                    .with_volume_mapping(nginx_conf, "/etc/nginx/nginx.conf", "ro")
                )
                lb_url = f"http://localhost:{front.get_exposed_port(5000)}"
                _wait_for("nginx load balancer", lambda: _operations_ready(lb_url))

            yield DaprMQServer(
                http_url=f"http://localhost:{front.get_exposed_port(5000)}",
                grpc_address=f"localhost:{front.get_exposed_port(5001)}",
            )
        finally:
            for container in reversed(containers):
                container.stop()
            if nginx_conf is not None:
                os.remove(nginx_conf)
