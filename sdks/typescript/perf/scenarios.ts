/**
 * The scenarios, driving the public SDK surface (DaprMQClient, SessionQueueConsumer, QueueConsumer).
 * Ports of LoadScenario.cs, SessionDrainScenario.cs and QueueDrainScenario.cs in
 * sdks/dotnet/perf/DaprMQ.Client.Perf.
 */
import { randomUUID } from "node:crypto";
import { performance } from "node:perf_hooks";
import {
  QueueConsumer,
  SessionQueueConsumer,
  type DaprMQClient,
  type EnqueueItem,
  type SessionCapableClient,
  type SessionDelivery,
} from "../src/index.js";
import {
  computeQueueDrain,
  computeSessionDrain,
  computeStep,
  type HandlerRecord,
  type LoadStep,
  type OpRecord,
  type QueueHandlerRecord,
  type StepTimeline,
  type StreamRecord,
} from "./metrics.js";
import type { LoadParams, QueueDrainParams, SessionDrainParams } from "./profiles.js";
import type { LoadResult } from "./records.js";

const SEED_BATCH = 100;
const SEED_PARALLELISM = 16;
const MAX_SAMPLE_ERRORS = 5;

const sleep = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));
const shortId = (): string => randomUUID().replace(/-/g, "");

/** Milliseconds on a monotonic clock since start(). */
class Clock {
  private startedAt: number | null = null;

  start(): void {
    this.startedAt = performance.now();
  }

  ms = (): number => (this.startedAt === null ? 0 : performance.now() - this.startedAt);
}

/**
 * Delay before each message: the first after a random 0..jitter start offset (so publishers don't
 * run in lockstep), each later one after interval + random 0..jitter.
 */
export function publishDelays(messages: number, intervalMs: number, jitterMs: number, random: () => number = Math.random): number[] {
  return Array.from({ length: messages }, (_, i) => (i === 0 ? 0 : intervalMs) + (jitterMs > 0 ? Math.floor(random() * (jitterMs + 1)) : 0));
}

async function runLimited(limit: number, jobs: (() => Promise<void>)[]): Promise<void> {
  let next = 0;
  await Promise.all(
    Array.from({ length: Math.min(limit, jobs.length) }, async () => {
      while (next < jobs.length) {
        await jobs[next++]();
      }
    }),
  );
}

function progress(describe: () => string): () => void {
  const timer = setInterval(() => console.log(`  ${describe()}`), 30_000);
  return () => clearInterval(timer);
}

function withTimeout(done: Promise<void>, timeoutMs: number, describe: () => string): Promise<void> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(describe())), timeoutMs);
  });
  return Promise.race([done, timeout]).finally(() => clearTimeout(timer));
}

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let resolve!: () => void;
  const promise = new Promise<void>((r) => (resolve = r));
  return { promise, resolve };
}

// --- P-01..P-03 closed-loop load ---

/**
 * Each step runs `concurrency` workers back to back on one shared client for warmup + duration,
 * worker w on queue w % queues. A ramp is a sequence of steps, each on fresh queues.
 */
export class LoadScenario {
  constructor(
    private readonly client: DaprMQClient,
    private readonly load: LoadParams,
  ) {}

