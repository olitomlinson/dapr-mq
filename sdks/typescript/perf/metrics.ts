/**
 * Turns raw operation, stream and handler records into the numbers of
 * sdks/testing/PERFORMANCE_TESTS.md. Pure, so it is unit tested; a port of LoadMetrics.cs,
 * SessionDrainMetrics.cs and QueueDrainMetrics.cs in sdks/dotnet/perf/DaprMQ.Client.Perf. Every
 * object is already in the result schema's shape.
 */
import type { QueueDrainParams, SessionDrainParams } from "./profiles.js";

export const BUCKET_MS = 1000;

export interface Distribution {
  count: number;
  mean: number;
  p50: number;
  p95: number;
  p99: number;
  max: number;
}

/** count, mean and nearest-rank percentiles. */
export function distribution(values: Iterable<number>): Distribution {
  const sorted = [...values].sort((a, b) => a - b);
  if (sorted.length === 0) {
    return { count: 0, mean: 0, p50: 0, p95: 0, p99: 0, max: 0 };
  }
  const percentile = (p: number): number => sorted[Math.min(Math.max(Math.ceil(p * sorted.length) - 1, 0), sorted.length - 1)];
  return {
    count: sorted.length,
    mean: sorted.reduce((a, b) => a + b, 0) / sorted.length,
    p50: percentile(0.5),
    p95: percentile(0.95),
    p99: percentile(0.99),
    max: sorted[sorted.length - 1],
  };
}

const sum = (values: number[]): number => values.reduce((a, b) => a + b, 0);
const round3 = (value: number): number => Math.round(value * 1000) / 1000;
// Not Math.min(...values): spreading 100k handler records overflows the stack.
const minOf = (values: number[]): number => values.reduce((a, b) => (b < a ? b : a), Infinity);
const maxOf = (values: number[]): number => values.reduce((a, b) => (b > a ? b : a), -Infinity);

// --- P-01..P-03 closed-loop load ---

/** One operation; times are ms from the start of the step (warmup included). */
export interface OpRecord {
  endMs: number;
  latencyMs: number;
  messages: number;
  error: boolean;
}

export interface LoadStep {
  concurrency: number;
  queues: number;
  durationSeconds: number;
  ops: number;
  messages: number;
  errors: number;
  opsPerSecond: number;
  messagesPerSecond: number;
  latencyMs: Distribution;
}

export interface StepTimeline {
  opsPerSecond: number[];
  messagesPerSecond: number[];
  latencyP50Ms: (number | null)[];
  latencyP95Ms: (number | null)[];
  latencyP99Ms: (number | null)[];
  errors: number[];
  concurrency: number[];
}

/** Only operations finishing inside [warmup, warmup + duration) count; errors are counted but left out of latency. */
export function computeStep(concurrency: number, queues: number, ops: OpRecord[], warmupMs: number, durationMs: number): { step: LoadStep; timeline: StepTimeline } {
  const window = ops.filter((o) => o.endMs >= warmupMs && o.endMs < warmupMs + durationMs);
  const seconds = durationMs / 1000;
  const messages = sum(window.map((o) => o.messages));

  const step: LoadStep = {
    concurrency,
    queues,
    durationSeconds: seconds,
    ops: window.length,
    messages,
    errors: window.filter((o) => o.error).length,
    opsPerSecond: window.length / seconds,
    messagesPerSecond: messages / seconds,
    latencyMs: distribution(window.filter((o) => !o.error).map((o) => o.latencyMs)),
  };

  const buckets: OpRecord[][] = Array.from({ length: Math.ceil(durationMs / BUCKET_MS) }, () => []);
  for (const op of window) {
    buckets[Math.floor((op.endMs - warmupMs) / BUCKET_MS)].push(op);
  }
  const percentile = (bucket: OpRecord[], name: "p50" | "p95" | "p99"): number | null => {
    const ok = bucket.filter((o) => !o.error).map((o) => o.latencyMs);
    return ok.length === 0 ? null : round3(distribution(ok)[name]);
  };

  const timeline: StepTimeline = {
    opsPerSecond: buckets.map((b) => b.length),
    messagesPerSecond: buckets.map((b) => sum(b.map((o) => o.messages))),
    latencyP50Ms: buckets.map((b) => percentile(b, "p50")),
    latencyP95Ms: buckets.map((b) => percentile(b, "p95")),
    latencyP99Ms: buckets.map((b) => percentile(b, "p99")),
    errors: buckets.map((b) => b.filter((o) => o.error).length),
    concurrency: buckets.map(() => concurrency),
  };
  return { step, timeline };
}

// --- P-04 session drain ---

/**
 * One consumeSession stream as a consumer slot saw it, ms from the start of the consume phase.
 * firstDeliveryMs/sessionId are null when the claim itself failed.
 */
