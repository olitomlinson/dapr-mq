/** Ports of LoadMetricsTests, SessionDrainMetricsTests and QueueDrainMetricsTests (.NET). */
import { describe, expect, it } from "vitest";
import {
  computeQueueDrain,
  computeSessionDrain,
  computeStep,
  distribution,
  type HandlerRecord,
  type QueueHandlerRecord,
  type StreamRecord,
} from "../metrics.js";
import { QueueDrainParams, SessionDrainParams } from "../profiles.js";

/* eslint-disable @typescript-eslint/no-explicit-any */
const op = (endMs: number, latencyMs: number, messages: number, error: boolean) => ({ endMs, latencyMs, messages, error });

describe("load metrics", () => {
  it("counts only operations finishing inside the recorded window", () => {
    const ops = [op(500, 99, 1, false), op(1000, 10, 1, false), op(1500, 20, 1, false), op(2999, 30, 1, false), op(3000, 99, 1, false)];
    const { step } = computeStep(2, 2, ops, 1000, 2000);
    expect(step.ops).toBe(3);
    expect(step.opsPerSecond).toBeCloseTo(1.5);
    expect(step.latencyMs.p50).toBe(20);
    expect(step.latencyMs.max).toBe(30);
    expect(step.durationSeconds).toBe(2);
  });

  it("counts batch items as messages, and errors but not their latency", () => {
    const { step } = computeStep(1, 1, [op(100, 10, 100, false), op(200, 5000, 0, true), op(300, 20, 100, false)], 0, 1000);
    expect([step.ops, step.messages, step.errors]).toEqual([3, 200, 1]);
    expect(step.messagesPerSecond).toBeCloseTo(200);
    expect(step.latencyMs.count).toBe(2);
    expect(step.latencyMs.max).toBe(20);
  });

  it("buckets the timeline by second of completion, with null percentiles for empty seconds", () => {
    const { timeline } = computeStep(4, 4, [op(1100, 10, 1, false), op(1900, 30, 1, false), op(3500, 40, 2, false), op(3600, 50, 0, true)], 1000, 3000);
    expect(timeline.opsPerSecond).toEqual([2, 0, 2]);
    expect(timeline.messagesPerSecond).toEqual([2, 0, 2]);
    expect(timeline.latencyP95Ms).toEqual([30, null, 40]);
    expect(timeline.errors).toEqual([0, 0, 1]);
    expect(timeline.concurrency).toEqual([4, 4, 4]);
  });

  it("uses nearest-rank percentiles", () => {
    const d = distribution(Array.from({ length: 100 }, (_, i) => i + 1));
    expect([d.p50, d.p95, d.p99, d.max]).toEqual([50, 95, 99, 100]);
  });
});

const scenario = (sessions: number, messages: number, slots: number, settleMs = 1000) =>
  new SessionDrainParams({ sessions, messagesPerSession: messages, settleMs, maxConcurrentSessions: slots });
const stream = (sessionId: string | null, openMs: number, firstDeliveryMs: number | null, endMs: number, endReason = "completed"): StreamRecord => ({
  openMs, firstDeliveryMs, endMs, sessionId, endReason,
});
const handler = (sessionId: string, seq: number, startMs: number, endMs: number, deliveryLatencyMs?: number): HandlerRecord => ({
  sessionId, seq, startMs, endMs, deliveryLatencyMs,
});

