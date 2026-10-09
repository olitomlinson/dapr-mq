/**
 * TypeScript SDK performance harness (sdks/testing/PERFORMANCE_TESTS.md). From sdks/typescript:
 *
 *   npm run perf -- --suite pr                 # every pr profile against one throwaway stack
 *   npm run perf -- --profile enqueue-ramp --api-replicas 3
 *   npm run perf -- --http http://localhost:8002 --grpc localhost:8102 --profile steady-drain
 *
 * Runs go to <out>/sdk-typescript; the cross-SDK report and the regression table come from the
 * .NET PerfReport tool, so `dotnet` must be on PATH for those (the runs are saved regardless).
 */
import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { DaprMQClient } from "../src/index.js";
import { API_IMAGE, DaprTopology, apiImageAvailable, dockerAvailable, startDaprMQServer, type DaprMQServer } from "../tests/integration/daprmqServer.js";
import { parse, scaleFor, type PerfOptions } from "./options.js";
import { LOAD_PROFILES, QUEUE_DRAIN_PROFILES, SESSION_DRAIN_PROFILES } from "./profiles.js";
import { captureEnvironment, loadRecord, queueDrainRecord, save, sdkVersion, sessionDrainRecord, type RunEnvironment, type RunRecord } from "./records.js";
import { LoadScenario, QueueDrainScenario, SessionDrainScenario } from "./scenarios.js";

const PERF_REPORT = resolve(fileURLToPath(import.meta.url), "../../../testing/perf/DaprMQ.PerfReport");

/** Runs the shared .NET report tool; its own output goes straight to the console. */
function perfReport(...args: string[]): number {
  const result = spawnSync("dotnet", ["run", "-c", "Release", "--project", PERF_REPORT, "--", ...args], { stdio: "inherit" });
  if (result.error) {
    console.error("dotnet not found: skipping the report and regression check.");
    return 0;
  }
  return result.status ?? 1;
}

async function runProfiles(
  options: PerfOptions,
  client: DaprMQClient,
  environment: RunEnvironment,
  schedulerReplicas: number | null,
): Promise<{ exitCode: number; runIds: string[] }> {
  let exitCode = 0;
  const runIds: string[] = [];
  for (const profile of options.profiles) {
    console.log(`\n=== Profile ${profile} ===`);
    const context = {
      timestamp: new Date(),
      environment,
      scale: scaleFor(options, profile),
      apiReplicas: options.apiReplicas,
      schedulerReplicas,
      sdkVersion: sdkVersion(),
    };

    let record: RunRecord;
    if (profile in LOAD_PROFILES) {
      const load = LOAD_PROFILES[profile];
      record = loadRecord(context, profile, load, await new LoadScenario(client, load).run());
    } else if (profile in QUEUE_DRAIN_PROFILES) {
      const scenario = QUEUE_DRAIN_PROFILES[profile];
      const result = await new QueueDrainScenario(client, scenario).run();
      record = queueDrainRecord(context, profile, scenario, result);
      printQueueDrain(result);
    } else {
      const scenario = SESSION_DRAIN_PROFILES[profile];
      const result = await new SessionDrainScenario(client, scenario).run();
      record = sessionDrainRecord(context, profile, scenario, result);
      printSessionDrain(result, scenario.maxConcurrentSessions);
    }

    console.log(`Run:    ${save(options.outDir, record)}`);
    runIds.push(record.runId);
    // Lost/reordered messages or failed operations make the timings meaningless - fail the run (and CI).
    if (!record.checks.passed) {
      console.log(`FAILED: ${JSON.stringify(record.checks.failures)}`);
      exitCode = 1;
    }
  }
  return { exitCode, runIds };
}

