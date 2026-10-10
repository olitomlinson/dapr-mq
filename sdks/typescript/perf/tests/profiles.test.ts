import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { parse } from "../options.js";
import { LOAD_PROFILES, PROFILES, QUEUE_DRAIN_PROFILES, SESSION_DRAIN_PROFILES, SUITES, QueueDrainParams, SessionDrainParams, scaleOf } from "../profiles.js";

const shared = JSON.parse(readFileSync(new URL("../../../testing/perf/profiles.json", import.meta.url), "utf8")) as {
  profiles: Record<string, { id: string; name: string; scale: string; key: string }>;
  suites: Record<string, string[]>;
};

function scenarioOf(profile: string): [string, string, string] {
  if (profile in LOAD_PROFILES) {
    const load = LOAD_PROFILES[profile];
    return [load.id, load.scenario, load.key];
  }
  if (profile in QUEUE_DRAIN_PROFILES) {
    return ["P-05", "queue-drain", QUEUE_DRAIN_PROFILES[profile].key];
  }
  return ["P-04", "session-drain", SESSION_DRAIN_PROFILES[profile].key];
}

describe("profiles", () => {
  it.each(Object.keys(shared.profiles))("%s matches sdks/testing/perf/profiles.json", (profile) => {
    const expected = shared.profiles[profile];
    expect(scenarioOf(profile)).toEqual([expected.id, expected.name, expected.key]);
    expect(scaleOf(profile)).toBe(expected.scale);
  });

  it("has exactly the shared profiles and suites", () => {
    expect([...PROFILES].sort()).toEqual(Object.keys(shared.profiles).sort());
    expect(SUITES).toEqual(shared.suites);
  });

  it("rounds a session drain's ideal up to whole slot rounds", () => {
    expect(new SessionDrainParams({ sessions: 1000, messagesPerSession: 100 }).idealSeconds).toBe(5000);
    expect(new SessionDrainParams({ sessions: 21, messagesPerSession: 10, settleMs: 500 }).idealSeconds).toBe(10);
  });

  it("bounds a concurrent session drain's ideal by the slower of consuming and publishing", () => {
    const base = { sessions: 4, messagesPerSession: 10, maxConcurrentSessions: 4, publishIntervalMs: 2000, publishJitterMs: 500 };
    expect(new SessionDrainParams({ ...base, publishMode: "concurrent" }).idealSeconds).toBeCloseTo(23.5);
    expect(new SessionDrainParams(base).idealSeconds).toBe(10);
  });

  it.each([
    [0, 100, false, 100],
    [10, 100, false, 10],
    [500, 100, false, 100],
    [10, 100, true, 1],
  ])("bounds queue handlers (handlers %i, active %i, strict %s) at %i", (handlers, active, strict, expected) => {
    expect(new QueueDrainParams(4, 1000, active, handlers, { strictOrder: strict }).concurrency).toBe(expected);
  });

  it("covers a slow tail and a rate-limited publisher in a queue drain's ideal", () => {
    expect(new QueueDrainParams(20, 100, 100, 0, { tailEvery: 10, tailMs: 5000 }).idealSeconds).toBeCloseTo(5);
    expect(new QueueDrainParams(50, 100, 100, 0, { publishIntervalMs: 200 }).idealSeconds).toBeCloseTo(9.9);
    expect(new QueueDrainParams(4, 0, 100, 0).idealSeconds).toBeNull();
  });
});

describe("options", () => {
  it("runs a suite's profiles, with three replicas by default for extreme", () => {
    const pr = parse(["--suite", "pr", "--env-label", "ci-x"]);
    expect(pr.profiles).toEqual(SUITES.pr);
    expect(pr.apiReplicas).toBe(1);
    expect(pr.envLabel).toBe("ci-x");

    expect(parse(["--suite", "extreme"]).apiReplicas).toBe(3);
    expect(parse(["--", "--suite", "extreme", "--api-replicas", "5"]).apiReplicas).toBe(5);
  });

  it("rejects an existing server with only one endpoint, or with replicas", () => {
    expect(() => parse(["--http", "http://localhost:8002"])).toThrow();
    expect(() => parse(["--http", "http://h", "--grpc", "g:1", "--api-replicas", "2"])).toThrow();
    expect(() => parse(["--profile", "nope"])).toThrow();
    expect(parse(["--http", "http://localhost:8002", "--grpc", "http://localhost:8102"]).grpc).toBe("localhost:8102");
  });
});