describe("session drain metrics", () => {
  it("fully utilises perfectly packed slots", () => {
    const r: any = computeSessionDrain(
      scenario(2, 2, 2),
      [stream("a", 0, 0, 2000), stream("b", 0, 0, 2000)],
      [handler("a", 0, 0, 1000), handler("a", 1, 1000, 2000), handler("b", 0, 0, 1000), handler("b", 1, 1000, 2000)],
      2000,
      0.5,
    );
    expect(r.wallClockSeconds).toBeCloseTo(2);
    expect(r.idealSeconds).toBeCloseTo(2);
    expect(r.efficiency).toBeCloseTo(1);
    expect(r.seedSeconds).toBe(0.5);
    expect(r.peak.utilization).toBeCloseTo(1);
    expect(r.peak.idleSlotSeconds).toBeCloseTo(0);
    expect(r.messagesHandled).toBe(4);
  });

  it("attributes idle slot time to claim, drain and between streams", () => {
    const r: any = computeSessionDrain(
      scenario(2, 1, 1),
      [stream("a", 0, 100, 4100), stream("b", 4300, 4400, 8400)],
      [handler("a", 0, 100, 1100), handler("b", 0, 4400, 5400)],
      5400,
      0,
    );
    expect(r.peak.startSeconds).toBeCloseTo(0.1);
    expect(r.peak.endSeconds).toBeCloseTo(4.4);
    expect(r.peak.capacitySlotSeconds).toBeCloseTo(4.3);
    expect(r.peak.handlingSeconds).toBeCloseTo(1);
    expect(r.peak.claimSeconds).toBeCloseTo(0.1);
    expect(r.peak.drainWaitSeconds).toBeCloseTo(3);
    expect(r.peak.betweenStreamsSeconds).toBeCloseTo(0.2);
    expect(r.peak.inSessionWaitSeconds).toBeCloseTo(0);
    expect(r.peak.idleSlotSeconds).toBeCloseTo(3.3);
    expect(r.peak.utilization).toBeCloseTo(1 / 4.3);
    expect(r.overall.capacitySlotSeconds).toBeCloseTo(5.4);
    expect(r.overall.handlingSeconds).toBeCloseTo(2);
    expect(r.overall.claimSeconds).toBeCloseTo(0.2);
    expect(r.overall.drainWaitSeconds).toBeCloseTo(3);
    expect(r.overall.betweenStreamsSeconds).toBeCloseTo(0.2);
    expect(r.tailSeconds).toBeCloseTo(1);
    expect(r.timeToFirstMessageSeconds).toBeCloseTo(0.1);
    expect(r.claimLatencyMs.count).toBe(2);
    expect(r.claimLatencyMs.p50).toBeCloseTo(100);
    expect(r.drainWaitMs.max).toBeCloseTo(3000);
  });

  it("counts gaps between handlers in one stream as in-session wait", () => {
    const r: any = computeSessionDrain(scenario(1, 2, 1), [stream("a", 0, 0, 2500)], [handler("a", 0, 0, 1000), handler("a", 1, 1500, 2500)], 2500, 0);
    expect(r.overall.inSessionWaitSeconds).toBeCloseTo(0.5);
    expect(r.interMessageGapMs.count).toBe(1);
    expect(r.interMessageGapMs.p50).toBeCloseTo(500);
  });

  it("counts a stream that never delivered as a failed claim, and re-claims", () => {
    const r: any = computeSessionDrain(
      scenario(1, 2, 1),
      [stream(null, 0, null, 300, "NoSessionsAvailableError"), stream("a", 300, 400, 1400), stream("a", 1400, 1500, 2500)],
      [handler("a", 0, 400, 1400), handler("a", 1, 1500, 2500)],
      2500,
      0,
    );
    expect(r.failedClaims).toBe(1);
    expect(r.streams).toBe(3);
    expect(r.overall.claimSeconds).toBeCloseTo(0.5);
    expect(r.sessionsClaimedMoreThanOnce).toBe(1);
  });

  it("detects ordering, duplicates and missing messages", () => {
    const handlers = [handler("a", 0, 0, 10), handler("a", 2, 10, 20), handler("a", 1, 20, 30), handler("b", 0, 0, 10), handler("b", 0, 10, 20)];
    const r: any = computeSessionDrain(scenario(2, 3, 2), [], handlers, 30, 0);
    expect([r.fifoViolations, r.duplicates, r.missing, r.messagesHandled]).toEqual([1, 1, 2, 5]);
  });

  it("distributes delivery latency over the handlers that recorded it", () => {
    const r: any = computeSessionDrain(scenario(1, 3, 1), [stream("a", 0, 0, 30)], [handler("a", 0, 0, 10, 100), handler("a", 1, 10, 20, 300), handler("a", 2, 20, 30)], 30, 0);
    expect([r.deliveryLatencyMs.count, r.deliveryLatencyMs.p50, r.deliveryLatencyMs.max]).toEqual([2, 100, 300]);
  });

  it("reports average busy slots per second", () => {
    const r: any = computeSessionDrain(scenario(1, 1, 1), [stream("a", 0, 500, 2500)], [handler("a", 0, 500, 2500)], 2500, 0);
    expect(r.timelineBucketMs).toBe(1000);
    expect(r.busySlotsTimeline).toEqual([0.5, 1, 0.5]);
  });
});

