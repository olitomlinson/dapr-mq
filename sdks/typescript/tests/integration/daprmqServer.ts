/**
 * Starts a throwaway DaprMQ stack with Testcontainers (no docker compose, no pre-existing server),
 * mirroring server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs: Postgres,
 * Dapr placement + scheduler, the DaprMQ API server and a daprd sidecar on one private network with
 * dynamic host ports.
 *
 * `DaprTopology.perf(n)` scales it out as sdks/testing/PERFORMANCE_TESTS.md#scales-and-topology
 * describes: a 3-member scheduler HA cluster and n API replicas (each with its own sidecar), behind
 * an nginx load balancer when n > 1. The perf harness (sdks/typescript/perf) uses it.
 *
 * Prerequisite: the API image must exist locally - build it with `./build-and-test.sh --skip-tests`
 * from the repo root (default tag `daprmq-api:test`, override with DAPRMQ_API_IMAGE).
 */
import { execFileSync } from "node:child_process";
import { chmodSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { GenericContainer, Network, type StartedNetwork, type StartedTestContainer } from "testcontainers";

export const API_IMAGE = process.env.DAPRMQ_API_IMAGE ?? "daprmq-api:test";
const DAPR_VERSION = "1.18.4";
const POSTGRES_PASSWORD = "test_password";
const COMPONENTS_DIR = resolve(import.meta.dirname, "../../../../server/tests/DaprMQ.IntegrationTests/dapr-components");
const STARTUP_TIMEOUT_MS = 90_000;
const SCHEDULER_PORT = 50006;
const LOAD_BALANCER_ALIAS = "api-lb";

/** Shape of the stack: the default is the single-everything integration stack. */
export class DaprTopology {
  constructor(
    readonly apiReplicas = 1,
    readonly schedulerReplicas = 1,
  ) {
    if (apiReplicas < 1 || schedulerReplicas < 1) {
      throw new Error("apiReplicas and schedulerReplicas must be >= 1.");
    }
    if (schedulerReplicas % 2 === 0) {
      throw new Error("etcd needs an odd scheduler member count.");
    }
  }

  static perf(apiReplicas: number): DaprTopology {
    return new DaprTopology(apiReplicas, 3);
  }

  get loadBalanced(): boolean {
    return this.apiReplicas > 1;
  }

  get schedulerHa(): boolean {
    return this.schedulerReplicas > 1;
  }

  static apiServerAlias(replica: number): string {
    return `api-server-${replica}`;
  }

  static sidecarAlias(replica: number): string {
    return `dapr-sidecar-${replica}`;
  }

  schedulerAlias(member: number): string {
    return this.schedulerHa ? `dapr-scheduler-${member}` : "dapr-scheduler";
  }

  private members(): number[] {
    return Array.from({ length: this.schedulerReplicas }, (_, i) => i);
  }

  get schedulerHostAddress(): string {
    return this.members().map((i) => `${this.schedulerAlias(i)}:${SCHEDULER_PORT}`).join(",");
  }

  /** One member of the HA cluster (embedded etcd, ephemeral data dir). */
  schedulerCommand(member: number): string[] {
    const initialCluster = this.members().map((i) => `${this.schedulerAlias(i)}=http://${this.schedulerAlias(i)}:2380`).join(",");
    const alias = this.schedulerAlias(member);
    return [
      "./scheduler", "--port", `${SCHEDULER_PORT}`, "--log-level", "info",
      "--id", alias,
      "--etcd-initial-cluster", initialCluster,
      "--etcd-client-listen-address", "0.0.0.0",
      "--etcd-data-dir", "/tmp/etcd",
      "--override-broadcast-host-port", `${alias}:${SCHEDULER_PORT}`,
    ];
  }

  /** REST on 5000, gRPC (h2c) on 5001, with 1 h timeouts so ConsumeSession streams survive. */
  nginxConfig(): string {
    const servers = (port: number): string =>
      Array.from({ length: this.apiReplicas }, (_, i) => `    server ${DaprTopology.apiServerAlias(i)}:${port};\n`).join("");
    return (
      "worker_processes auto;\n" +
      "events { worker_connections 8192; }\n" +
      "http {\n" +
      "  access_log off;\n" +
      `  upstream api_rest {\n${servers(5000)}    keepalive 256;\n  }\n` +
      `  upstream api_grpc {\n${servers(5001)}    keepalive 256;\n  }\n` +
      "  server {\n" +
      "    listen 5000;\n" +
      "    location / {\n" +
      "      proxy_pass http://api_rest;\n" +
      "      proxy_http_version 1.1;\n" +
      '      proxy_set_header Connection "";\n' +
      "      proxy_read_timeout 1h;\n" +
      "      client_max_body_size 0;\n" +
      "    }\n" +
      "  }\n" +
      "  server {\n" +
      "    listen 5001 http2;\n" +
      "    http2_max_concurrent_streams 10000;\n" +
      "    client_body_timeout 1h;\n" +
      "    client_max_body_size 0;\n" +
      "    location / {\n" +
      "      grpc_pass grpc://api_grpc;\n" +
      "      grpc_read_timeout 1h;\n" +
      "      grpc_send_timeout 1h;\n" +
      "    }\n" +
      "  }\n" +
      "}\n"
    );
  }
}

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

export function apiImageAvailable(image = API_IMAGE): boolean {
  return docker("image", "inspect", image);
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

async function operationsReady(httpUrl: string): Promise<boolean> {
  // The daprmq.DaprMQ.operations signal over HTTP: queue operations can be served.
  const response = await fetch(`${httpUrl}/health/operations`, { signal: AbortSignal.timeout(5000) });
  return response.status === 200;
}

export async function startDaprMQServer(topology = new DaprTopology(), image = API_IMAGE): Promise<DaprMQServer> {
  if (!apiImageAvailable(image)) {
    throw new Error(`Docker image ${image} not found - run ./build-and-test.sh --skip-tests from the repo root first.`);
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
    );
    // HA members keep etcd in the container (ephemeral); the single scheduler keeps the bind-mounted
    // data dir. Members only reach quorum together, so they start in parallel.
    const schedulers = await Promise.all(
      Array.from({ length: topology.schedulerReplicas }, (_, member) => {
        const scheduler = new GenericContainer(`daprio/dapr:${DAPR_VERSION}`)
          .withNetwork(network!)
          .withNetworkAliases(topology.schedulerAlias(member));
        return (
          topology.schedulerHa
            ? scheduler.withCommand(topology.schedulerCommand(member))
            : scheduler
                .withBindMounts([{ source: schedulerDir, target: "/data/dapr-scheduler", mode: "rw" }])
                .withCommand(["./scheduler", "--port", "50006", "--log-level", "info", "--etcd-data-dir", "/data/dapr-scheduler"])
        ).start();
      }),
    );
    containers.push(...schedulers);
    await new Promise((r) => setTimeout(r, 2000)); // no health probe for placement/scheduler; same grace period as the .NET fixture

    const apis: StartedTestContainer[] = [];
    for (let replica = 0; replica < topology.apiReplicas; replica++) {
      const apiAlias = DaprTopology.apiServerAlias(replica);
      const sidecarAlias = DaprTopology.sidecarAlias(replica);
      // Replica 0 also answers to the historical single-instance aliases.
      const legacy = replica === 0;
      const api = await new GenericContainer(image)
        .withExposedPorts(5000, 5001)
        .withNetwork(network)
        .withNetworkAliases(...(legacy ? [apiAlias, "api-server"] : [apiAlias]))
        .withEnvironment({
          ASPNETCORE_URLS: "http://+:5000",
          REGISTER_ACTORS: "true",
          DAPR_HTTP_ENDPOINT: `http://${sidecarAlias}:3500`,
          DAPR_GRPC_ENDPOINT: `http://${sidecarAlias}:50001`,
          Logging__LogLevel__Default: "Warning",
          QUEUE_ACTOR_TYPE_NAME: "QueueActor",
          HTTP_SINK_ACTOR_TYPE_NAME: "HttpSinkActor",
        })
        .start();
      containers.push(api);
      apis.push(api);

      containers.push(
        await new GenericContainer(`daprio/daprd:${DAPR_VERSION}`)
          .withNetwork(network)
          .withNetworkAliases(...(legacy ? [sidecarAlias, "dapr-sidecar"] : [sidecarAlias]))
          .withBindMounts([
            { source: COMPONENTS_DIR, target: "/tmp/dapr-components", mode: "ro" },
            { source: blobstoreDir, target: "/tmp/blobstore", mode: "rw" },
          ])
          .withCommand([
            "./daprd", "--app-id", "daprmq-api", "--app-channel-address", apiAlias, "--app-port", "5000",
            "--dapr-http-port", "3500", "--dapr-grpc-port", "50001",
            "--placement-host-address", "dapr-placement:50005", "--scheduler-host-address", topology.schedulerHostAddress,
            "--resources-path", "/tmp/dapr-components", "--config", "/tmp/dapr-components/config.yml", "--log-level", "info",
          ])
          .start(),
      );
    }

    // Every replica on its own, not just through the load balancer (which answers as soon as one is up).
    for (const api of apis) {
      const url = `http://localhost:${api.getMappedPort(5000)}`;
      await waitFor(`DaprMQ API + sidecar at ${url} (queue operations servable)`, () => operationsReady(url));
    }

    let front = apis[0];
    if (topology.loadBalanced) {
      // nginx resolves its upstreams at startup, so it goes last, once every replica's alias exists.
      front = await new GenericContainer("nginx:1.27-alpine")
        .withExposedPorts(5000, 5001)
        .withNetwork(network)
        .withNetworkAliases(LOAD_BALANCER_ALIAS)
        .withCopyContentToContainer([{ content: topology.nginxConfig(), target: "/etc/nginx/nginx.conf" }])
        .start();
      containers.push(front);
      const lbUrl = `http://localhost:${front.getMappedPort(5000)}`;
      await waitFor("nginx load balancer", () => operationsReady(lbUrl));
    }

    return { httpUrl: `http://localhost:${front.getMappedPort(5000)}`, grpcAddress: `localhost:${front.getMappedPort(5001)}`, stop };
  } catch (err) {
    await stop();
    throw err;
  }
}
