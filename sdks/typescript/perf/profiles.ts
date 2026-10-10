/**
 * The profiles of sdks/testing/PERFORMANCE_TESTS.md, exactly as tabled there and in the .NET
 * reference (sdks/dotnet/perf/DaprMQ.Client.Perf/PerfOptions.cs). The `key` strings group runs
 * across SDKs on the report, so they must match the .NET ones character for character
 * (sdks/testing/perf/profiles.json, checked by the tests).
 */

export type LoadScenarioName = "enqueue" | "enqueue-batch" | "dequeue-ack";
export type PublishMode = "before" | "concurrent";

/** P-01..P-03 closed-loop load. More than one concurrency value makes it a ramp; queues null = one per worker. */
export class LoadParams {
  constructor(
    readonly scenario: LoadScenarioName,
    readonly concurrency: number[],
    readonly queues: number | null,
    readonly batchSize: number,
    readonly dequeueCount: number,
    readonly payloadBytes: number,
    readonly seedPerQueue: number,
    readonly warmupSeconds: number,
    readonly durationSeconds: number,
  ) {}

  get id(): string {
    return { enqueue: "P-01", "enqueue-batch": "P-02", "dequeue-ack": "P-03" }[this.scenario];
  }

  queuesAt(concurrency: number): number {
    return this.queues ?? concurrency;
  }

  get key(): string {
    const shape = this.scenario === "dequeue-ack" ? `/k${this.dequeueCount}/seed${this.seedPerQueue}` : `/b${this.batchSize}`;
    return (
      `${this.scenario}:c${this.concurrency.join(",")}/q${this.queues ?? "=c"}${shape}` +
      `/${this.payloadBytes}B/${this.warmupSeconds}+${this.durationSeconds}s`
    );
  }

  params(): Record<string, unknown> {
    return {
      concurrency: this.concurrency,
      queues: this.queues,
      batchSize: this.batchSize,
      dequeueCount: this.dequeueCount,
      payloadBytes: this.payloadBytes,
      seedPerQueue: this.seedPerQueue,
      warmupSeconds: this.warmupSeconds,
      durationSeconds: this.durationSeconds,
    };
  }
}

/** P-04: sessions x messages drained by one SessionQueueConsumer. */
export class SessionDrainParams {
  readonly sessions: number;
  readonly messagesPerSession: number;
  readonly settleMs: number;
  readonly maxConcurrentSessions: number;
  readonly prefetchCount: number;
  readonly leaseSeconds: number;
  readonly sessionIdleTimeoutSeconds: number;
  readonly publishMode: PublishMode;
  readonly publishIntervalMs: number;
  readonly publishJitterMs: number;

  constructor(p: Partial<Omit<SessionDrainParams, "rateLimited" | "key" | "idealSeconds" | "params">> = {}) {
    this.sessions = p.sessions ?? 1000;
    this.messagesPerSession = p.messagesPerSession ?? 100;
    this.settleMs = p.settleMs ?? 1000;
    this.maxConcurrentSessions = p.maxConcurrentSessions ?? 20;
    this.prefetchCount = p.prefetchCount ?? 10;
    this.leaseSeconds = p.leaseSeconds ?? 30;
    this.sessionIdleTimeoutSeconds = p.sessionIdleTimeoutSeconds ?? 0;
    this.publishMode = p.publishMode ?? "before";
    this.publishIntervalMs = p.publishIntervalMs ?? 0;
    this.publishJitterMs = p.publishJitterMs ?? 0;
  }

  get rateLimited(): boolean {
    return this.publishIntervalMs > 0 || this.publishJitterMs > 0;
  }

  get key(): string {
    return (
      `${this.sessions}x${this.messagesPerSession}@${this.settleMs}ms/${this.maxConcurrentSessions}slots` +
      (this.publishMode === "before" ? "" : `+${this.publishMode}`) +
      (this.rateLimited ? `+pub${this.publishIntervalMs}ms~${this.publishJitterMs}ms` : "") +
      (this.sessionIdleTimeoutSeconds !== 0 ? `+idle${this.sessionIdleTimeoutSeconds}s` : "") +
      (this.prefetchCount !== 10 ? `+prefetch${this.prefetchCount}` : "") +
      (this.leaseSeconds !== 30 ? `+lease${this.leaseSeconds}s` : "")
    );
  }