async function run(options: PerfOptions): Promise<number> {
  let server: DaprMQServer | undefined;
  let httpUrl: string;
  let grpcAddress: string;
  let description: string;
  let schedulerReplicas: number | null = null;
  if (options.http !== null) {
    [httpUrl, grpcAddress, description] = [options.http, options.grpc!, `external ${options.http}`];
  } else {
    if (!dockerAvailable() || !apiImageAvailable()) {
      console.error(`Docker and the ${API_IMAGE} image are required - run ./build-and-test.sh --skip-tests from the repo root first.`);
      return 2;
    }
    const topology = DaprTopology.perf(options.apiReplicas);
    schedulerReplicas = topology.schedulerReplicas;
    console.log(`Starting Testcontainers stack (${API_IMAGE}, ${topology.apiReplicas} API replica(s), ${schedulerReplicas} schedulers)...`);
    server = await startDaprMQServer(topology);
    [httpUrl, grpcAddress, description] = [server.httpUrl, server.grpcAddress, `testcontainers ${API_IMAGE}`];
  }

  let exitCode: number;
  let runIds: string[];
  const client = new DaprMQClient({ httpBaseUrl: httpUrl, grpcAddress });
  try {
    await client.waitForReady({ signal: AbortSignal.timeout(120_000) });
    ({ exitCode, runIds } = await runProfiles(options, client, captureEnvironment(options.envLabel, description), schedulerReplicas));
  } finally {
    client.close();
    await server?.stop();
  }

  perfReport("report", "--results", options.outDir);
  const check = ["check", "--results", options.outDir, "--runs", runIds.join(","), "--baseline-branch", options.baselineBranch];
  const regressed = perfReport(...check, ...(options.gate ? ["--gate"] : [])) === 3;
  return exitCode === 0 && regressed ? 3 : exitCode;
}

/* eslint-disable @typescript-eslint/no-explicit-any */
const pct = (v: number): string => `${(v * 100).toFixed(1)}%`;

function printSessionDrain(r: Record<string, any>, slots: number): void {
  const p = r.peak;
  console.log("\n=== Session drain ===");
  console.log(`Seed:                 ${r.seedSeconds.toFixed(1).padStart(10)} s`);
  console.log(`Wall clock:           ${r.wallClockSeconds.toFixed(1).padStart(10)} s   (ideal ${r.idealSeconds.toFixed(0)} s, efficiency ${pct(r.efficiency)})`);
  console.log(`First message after:  ${r.timeToFirstMessageSeconds.toFixed(2).padStart(10)} s`);
  console.log(`Peak window:          ${p.startSeconds.toFixed(1)}-${p.endSeconds.toFixed(1)} s, ${slots} slots, utilisation ${pct(p.utilization)}`);
  console.log(`Claim latency ms:     p50 ${r.claimLatencyMs.p50.toFixed(0)}  p95 ${r.claimLatencyMs.p95.toFixed(0)}  max ${r.claimLatencyMs.max.toFixed(0)}`);
  console.log(`Delivery ms:          p50 ${r.deliveryLatencyMs.p50.toFixed(0)}  p95 ${r.deliveryLatencyMs.p95.toFixed(0)}  max ${r.deliveryLatencyMs.max.toFixed(0)}`);
  console.log(`Streams ${r.streams} (failed claims ${r.failedClaims}, re-claimed sessions ${r.sessionsClaimedMoreThanOnce})`);
  console.log(`Messages ${r.messagesHandled} (duplicates ${r.duplicates}, missing ${r.missing}, FIFO violations ${r.fifoViolations})`);
}

function printQueueDrain(r: Record<string, any>): void {
  const ideal = r.idealSeconds !== null ? `   (ideal ${r.idealSeconds.toFixed(1)} s, efficiency ${pct(r.efficiency)})` : "";
  console.log("\n=== Queue drain ===");
  console.log(`Publish:              ${r.seedSeconds.toFixed(1).padStart(10)} s`);
  console.log(`Wall clock:           ${r.wallClockSeconds.toFixed(1).padStart(10)} s${ideal}`);
  console.log(`Throughput:           ${r.messagesPerSecond.toFixed(0).padStart(10)} msg/s`);
  console.log(`First message after:  ${r.timeToFirstMessageSeconds.toFixed(2).padStart(10)} s`);
  console.log(`Peak handlers:        ${String(r.peakConcurrentHandlers).padStart(10)}`);
  console.log(`Delivery ms:          p50 ${r.deliveryLatencyMs.p50.toFixed(0)}  p95 ${r.deliveryLatencyMs.p95.toFixed(0)}  max ${r.deliveryLatencyMs.max.toFixed(0)}`);
  console.log(`Messages ${r.messagesHandled} (duplicates ${r.duplicates}, missing ${r.missing}, order violations ${r.orderViolations})`);
}
/* eslint-enable @typescript-eslint/no-explicit-any */

async function main(): Promise<number> {
  let options: PerfOptions;
  try {
    options = parse(process.argv.slice(2));
  } catch (err) {
    console.error((err as Error).message);
    return 2;
  }
  mkdirSync(join(options.outDir, "sdk-typescript"), { recursive: true });
  if (options.reportOnly) {
    return perfReport("report", "--results", options.outDir);
  }
  return run(options);
}

main().then(
  (code) => process.exit(code),
  (err) => {
    console.error(err);
    process.exit(1);
  },
);
