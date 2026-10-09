/**
 * Schema-2 run records (sdks/testing/perf/result.schema.json), built exactly as
 * sdks/dotnet/perf/DaprMQ.Client.Perf/RunRecords.cs builds them, and saved in the shared layout:
 * <out>/sdk-typescript/runs/<runId>.json in full, <out>/sdk-typescript/history.jsonl without the timeline.
 */
import { execFileSync } from "node:child_process";
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { cpus, hostname, release, type } from "node:os";
import { join } from "node:path";
import type { LoadStep, StepTimeline } from "./metrics.js";
import type { LoadParams, QueueDrainParams, SessionDrainParams } from "./profiles.js";

export const SCHEMA_VERSION = 2;
export const SDK = "typescript";

export interface RunEnvironment {
  label: string;
  gitSha: string | null;
  gitBranch: string | null;
  gitDirty: boolean;
  os: string;
  cpuCount: number;
  runtime: string;
  server: string;
}

function git(...args: string[]): string | null {
  try {
    return execFileSync("git", args, { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return null;
  }
}

export function captureEnvironment(label: string, server: string): RunEnvironment {
  return {
    label,
    gitSha: process.env.GITHUB_SHA || git("rev-parse", "HEAD"),
    gitBranch: process.env.GITHUB_HEAD_REF || process.env.GITHUB_REF_NAME || git("rev-parse", "--abbrev-ref", "HEAD"),
    gitDirty: Boolean(git("status", "--porcelain", "--untracked-files=no")),
    os: `${type()} ${release()}`,
    cpuCount: cpus().length,
    runtime: `Node ${process.versions.node}`,
    server,
  };
}

export function defaultEnvLabel(): string {
  return `local-${hostname().split(".")[0].toLowerCase()}`;
}

export function sdkVersion(): string | null {
  try {
    return JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")).version ?? null;
  } catch {
    return null;
  }
}

/** Everything about a run that isn't its scenario or result. */
export interface RunContext {
  timestamp: Date;
  environment: RunEnvironment;
  scale: string;
  apiReplicas: number;
  schedulerReplicas: number | null;
  sdkVersion: string | null;
}

export type RunRecord = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

/** yyyyMMdd'T'HHmmss'Z' */
function compactTimestamp(t: Date): string {
  return t.toISOString().replace(/[-:]/g, "").replace(/\.\d+Z$/, "Z");
}

function envelope(
  context: RunContext,
  profile: string,
  id: string,
  name: string,
  key: string,
  params: Record<string, unknown>,
  metrics: Record<string, unknown>,
  failures: string[],
): RunRecord {
  const env = context.environment;
  return {
    schemaVersion: SCHEMA_VERSION,
    runId: `${compactTimestamp(context.timestamp)}_${SDK}_${env.label}_${profile}`,
    timestampUtc: context.timestamp.toISOString(),
    sdk: { name: SDK, version: context.sdkVersion, runtime: env.runtime },
    environment: { label: env.label, gitSha: env.gitSha, gitBranch: env.gitBranch, gitDirty: env.gitDirty, os: env.os, cpuCount: env.cpuCount },
    topology: { apiReplicas: context.apiReplicas, schedulerReplicas: context.schedulerReplicas, loadBalancer: context.apiReplicas > 1, server: env.server },
    scale: context.scale,
    scenario: { id, name, profile, key, params },
    metrics,
    checks: { passed: failures.length === 0, failures },
  };
}

export interface LoadResult {
  steps: LoadStep[];
  timelines: StepTimeline[];
  drained: boolean;
  sampleErrors: string[];
}

export function loadRecord(context: RunContext, profile: string, load: LoadParams, result: LoadResult): RunRecord {
  // A ramp's headline is its best step; ops/messages/errors cover every step.
  const best = result.steps.reduce((a, b) => (b.messagesPerSecond > a.messagesPerSecond ? b : a));
  const total = (pick: (s: LoadStep) => number): number => result.steps.reduce((n, s) => n + pick(s), 0);
  const errors = total((s) => s.errors);
  const metrics = {
    opsPerSecond: best.opsPerSecond,
    messagesPerSecond: best.messagesPerSecond,
    latencyMs: best.latencyMs,
    ops: total((s) => s.ops),
    messages: total((s) => s.messages),
    errors,
  };

  const failures: string[] = [];
  if (errors > 0) {
    failures.push(`${errors} errors` + (result.sampleErrors.length > 0 ? ` (first: ${result.sampleErrors[0]})` : ""));
  }
  if (result.drained) {
    failures.push("a queue drained before the window ended: raise seedPerQueue");
  }

  const run = envelope(context, profile, load.id, load.scenario, load.key, load.params(), metrics, failures);
  run.steps = result.steps;
  const names = ["opsPerSecond", "messagesPerSecond", "latencyP50Ms", "latencyP95Ms", "latencyP99Ms", "errors", "concurrency"] as const;
  run.timeline = {
    bucketMs: 1000,
    series: Object.fromEntries(names.map((name) => [name, result.timelines.flatMap((t) => t[name])])),
  };
  return run;
}

export function sessionDrainRecord(context: RunContext, profile: string, scenario: SessionDrainParams, result: Record<string, unknown>): RunRecord {
  const { busySlotsTimeline, timelineBucketMs, ...metrics } = result;
  const failures: string[] = [];
  if ((metrics.missing as number) > 0) {
    failures.push(`${metrics.missing} messages missing`);
  }
  if ((metrics.fifoViolations as number) > 0) {
    failures.push(`${metrics.fifoViolations} FIFO violations`);
  }

  const run = envelope(context, profile, "P-04", "session-drain", scenario.key, scenario.params(), metrics, failures);
  run.timeline = { bucketMs: timelineBucketMs, series: { busySlots: busySlotsTimeline } };
  return run;
}

export function queueDrainRecord(context: RunContext, profile: string, scenario: QueueDrainParams, result: Record<string, unknown>): RunRecord {
  const { messagesPerSecondTimeline, busyHandlersTimeline, ...metrics } = result;
  const failures: string[] = [];
  if ((metrics.missing as number) > 0) {
    failures.push(`${metrics.missing} messages missing`);
  }
  // Above a window of 1, handlers legitimately start messages out of queue order.
  if (scenario.strictOrder && (metrics.orderViolations as number) > 0) {
    failures.push(`${metrics.orderViolations} order violations`);
  }

  const run = envelope(context, profile, "P-05", "queue-drain", scenario.key, scenario.params(), metrics, failures);
  run.timeline = { bucketMs: 1000, series: { messagesPerSecond: messagesPerSecondTimeline, busyHandlers: busyHandlersTimeline } };
  return run;
}

export function save(root: string, run: RunRecord): string {
  const sdkDir = join(root, `sdk-${run.sdk.name}`);
  const runsDir = join(sdkDir, "runs");
  mkdirSync(runsDir, { recursive: true });

  const runPath = join(runsDir, `${run.runId}.json`);
  writeFileSync(runPath, JSON.stringify(run, null, 2));

  const { timeline: _timeline, ...summary } = run;
  appendFileSync(join(sdkDir, "history.jsonl"), JSON.stringify(summary) + "\n");
  return runPath;
}