  /**
   * Consumer-bound: ceil(sessions / slots) rounds of M x settle. Concurrent and rate limited, no
   * session can finish before its last message is published (~M x mean delay) and settled.
   */
  get idealSeconds(): number {
    const consumeBound = (Math.ceil(this.sessions / this.maxConcurrentSessions) * this.messagesPerSession * this.settleMs) / 1000;
    if (this.publishMode !== "concurrent" || !this.rateLimited) {
      return consumeBound;
    }
    const publishBound = (this.messagesPerSession * (this.publishIntervalMs + this.publishJitterMs / 2) + this.settleMs) / 1000;
    return Math.max(consumeBound, publishBound);
  }

  params(): Record<string, unknown> {
    return {
      sessions: this.sessions,
      messagesPerSession: this.messagesPerSession,
      settleMs: this.settleMs,
      maxConcurrentSessions: this.maxConcurrentSessions,
      prefetchCount: this.prefetchCount,
      leaseSeconds: this.leaseSeconds,
      sessionIdleTimeoutSeconds: this.sessionIdleTimeoutSeconds,
      publishMode: this.publishMode,
      publishIntervalMs: this.publishIntervalMs,
      publishJitterMs: this.publishJitterMs,
    };
  }
}

/** P-05: messages drained by one QueueConsumer; a publish interval publishes alongside it. */
export class QueueDrainParams {
  readonly strictOrder: boolean;
  readonly publishIntervalMs: number;
  readonly publishJitterMs: number;
  readonly tailEvery: number;
  readonly tailMs: number;

  constructor(
    readonly messages: number,
    readonly settleMs: number,
    readonly maxActiveMessages: number,
    readonly maxConcurrentHandlers: number,
    extra: { strictOrder?: boolean; publishIntervalMs?: number; publishJitterMs?: number; tailEvery?: number; tailMs?: number } = {},
  ) {
    this.strictOrder = extra.strictOrder ?? false;
    this.publishIntervalMs = extra.publishIntervalMs ?? 0;
    this.publishJitterMs = extra.publishJitterMs ?? 0;
    this.tailEvery = extra.tailEvery ?? 0;
    this.tailMs = extra.tailMs ?? 0;
  }

  get livePublish(): boolean {
    return this.publishIntervalMs > 0 || this.publishJitterMs > 0;
  }

  get key(): string {
    return (
      `queue:${this.messages}@${this.settleMs}ms/active${this.maxActiveMessages}` +
      (this.maxConcurrentHandlers > 0 ? `/handlers${this.maxConcurrentHandlers}` : "") +
      (this.strictOrder ? "+strict" : "") +
      (this.livePublish ? `+pub${this.publishIntervalMs}ms~${this.publishJitterMs}ms` : "") +
      (this.tailEvery > 0 ? `+tail${this.tailMs}ms/${this.tailEvery}` : "")
    );
  }

  /** Handlers that can run at once: strict order runs one, otherwise the window bounds them. */
  get concurrency(): number {
    if (this.strictOrder) {
      return 1;
    }
    return this.maxConcurrentHandlers > 0 ? Math.min(this.maxConcurrentHandlers, this.maxActiveMessages) : this.maxActiveMessages;
  }

  get tailMessages(): number {
    return this.tailEvery > 0 ? Math.floor(this.messages / this.tailEvery) : 0;
  }

  handlerMs(seq: number): number {
    return this.tailEvery > 0 && (seq + 1) % this.tailEvery === 0 ? this.tailMs : this.settleMs;
  }

  /**
   * The handler work spread over `concurrency`, but no less than the slowest single handler, nor
   * than the last live-published message plus its handler. Null for an instant handler.
   */
  get idealSeconds(): number | null {
    if (this.settleMs === 0 && this.tailMs === 0 && !this.livePublish) {
      return null;
    }
    const workMs = (this.messages - this.tailMessages) * this.settleMs + this.tailMessages * this.tailMs;
    const slowestMs = this.tailMessages > 0 ? Math.max(this.settleMs, this.tailMs) : this.settleMs;
    const lastPublishedMs = this.livePublish ? (this.messages - 1) * this.publishIntervalMs + this.handlerMs(this.messages - 1) : 0;
    return Math.max(workMs / this.concurrency, slowestMs, lastPublishedMs) / 1000;
  }

  params(): Record<string, unknown> {
    return {
      messages: this.messages,
      settleMs: this.settleMs,
      maxActiveMessages: this.maxActiveMessages,
      maxConcurrentHandlers: this.maxConcurrentHandlers,
      strictOrder: this.strictOrder,
      publishIntervalMs: this.publishIntervalMs,
      publishJitterMs: this.publishJitterMs,
      tailEvery: this.tailEvery,
      tailMs: this.tailMs,
    };
  }
}