const queue = (messages = 4, settleMs = 1000, active = 100, handlers = 2, strict = false) =>
  new QueueDrainParams(messages, settleMs, active, handlers, { strictOrder: strict });
const qh = (seq: number, startMs: number, endMs: number, deliveryLatencyMs = 0): QueueHandlerRecord => ({ seq, startMs, endMs, deliveryLatencyMs });

describe("queue drain metrics", () => {
  it("takes throughput, wall clock and first message from the handler records", () => {
    const r: any = computeQueueDrain(queue(), [qh(0, 100, 1100, 50), qh(1, 120, 1120, 70), qh(2, 1100, 2100, 1050), qh(3, 1120, 2000, 1070)], 2100, 0.5);
    expect(r.wallClockSeconds).toBeCloseTo(2.1);
    expect(r.messagesPerSecond).toBeCloseTo(4 / 2.1);
    expect(r.timeToFirstMessageSeconds).toBeCloseTo(0.1);
    expect(r.seedSeconds).toBe(0.5);
    expect([r.deliveryLatencyMs.count, r.deliveryLatencyMs.max]).toEqual([4, 1070]);
    expect([r.messagesHandled, r.missing, r.duplicates]).toEqual([4, 0, 0]);
  });

  it("gives efficiency as ideal over wall clock, unset for an instant handler", () => {
    const r: any = computeQueueDrain(queue(), [qh(0, 0, 1000), qh(1, 0, 1000), qh(2, 1000, 2000), qh(3, 1000, 2500)], 2500, 0);
    expect(r.idealSeconds).toBeCloseTo(2);
    expect(r.efficiency).toBeCloseTo(0.8);

    const instant: any = computeQueueDrain(queue(4, 0), [qh(0, 0, 1)], 1, 0);
    expect(instant.idealSeconds).toBeNull();
    expect(instant.efficiency).toBeNull();
  });

  it("finds the most handlers running at once", () => {
    const r: any = computeQueueDrain(queue(), [qh(0, 0, 1000), qh(1, 100, 900), qh(2, 200, 300), qh(3, 1000, 1500)], 1500, 0);
    expect(r.peakConcurrentHandlers).toBe(3);
  });

  it("counts missing, duplicate and out-of-order messages", () => {
    const r: any = computeQueueDrain(queue(4), [qh(0, 0, 10), qh(0, 20, 30), qh(2, 40, 50)], 50, 0);
    expect([r.messagesHandled, r.duplicates, r.missing]).toEqual([2, 1, 2]);

    const ordered: any = computeQueueDrain(queue(4, 1000, 100, 2, true), [qh(0, 0, 10), qh(2, 10, 20), qh(1, 20, 30), qh(3, 30, 40)], 40, 0);
    expect(ordered.orderViolations).toBe(1);
  });

  it("counts messages finished and average busy handlers per second", () => {
    const r: any = computeQueueDrain(queue(3), [qh(0, 0, 1000), qh(1, 0, 500), qh(2, 1000, 1500)], 1500, 0);
    expect(r.messagesPerSecondTimeline).toEqual([1, 2]);
    expect(r.busyHandlersTimeline).toEqual([1.5, 0.5]);
  });
});
