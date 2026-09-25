/**
 * Starts a throwaway DaprMQ stack with Testcontainers (no docker compose, no pre-existing server),
 * mirroring server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs: Postgres,
 * Dapr placement + scheduler, the DaprMQ API server and a daprd sidecar on one private network with
 * dynamic host ports.
 *
 * Prerequisite: the API image must exist locally - build it with `./build-and-test.sh --skip-tests`
 * from the repo root (default tag `daprmq-api:test`, override with DAPRMQ_API_IMAGE).
 */
import { execFileSync } from "node:child_process";
import { chmodSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { randomUUID } from "node:crypto";
import { GenericContainer, Network, type StartedNetwork, type StartedTestContainer } from "testcontainers";

const API_IMAGE = process.env.DAPRMQ_API_IMAGE ?? "daprmq-api:test";
const DAPR_VERSION = "1.18.4";
const POSTGRES_PASSWORD = "test_password";
const COMPONENTS_DIR = resolve(import.meta.dirname, "../../../../server/tests/DaprMQ.IntegrationTests/dapr-components");
const STARTUP_TIMEOUT_MS = 90_000;

export interface DaprMQServer {
  httpUrl: string;
  grpcAddress: string;
  stop(): Promise<void>;
}

function docker(...args: string[]): boolean {
  try {
    execFileSync("docker", args, { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

export function dockerAvailable(): boolean {
  return docker("info");
}

function worldWritableDir(prefix: string): string {
  const path = mkdtempSync(join(tmpdir(), prefix));
  chmodSync(path, 0o777);
  return path;
}

async function waitFor(description: string, probe: () => Promise<boolean>): Promise<void> {
  const deadline = Date.now() + STARTUP_TIMEOUT_MS;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      if (await probe()) {
        return;
      }
    } catch (err) {
      lastError = err; // any failure just means "not ready yet"
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`${description} not ready after ${STARTUP_TIMEOUT_MS / 1000}s (last error: ${String(lastError)})`);
}

export async function startDaprMQServer(): Promise<DaprMQServer> {
  if (!docker("image", "inspect", API_IMAGE)) {
    throw new Error(`Docker image ${API_IMAGE} not found - run ./build-and-test.sh --skip-tests from the repo root first.`);
  }

  const schedulerDir = worldWritableDir("daprmq-scheduler-");
  const blobstoreDir = worldWritableDir("daprmq-blobstore-");
  const containers: StartedTestContainer[] = [];
  let network: StartedNetwork | undefined;

  const stop = async (): Promise<void> => {
    for (const container of containers.reverse()) {
      await container.stop();
    }
    containers.length = 0;
    await network?.stop();
    network = undefined;
  };

  try {
    network = await new Network().start();

    const postgres = await new GenericContainer("postgres:16.2-alpine")
      .withEnvironment({ POSTGRES_DB: "actor_state", POSTGRES_USER: "postgres", POSTGRES_PASSWORD: POSTGRES_PASSWORD })
      .withNetwork(network)
      .withNetworkAliases("postgres-db")
      .start();
    containers.push(postgres);
    await waitFor("postgres", async () => (await postgres.exec(["pg_isready", "-U", "postgres", "-d", "actor_state"])).exitCode === 0);

    containers.push(
      await new GenericContainer(`daprio/dapr:${DAPR_VERSION}`)
        .withNetwork(network)
        .withNetworkAliases("dapr-placement")
        .withCommand(["./placement", "-port", "50005"])
        .start(),
      await new GenericContainer(`daprio/dapr:${DAPR_VERSION}`)
        .withNetwork(network)
        .withNetworkAliases("dapr-scheduler")
        .withBindMounts([{ source: schedulerDir, target: "/data/dapr-scheduler", mode: "rw" }])
        .withCommand(["./scheduler", "--port", "50006", "--log-level", "info", "--etcd-data-dir", "/data/dapr-scheduler"])
        .start(),
    );
    await new Promise((r) => setTimeout(r, 2000)); // no health probe for placement/scheduler; same grace period as the .NET fixture

    const api = await new GenericContainer(API_IMAGE)
      .withExposedPorts(5000, 5001)
      .withNetwork(network)
      .withNetworkAliases("api-server")
      .withEnvironment({
        ASPNETCORE_URLS: "http://+:5000",
        REGISTER_ACTORS: "true",
        DAPR_HTTP_ENDPOINT: "http://dapr-sidecar:3500",
        DAPR_GRPC_ENDPOINT: "http://dapr-sidecar:50001",
        Logging__LogLevel__Default: "Warning",
        QUEUE_ACTOR_TYPE_NAME: "QueueActor",
        HTTP_SINK_ACTOR_TYPE_NAME: "HttpSinkActor",
      })
      .start();
    containers.push(api);

    containers.push(
      await new GenericContainer(`daprio/daprd:${DAPR_VERSION}`)
        .withNetwork(network)
        .withNetworkAliases("dapr-sidecar")
        .withBindMounts([
          { source: COMPONENTS_DIR, target: "/tmp/dapr-components", mode: "ro" },
          { source: blobstoreDir, target: "/tmp/blobstore", mode: "rw" },
        ])
        .withCommand([
          "./daprd", "--app-id", "daprmq-api", "--app-channel-address", "api-server", "--app-port", "5000",
          "--dapr-http-port", "3500", "--dapr-grpc-port", "50001",
          "--placement-host-address", "dapr-placement:50005", "--scheduler-host-address", "dapr-scheduler:50006",
          "--resources-path", "/tmp/dapr-components", "--config", "/tmp/dapr-components/config.yml", "--log-level", "info",
        ])
        .start(),
    );

    const httpUrl = `http://localhost:${api.getMappedPort(5000)}`;
    const grpcAddress = `localhost:${api.getMappedPort(5001)}`;

    await waitFor("DaprMQ API + sidecar (actors registered)", async () => {
      const response = await fetch(`${httpUrl}/queue/readiness-${randomUUID().replaceAll("-", "")}/enqueue`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ items: [{ item: { probe: true }, priority: 1 }] }),
        signal: AbortSignal.timeout(5000),
      });
      return response.status === 200;
    });

    return { httpUrl, grpcAddress, stop };
  } catch (err) {
    await stop();
    throw err;
  }
}