  async run(): Promise<LoadResult> {
    const load = this.load;
    const steps: LoadStep[] = [];
    const timelines: StepTimeline[] = [];
    const errors: string[] = [];
    let drained = false;
    const pad = "x".repeat(load.payloadBytes);

    for (const concurrency of load.concurrency) {
      const queues = load.queuesAt(concurrency);
      const runId = shortId().slice(0, 12);
      // No "-session-" in the base id: QueueActor treats that marker as a per-session actor.
      const queueIds = Array.from({ length: queues }, (_, q) => `perf-${load.scenario}-${runId}-q${q}`);

      if (load.scenario === "dequeue-ack") {
        const seedStart = performance.now();
        await this.seed(queueIds, pad);
        console.log(`  seeded ${queues} x ${load.seedPerQueue} in ${((performance.now() - seedStart) / 1000).toFixed(1)}s`);
      }

      const warmupMs = load.warmupSeconds * 1000;
      const durationMs = load.durationSeconds * 1000;
      const clock = new Clock();
      clock.start();
      const perWorker: OpRecord[][] = Array.from({ length: concurrency }, () => []);
      let stepDrained = false;

      await Promise.all(
        perWorker.map(async (records, w) => {
          const queueId = queueIds[w % queues];
          let seq = 0;
          while (clock.ms() < warmupMs + durationMs) {
            const start = clock.ms();
            let messages: number;
            let error = false;
            try {
              messages = await this.operation(queueId, w, seq, pad);
              seq += Math.max(messages, 1);
            } catch (err) {
              messages = 0;
              error = true;
              if (errors.length < MAX_SAMPLE_ERRORS) {
                errors.push(`${(err as Error).name}: ${(err as Error).message}`);
              }
            }

            if (messages < 0) {
              stepDrained = true;
              break;
            }
            const end = clock.ms();
            records.push({ endMs: end, latencyMs: end - start, messages, error });
          }
        }),
      );

      const { step, timeline } = computeStep(concurrency, queues, perWorker.flat(), warmupMs, durationMs);
      steps.push(step);
      timelines.push(timeline);
      drained ||= stepDrained;
      const l = step.latencyMs;
      console.log(
        `  c=${String(concurrency).padEnd(4)} q=${String(queues).padEnd(4)} ${step.messagesPerSecond.toFixed(0).padStart(10)} msg/s  ` +
          `p50 ${l.p50.toFixed(2).padStart(7)}  p95 ${l.p95.toFixed(2).padStart(7)}  p99 ${l.p99.toFixed(2).padStart(7)} ms  ` +
          `errors ${step.errors}${stepDrained ? "  DRAINED" : ""}`,
      );
    }

    return { steps, timelines, drained, sampleErrors: errors };
  }

  /** One operation: the messages it moved, or -1 when a dequeue found the queue empty. */
  private async operation(queueId: string, worker: number, seq: number, pad: string): Promise<number> {
    const load = this.load;
    if (load.scenario === "dequeue-ack") {
      const result = await this.client.dequeueLocked(queueId, { count: load.dequeueCount });
      if (result === null || result.items.length === 0) {
        return -1;
      }
      for (const item of result.items) {
        await this.client.acknowledge(queueId, item.lockId);
      }
      return result.items.length;
    }

    const batch = load.scenario === "enqueue-batch" ? load.batchSize : 1;
    const enqueued = await this.client.enqueue(queueId, LoadScenario.items(worker, seq, batch, pad));
    if (!enqueued.success || enqueued.itemsEnqueued !== batch) {
      throw new Error(`Enqueue returned ${enqueued.itemsEnqueued}/${batch}: ${enqueued.message}`);
    }
    return batch;
  }

  private static items(worker: number, seq: number, count: number, pad: string): EnqueueItem[] {
    const publishedAt = Date.now();
    return Array.from({ length: count }, (_, i) => ({ item: { worker, seq: seq + i, publishedAt, pad } }));
  }

  private async seed(queueIds: string[], pad: string): Promise<void> {
    const perQueue = this.load.seedPerQueue;
    // Batches for one queue go in order (one actor serialises them anyway); queues in parallel.
    await runLimited(
      SEED_PARALLELISM,
      queueIds.map((queueId, q) => async () => {
        for (let start = 0; start < perQueue; start += SEED_BATCH) {
          const count = Math.min(SEED_BATCH, perQueue - start);
          const result = await this.client.enqueue(queueId, LoadScenario.items(q, start, count, pad));
          if (!result.success || result.itemsEnqueued !== count) {
            throw new Error(`Seeding ${queueId} failed: ${result.message}`);
          }
        }
      }),
    );
  }
}

// --- P-04 session drain ---

/**
 * Decorates the client handed to SessionQueueConsumer, timestamping each consumeSession stream
 * (open, first delivery, end): with the handler intervals that accounts for every slot-second.
 */
export class RecordingClient implements SessionCapableClient {
  readonly streams: StreamRecord[] = [];

  constructor(
    private readonly inner: SessionCapableClient,
    private readonly clockMs: () => number,
  ) {}

  async *consumeSession(queueId: string, options: Parameters<SessionCapableClient["consumeSession"]>[1]): AsyncGenerator<SessionDelivery> {
    const openMs = this.clockMs();
    let firstDeliveryMs: number | null = null;
    let sessionId: string | null = null;
    let endReason = "abandoned";
    try {
      try {
        for await (const delivery of this.inner.consumeSession(queueId, options)) {
          if (firstDeliveryMs === null) {
            firstDeliveryMs = this.clockMs();
            sessionId = delivery.sessionId;
          }
          yield delivery;
        }
        endReason = "completed";
      } catch (err) {
        endReason = (err as Error).name;
        throw err;
      }
    } finally {
      this.streams.push({ openMs, firstDeliveryMs, endMs: this.clockMs(), sessionId, endReason });
    }
  }
}