export interface StreamRecord {
  openMs: number;
  firstDeliveryMs: number | null;
  endMs: number;
  sessionId: string | null;
  endReason: string;
}

/** One handler invocation, ms from the start of the consume phase; delivery latency is publish -> handler start. */
export interface HandlerRecord {
  sessionId: string;
  seq: number;
  startMs: number;
  endMs: number;
  deliveryLatencyMs?: number;
}

export interface SlotTimeBreakdown {
  startSeconds: number;
  endSeconds: number;
  capacitySlotSeconds: number;
  handlingSeconds: number;
  claimSeconds: number;
  inSessionWaitSeconds: number;
  drainWaitSeconds: number;
  betweenStreamsSeconds: number;
  idleSlotSeconds: number;
  utilization: number;
}

type Delivered = { stream: StreamRecord; handlers: HandlerRecord[] };

/** Where every slot-second in [from, to) went: handling + claim + in-session wait + drain wait + between streams = capacity. */
function breakdown(slots: number, streams: StreamRecord[], delivered: Delivered[], from: number, to: number): SlotTimeBreakdown {
  const clip = (a: number, b: number): number => Math.max(0, Math.min(b, to) - Math.max(a, from));
  let handling = 0;
  let claim = 0;
  let inSession = 0;
  let drain = 0;

  for (const s of streams) {
    if (s.firstDeliveryMs === null) {
      claim += clip(s.openMs, s.endMs);
    }
  }
  for (const { stream: s, handlers: hs } of delivered) {
    const first = s.firstDeliveryMs!;
    const lastHandlerEnd = hs.length > 0 ? Math.min(hs[hs.length - 1].endMs, s.endMs) : first;
    const streamHandling = sum(hs.map((h) => clip(h.startMs, h.endMs)));
    claim += clip(s.openMs, first);
    handling += streamHandling;
    inSession += Math.max(0, clip(first, lastHandlerEnd) - streamHandling);
    drain += clip(lastHandlerEnd, s.endMs);
  }

  const capacity = slots * Math.max(0, to - from);
  const inStreams = sum(streams.map((s) => clip(s.openMs, s.endMs)));
  const capacitySeconds = capacity / 1000;
  const handlingSeconds = handling / 1000;
  return {
    startSeconds: from / 1000,
    endSeconds: to / 1000,
    capacitySlotSeconds: capacitySeconds,
    handlingSeconds,
    claimSeconds: claim / 1000,
    inSessionWaitSeconds: inSession / 1000,
    drainWaitSeconds: drain / 1000,
    betweenStreamsSeconds: Math.max(0, capacity - inStreams) / 1000,
    idleSlotSeconds: capacitySeconds - handlingSeconds,
    utilization: capacitySeconds > 0 ? handlingSeconds / capacitySeconds : 0,
  };
}

/** Average busy handlers per bucket. */
function busyTimeline(intervals: { startMs: number; endMs: number }[], buckets: number): number[] {
  const busy = new Array<number>(buckets).fill(0);
  for (const { startMs, endMs } of intervals) {
    for (let b = Math.floor(startMs / BUCKET_MS); b < buckets && b * BUCKET_MS < endMs; b++) {
      busy[b] += Math.max(0, Math.min(endMs, (b + 1) * BUCKET_MS) - Math.max(startMs, b * BUCKET_MS));
    }
  }
  return busy.map((ms) => round3(ms / BUCKET_MS));
}

