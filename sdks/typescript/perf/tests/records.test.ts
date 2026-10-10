/** Port of RunRecordsTests (.NET): the record shape the shared report reads. */
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { computeSessionDrain, type LoadStep, type StepTimeline } from "../metrics.js";
import { LOAD_PROFILES, QUEUE_DRAIN_PROFILES, SESSION_DRAIN_PROFILES } from "../profiles.js";
import { loadRecord, queueDrainRecord, save, sessionDrainRecord, type RunContext } from "../records.js";

const context: RunContext = {
  timestamp: new Date(Date.UTC(2026, 9, 5, 10, 0, 0)),
  environment: { label: "ci", gitSha: "abc123", gitBranch: "main", gitDirty: false, os: "Linux", cpuCount: 4, runtime: "Node 22.9.0", server: "testcontainers daprmq-api:test" },
  scale: "pr",
  apiReplicas: 2,
  schedulerReplicas: 3,
  sdkVersion: "0.1.0",
};

const step = (concurrency: number, messagesPerSecond: number, errors = 0): LoadStep => ({
  concurrency, queues: concurrency, durationSeconds: 30, ops: 100, messages: messagesPerSecond * 30, errors,
  opsPerSecond: messagesPerSecond, messagesPerSecond, latencyMs: { count: 100, mean: 2, p50: 1, p95: 3, p99: 4, max: 5 },
});
const timeline = (concurrency: number, ...ops: number[]): StepTimeline => ({
  opsPerSecond: ops, messagesPerSecond: ops, latencyP50Ms: ops, latencyP95Ms: ops, latencyP99Ms: ops,
  errors: ops.map(() => 0), concurrency: ops.map(() => concurrency),
});

const queueResult = (missing = 0, orderViolations = 0): Record<string, unknown> => ({
  seedSeconds: 0.4, wallClockSeconds: 5, messagesPerSecond: 800, idealSeconds: 0.4, efficiency: 0.08, timeToFirstMessageSeconds: 0.02,
  peakConcurrentHandlers: 100, deliveryLatencyMs: { count: 4000, mean: 2000, p50: 2000, p95: 4000, p99: 4500, max: 5000 },
  messagesHandled: 4000 - missing, duplicates: 0, missing, orderViolations, messagesPerSecondTimeline: [700, 900], busyHandlersTimeline: [95.5, 99],
});

describe("run records", () => {
  it("records a load run's identity, topology and scenario", () => {
    const run = loadRecord(context, "enqueue", LOAD_PROFILES.enqueue, { steps: [step(8, 500)], timelines: [timeline(8, 500, 500)], drained: false, sampleErrors: [] });

    expect(run.schemaVersion).toBe(2);
    expect(run.runId).toBe("20261005T100000Z_typescript_ci_enqueue");
    expect(run.timestampUtc).toBe("2026-10-05T10:00:00.000Z");
    expect(run.sdk).toEqual({ name: "typescript", version: "0.1.0", runtime: "Node 22.9.0" });
    expect(run.topology).toEqual({ apiReplicas: 2, schedulerReplicas: 3, loadBalancer: true, server: "testcontainers daprmq-api:test" });
    expect(run.scale).toBe("pr");
    expect(run.scenario.id).toBe("P-01");
    expect(run.scenario.key).toBe("enqueue:c8/q8/b1/256B/3+15s");
    expect(run.scenario.params.queues).toBe(8);
    expect(run.checks).toEqual({ passed: true, failures: [] });
  });

  it("headlines a ramp's best step and concatenates the timelines", () => {
    const run = loadRecord(context, "enqueue-ramp", LOAD_PROFILES["enqueue-ramp"], {
      steps: [step(1, 100), step(2, 300), step(4, 250)],
      timelines: [timeline(1, 1), timeline(2, 2), timeline(4, 3, 3)],
      drained: false,
      sampleErrors: [],
    });

    expect(run.metrics.messagesPerSecond).toBe(300);
    expect(run.steps).toHaveLength(3);
    expect(run.timeline.series.opsPerSecond).toEqual([1, 2, 3, 3]);
    expect(run.timeline.series.concurrency).toEqual([1, 2, 4, 4]);
    expect(run.scenario.params.queues).toBeNull();
  });

  it("fails a load run's checks on errors or a drained queue", () => {
    const run = loadRecord(context, "dequeue-ack", LOAD_PROFILES["dequeue-ack"], {
      steps: [step(8, 100, 2)], timelines: [timeline(8, 1)], drained: true, sampleErrors: ["TimeoutError: boom"],
    });
    expect(run.checks).toEqual({
      passed: false,
      failures: ["2 errors (first: TimeoutError: boom)", "a queue drained before the window ended: raise seedPerQueue"],
    });
  });

  it("moves a session drain's busy-slots timeline out of the metrics", () => {
    const scenario = SESSION_DRAIN_PROFILES["steady-drain"];
    const result = { ...computeSessionDrain(scenario, [], [{ sessionId: "s0", seq: 0, startMs: 0, endMs: 1500 }], 2000, 1), missing: 0 };

    const run = sessionDrainRecord(context, "steady-drain", scenario, result);

    expect([run.scenario.id, run.scenario.name, run.scenario.key]).toEqual(["P-04", "session-drain", "200x20@100ms/20slots+idle1s"]);
    expect(run.scenario.params.sessionIdleTimeoutSeconds).toBe(1);
    expect(run.metrics.busySlotsTimeline).toBeUndefined();
    expect(run.metrics.timelineBucketMs).toBeUndefined();
    expect(run.timeline).toEqual({ bucketMs: 1000, series: { busySlots: [1, 0.5] } });
    expect(run.checks.passed).toBe(true);
  });

  it("records a queue drain and moves its timelines out of the metrics", () => {
    const run = queueDrainRecord(context, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queueResult());

    expect([run.scenario.id, run.scenario.name, run.scenario.key]).toEqual(["P-05", "queue-drain", "queue:4000@10ms/active100"]);
    expect(run.scenario.params.messages).toBe(4000);
    expect(run.metrics.deliveryLatencyMs.p95).toBe(4000);
    expect(run.metrics.messagesPerSecondTimeline).toBeUndefined();
    expect(run.timeline.series).toEqual({ messagesPerSecond: [700, 900], busyHandlers: [95.5, 99] });
  });

  it("fails a queue drain on missing messages, or out of order only under strict order", () => {
    expect(queueDrainRecord(context, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queueResult(3)).checks.failures).toEqual(["3 messages missing"]);
    expect(queueDrainRecord(context, "queue-strict-order", QUEUE_DRAIN_PROFILES["queue-strict-order"], queueResult(0, 2)).checks.failures).toEqual(["2 order violations"]);
    expect(queueDrainRecord(context, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queueResult(0, 2)).checks.passed).toBe(true);
  });

  it("saves the run in full and history without the timeline", () => {
    const root = mkdtempSync(join(tmpdir(), "perf-results-"));
    const path = save(root, queueDrainRecord(context, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queueResult()));

    expect(path).toBe(join(root, "sdk-typescript", "runs", "20261005T100000Z_typescript_ci_queue-drain.json"));
    expect(JSON.parse(readFileSync(path, "utf8")).timeline.bucketMs).toBe(1000);
    const history = readFileSync(join(root, "sdk-typescript", "history.jsonl"), "utf8").trim().split("\n").map((l) => JSON.parse(l));
    expect(history).toHaveLength(1);
    expect(history[0].timeline).toBeUndefined();
  });
});