const RAMP = [1, 2, 4, 8, 16, 32, 64];
const WIDE_RAMP = [...RAMP, 128, 256];

export const LOAD_PROFILES: Record<string, LoadParams> = {
  enqueue: new LoadParams("enqueue", [8], 8, 1, 1, 256, 0, 3, 15),
  "enqueue-hot": new LoadParams("enqueue", [8], 1, 1, 1, 256, 0, 3, 15),
  "enqueue-batch": new LoadParams("enqueue-batch", [4], 4, 100, 1, 256, 0, 3, 15),
  "dequeue-ack": new LoadParams("dequeue-ack", [8], 8, 1, 1, 256, 4000, 3, 15),
  "enqueue-ramp": new LoadParams("enqueue", WIDE_RAMP, null, 1, 1, 256, 0, 5, 30),
  "enqueue-hot-ramp": new LoadParams("enqueue", RAMP, 1, 1, 1, 256, 0, 5, 30),
  "enqueue-batch-ramp": new LoadParams("enqueue-batch", RAMP, null, 100, 1, 256, 0, 5, 30),
  "dequeue-ack-ramp": new LoadParams("dequeue-ack", WIDE_RAMP, null, 1, 1, 256, 12000, 5, 30),
};

export const SESSION_DRAIN_PROFILES: Record<string, SessionDrainParams> = {
  "steady-drain": new SessionDrainParams({ sessions: 200, messagesPerSession: 20, settleMs: 100, sessionIdleTimeoutSeconds: 1 }),
  "session-churn": new SessionDrainParams({ sessions: 300, messagesPerSession: 2, settleMs: 50, sessionIdleTimeoutSeconds: 1 }),
  "deep-session": new SessionDrainParams({
    sessions: 4, messagesPerSession: 1000, settleMs: 10, maxConcurrentSessions: 4, sessionIdleTimeoutSeconds: 1,
  }),
  "live-publish": new SessionDrainParams({
    sessions: 20, messagesPerSession: 50, settleMs: 100, sessionIdleTimeoutSeconds: 1,
    publishMode: "concurrent", publishIntervalMs: 200, publishJitterMs: 100,
  }),
  "sdk-defaults": new SessionDrainParams({ sessions: 40, messagesPerSession: 5 }),
  full: new SessionDrainParams(),
  "wide-drain": new SessionDrainParams({
    sessions: 2000, messagesPerSession: 10, settleMs: 50, maxConcurrentSessions: 200, sessionIdleTimeoutSeconds: 1,
  }),
};

export const QUEUE_DRAIN_PROFILES: Record<string, QueueDrainParams> = {
  "queue-drain-instant": new QueueDrainParams(4000, 0, 100, 0),
  "queue-drain": new QueueDrainParams(4000, 10, 100, 0),
  "queue-drain-slow": new QueueDrainParams(3000, 100, 100, 0),
  "queue-strict-order": new QueueDrainParams(300, 0, 100, 0, { strictOrder: true }),
  "queue-live-publish": new QueueDrainParams(50, 10, 100, 0, { publishIntervalMs: 200, publishJitterMs: 100 }),
  "queue-drain-large": new QueueDrainParams(50000, 10, 500, 0),
  "queue-drain-tail": new QueueDrainParams(2000, 100, 100, 0, { tailEvery: 20, tailMs: 5000 }),
};

export const SUITES: Record<string, string[]> = {
  pr: [
    "enqueue", "enqueue-hot", "enqueue-batch", "dequeue-ack",
    "steady-drain", "session-churn", "deep-session", "live-publish", "sdk-defaults",
    "queue-drain-instant", "queue-drain", "queue-drain-slow", "queue-strict-order", "queue-live-publish",
  ],
  extreme: [
    "enqueue-ramp", "enqueue-hot-ramp", "enqueue-batch-ramp", "dequeue-ack-ramp",
    "full", "wide-drain", "queue-drain-large", "queue-drain-tail",
  ],
};

export const PROFILES = [...Object.keys(LOAD_PROFILES), ...Object.keys(SESSION_DRAIN_PROFILES), ...Object.keys(QUEUE_DRAIN_PROFILES)];

export function scaleOf(profile: string): string {
  return Object.entries(SUITES).find(([, profiles]) => profiles.includes(profile))?.[0] ?? "adhoc";
}