/** The P-04 metrics, plus timelineBucketMs and busySlotsTimeline (moved into the timeline when recorded). */
export function computeSessionDrain(
  scenario: SessionDrainParams,
  streams: StreamRecord[],
  handlers: HandlerRecord[],
  completedMs: number,
  seedSeconds: number,
): Record<string, unknown> {
  const ideal = scenario.idealSeconds;
  const wall = completedMs / 1000;

  const bySession = new Map<string, HandlerRecord[]>();
  for (const h of handlers) {
    const hs = bySession.get(h.sessionId);
    if (hs) {
      hs.push(h);
    } else {
      bySession.set(h.sessionId, [h]);
    }
  }
  for (const hs of bySession.values()) {
    hs.sort((a, b) => a.startMs - b.startMs);
  }

  const delivered: Delivered[] = streams
    .filter((s) => s.firstDeliveryMs !== null && s.sessionId !== null)
    .map((s) => ({
      stream: s,
      handlers: (bySession.get(s.sessionId!) ?? []).filter((h) => h.startMs >= s.firstDeliveryMs! && h.startMs <= s.endMs),
    }));
  const firstHandlerStart = handlers.length > 0 ? minOf(handlers.map((h) => h.startMs)) : 0;

  // Peak = first delivery until the last session gets its first delivery; after that the slots
  // going idle is the unavoidable tail, not a claim/drain cost.
  const lastSessionStart = bySession.size > 0 ? maxOf([...bySession.values()].map((hs) => hs[0].startMs)) : 0;
  const peakEnd = lastSessionStart > firstHandlerStart ? lastSessionStart : completedMs;

  const gaps = delivered.flatMap(({ handlers: hs }) => hs.slice(1).map((b, i) => b.startMs - hs[i].endMs));
  const unique = new Set(handlers.map((h) => `${h.sessionId}\u0000${h.seq}`)).size;
  const claims = new Map<string, number>();
  for (const { stream } of delivered) {
    claims.set(stream.sessionId!, (claims.get(stream.sessionId!) ?? 0) + 1);
  }

  return {
    seedSeconds,
    wallClockSeconds: wall,
    idealSeconds: ideal,
    efficiency: wall > 0 ? ideal / wall : 0,
    timeToFirstMessageSeconds: firstHandlerStart / 1000,
    tailSeconds: (completedMs - peakEnd) / 1000,
    peak: breakdown(scenario.maxConcurrentSessions, streams, delivered, firstHandlerStart, peakEnd),
    overall: breakdown(scenario.maxConcurrentSessions, streams, delivered, 0, completedMs),
    claimLatencyMs: distribution(delivered.map(({ stream }) => stream.firstDeliveryMs! - stream.openMs)),
    drainWaitMs: distribution(delivered.filter(({ handlers: hs }) => hs.length > 0).map(({ stream, handlers: hs }) => stream.endMs - hs[hs.length - 1].endMs)),
    interMessageGapMs: distribution(gaps),
    deliveryLatencyMs: distribution(handlers.flatMap((h) => (h.deliveryLatencyMs === undefined ? [] : [h.deliveryLatencyMs]))),
    streams: streams.length,
    failedClaims: streams.filter((s) => s.firstDeliveryMs === null).length,
    sessionsClaimedMoreThanOnce: [...claims.values()].filter((n) => n > 1).length,
    messagesHandled: handlers.length,
    duplicates: handlers.length - unique,
    missing: Math.max(0, scenario.sessions * scenario.messagesPerSession - unique),
    fifoViolations: sum([...bySession.values()].map((hs) => hs.slice(1).filter((b, i) => b.seq < hs[i].seq).length)),
    timelineBucketMs: BUCKET_MS,
    busySlotsTimeline: busyTimeline(handlers, Math.ceil(completedMs / BUCKET_MS)),
  };
}

// --- P-05 queue drain ---

export interface QueueHandlerRecord {
  seq: number;
  startMs: number;
  endMs: number;
  deliveryLatencyMs: number;
}

function peakOverlap(handled: QueueHandlerRecord[]): number {
  // Ends sort before starts at the same instant: back-to-back handlers don't overlap.
  const events = handled.flatMap((h) => [[h.startMs, 1] as const, [h.endMs, -1] as const]).sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  let running = 0;
  let peak = 0;
  for (const [, delta] of events) {
    running += delta;
    peak = Math.max(peak, running);
  }
  return peak;
}

/** The P-05 metrics, plus messagesPerSecondTimeline and busyHandlersTimeline (moved into the timeline when recorded). */
export function computeQueueDrain(p: QueueDrainParams, handled: QueueHandlerRecord[], completedMs: number, seedSeconds: number): Record<string, unknown> {
  const wall = completedMs / 1000;
  const unique = new Set(handled.map((h) => h.seq)).size;
  const ideal = p.idealSeconds;

  // Handler starts in time order; a message started after a later one is out of queue order.
  const starts = [...handled].sort((a, b) => a.startMs - b.startMs).map((h) => h.seq);
  const orderViolations = starts.slice(1).filter((seq, i) => seq < starts[i]).length;

  const buckets = Math.max(1, Math.ceil(completedMs / BUCKET_MS));
  const finished = new Array<number>(buckets).fill(0);
  for (const h of handled) {
    finished[Math.min(Math.floor(h.endMs / BUCKET_MS), buckets - 1)]++;
  }

  return {
    seedSeconds,
    wallClockSeconds: wall,
    messagesPerSecond: wall > 0 ? unique / wall : 0,
    idealSeconds: ideal,
    efficiency: ideal !== null && wall > 0 ? ideal / wall : null,
    timeToFirstMessageSeconds: handled.length > 0 ? minOf(handled.map((h) => h.startMs)) / 1000 : 0,
    peakConcurrentHandlers: peakOverlap(handled),
    deliveryLatencyMs: distribution(handled.map((h) => h.deliveryLatencyMs)),
    messagesHandled: unique,
    duplicates: handled.length - unique,
    missing: p.messages - unique,
    orderViolations,
    messagesPerSecondTimeline: finished,
    busyHandlersTimeline: busyTimeline(handled, buckets),
  };
}