const sessionId = (session: number): string => `s${String(session).padStart(5, "0")}`;

/**
 * Seed sessions x messages, then drain them with one SessionQueueConsumer whose handler takes
 * settleMs per message. Completion is the moment every (session, seq) has been handled once.
 */
export class SessionDrainScenario {
  constructor(
    private readonly client: DaprMQClient,
    private readonly scenario: SessionDrainParams,
  ) {}

  async run(): Promise<Record<string, unknown>> {
    const s = this.scenario;
    const queueId = `perf-drain-${shortId()}`;
    console.log(
      `Queue ${queueId}: ${s.key}, prefetch ${s.prefetchCount}, lease ${s.leaseSeconds}s, ` +
        `idle-timeout ${s.sessionIdleTimeoutSeconds}s, publish ${s.publishMode}`,
    );

    // The consume clock: it starts with the consumer, which in concurrent mode is also when
    // publishing starts - so there the wall clock includes the publish.
    const clock = new Clock();
    const recorder = new RecordingClient(this.client, clock.ms);
    const handlers: HandlerRecord[] = [];
    const seen = new Set<string>();
    const expected = s.sessions * s.messagesPerSession;
    const allHandled = deferred();
    let completedMs = 0;

    const consumer = new SessionQueueConsumer(
      recorder,
      queueId,
      {
        maxConcurrentSessions: s.maxConcurrentSessions,
        prefetchCount: s.prefetchCount,
        leaseSeconds: s.leaseSeconds,
        sessionIdleTimeoutSeconds: s.sessionIdleTimeoutSeconds,
        drainTimeoutMs: 5000,
      },
      async (ctx) => {
        const start = clock.ms();
        const item = ctx.item as { seq: number; publishedAt: number };
        const deliveryLatencyMs = Date.now() - item.publishedAt;
        await sleep(s.settleMs);
        const end = clock.ms();
        handlers.push({ sessionId: ctx.sessionId, seq: item.seq, startMs: start, endMs: end, deliveryLatencyMs });
        const key = `${ctx.sessionId}/${item.seq}`;
        if (!seen.has(key)) {
          seen.add(key);
          if (seen.size === expected) {
            completedMs = end;
            allHandled.resolve();
          }
        }
      },
    );

    const timeoutMs = (s.idealSeconds * 3 + 600) * 1000;
    const wait = (): Promise<void> =>
      withTimeout(allHandled.promise, timeoutMs, () => `Only ${seen.size}/${expected} messages handled after ${timeoutMs / 1000}s.`);
    const stopProgress = progress(() => `t=${(clock.ms() / 1000).toFixed(0)}s  handled ${seen.size}/${expected}  streams ${recorder.streams.length}`);
    let seedSeconds: number;
    try {
      if (s.publishMode === "concurrent") {
        clock.start();
        consumer.start();
        try {
          seedSeconds = await this.seed(queueId);
          await wait();
        } finally {
          await consumer.stop();
        }
      } else {
        seedSeconds = await this.seed(queueId);
        clock.start();
        consumer.start();
        try {
          await wait();
        } finally {
          await consumer.stop();
        }
      }
    } finally {
      stopProgress();
    }

    return computeSessionDrain(s, recorder.streams, handlers, completedMs, seedSeconds);
  }

  private async seed(queueId: string): Promise<number> {
    const s = this.scenario;
    const start = performance.now();
    if (s.rateLimited) {
      // Every session publishes at once, one message at a time on its own schedule.
      await Promise.all(
        Array.from({ length: s.sessions }, async (_, session) => {
          const delays = publishDelays(s.messagesPerSession, s.publishIntervalMs, s.publishJitterMs);
          for (let seq = 0; seq < delays.length; seq++) {
            await sleep(delays[seq]);
            await this.enqueue(queueId, sessionId(session), [seq]);
          }
        }),
      );
    } else {
      const seqs = Array.from({ length: s.messagesPerSession }, (_, i) => i);
      await runLimited(
        SEED_PARALLELISM,
        Array.from({ length: s.sessions }, (_, session) => () => this.enqueue(queueId, sessionId(session), seqs)),
      );
    }
    const seconds = (performance.now() - start) / 1000;
    console.log(`Seeded ${s.sessions * s.messagesPerSession} messages in ${seconds.toFixed(1)}s`);
    return seconds;
  }

  private async enqueue(queueId: string, session: string, seqs: number[]): Promise<void> {
    // Same process publishes and consumes, so wall-clock ms is a consistent publish -> handler clock.
    const publishedAt = Date.now();
    const items = seqs.map((seq) => ({ item: { session, seq, publishedAt }, sessionId: session }));
    const result = await this.client.enqueue(queueId, items);
    if (!result.success || result.itemsEnqueued !== items.length) {
      throw new Error(`Publishing to session ${session} failed: ${result.message}`);
    }
  }
}

// --- P-05 queue drain ---

/**
 * Publish messages to a plain queue and drain them with one QueueConsumer whose handler takes
 * settleMs per message. Completion is the moment every seq has been handled once.
 */
export class QueueDrainScenario {
  constructor(
    private readonly client: DaprMQClient,
    private readonly scenario: QueueDrainParams,
  ) {}

  async run(): Promise<Record<string, unknown>> {
    const s = this.scenario;
    const queueId = `perf-queue-${shortId()}`;
    console.log(`Queue ${queueId}: ${s.key}`);

    const clock = new Clock();
    const handled: QueueHandlerRecord[] = [];
    const seen = new Set<number>();
    const allHandled = deferred();
    let completedMs = 0;

    const consumer = new QueueConsumer(
      this.client,
      queueId,
      { maxActiveMessages: s.maxActiveMessages, maxConcurrentHandlers: s.maxConcurrentHandlers, strictOrder: s.strictOrder, drainTimeoutMs: 5000 },
      async (ctx) => {
        const start = clock.ms();
        const item = ctx.item as { seq: number; publishedAt: number };
        const deliveryLatencyMs = Date.now() - item.publishedAt;
        const settleMs = s.handlerMs(item.seq);
        if (settleMs > 0) {
          await sleep(settleMs);
        }
        const end = clock.ms();
        handled.push({ seq: item.seq, startMs: start, endMs: end, deliveryLatencyMs });
        if (!seen.has(item.seq)) {
          seen.add(item.seq);
          if (seen.size === s.messages) {
            completedMs = end;
            allHandled.resolve();
          }
        }
      },
    );

    const timeoutMs = ((s.idealSeconds ?? 0) * 3 + 600) * 1000;
    const stopProgress = progress(() => `t=${(clock.ms() / 1000).toFixed(0)}s  handled ${seen.size}/${s.messages}`);
    let seedSeconds: number;
    try {
      // With a live publisher the clock starts with publishing, so the wall clock includes it.
      if (s.livePublish) {
        clock.start();
        consumer.start();
        seedSeconds = await this.publishLive(queueId);
      } else {
        seedSeconds = await this.seed(queueId);
        clock.start();
        consumer.start();
      }
      console.log(`Published ${s.messages} messages in ${seedSeconds.toFixed(1)}s`);
      try {
        await withTimeout(allHandled.promise, timeoutMs, () => `Only ${seen.size}/${s.messages} messages handled after ${timeoutMs / 1000}s.`);
      } finally {
        await consumer.stop();
      }
    } finally {
      stopProgress();
    }

    return computeQueueDrain(s, handled, completedMs, seedSeconds);
  }

  private async seed(queueId: string): Promise<number> {
    const s = this.scenario;
    const start = performance.now();
    // Parallel batches land out of seq order; strict order checks handler order against queue
    // order, so it seeds with one publisher.
    const batches = Array.from({ length: Math.ceil(s.messages / SEED_BATCH) }, (_, b) =>
      Array.from({ length: Math.min(SEED_BATCH, s.messages - b * SEED_BATCH) }, (_, i) => b * SEED_BATCH + i),
    );
    await runLimited(s.strictOrder ? 1 : SEED_PARALLELISM, batches.map((batch) => () => this.enqueue(queueId, batch)));
    return (performance.now() - start) / 1000;
  }

  private async publishLive(queueId: string): Promise<number> {
    const s = this.scenario;
    const start = performance.now();
    const delays = publishDelays(s.messages, s.publishIntervalMs, s.publishJitterMs);
    for (let seq = 0; seq < delays.length; seq++) {
      await sleep(delays[seq]);
      await this.enqueue(queueId, [seq]);
    }
    return (performance.now() - start) / 1000;
  }

  private async enqueue(queueId: string, seqs: number[]): Promise<void> {
    const publishedAt = Date.now();
    const result = await this.client.enqueue(queueId, seqs.map((seq) => ({ item: { seq, publishedAt } })));
    if (!result.success || result.itemsEnqueued !== seqs.length) {
      throw new Error(`Publishing to ${queueId} failed: ${result.message}`);
    }
  }
}
