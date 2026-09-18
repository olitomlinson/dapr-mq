import { getClient, getQueuePrefix } from "./config.js";
import { log } from "./logger.js";

export interface ScenarioStep {
  action: string;
  detail: string;
}

export interface ScenarioRunResult {
  queueId: string;
  steps: ScenarioStep[];
}

export type ScenarioName = "basic" | "ack-deadletter" | "priority" | "sessions" | "idempotency";

export interface ScenarioDescriptor {
  name: ScenarioName;
  role: "producer";
  description: string;
  run: () => Promise<ScenarioRunResult>;
}

/** basic - enqueues 3 plain items in a single Enqueue call, default priority, no session. */
async function runBasic(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-basic`;
  const client = getClient();

  const items = [1, 2, 3].map((n) => ({ item: { n, message: "hello from producer" } }));
  await client.enqueue(queueId, items);

  const steps: ScenarioStep[] = [];
  items.forEach((_entry, index) => {
    const detail = `Enqueued item ${index + 1}/${items.length} to queue ${queueId} (priority=1)`;
    log("INFO", detail);
    steps.push({ action: "enqueue", detail });
  });

  return { queueId, steps };
}

/** ack-deadletter - enqueues 3 items tagged with the outcome the consumer should apply. */
async function runAckDeadletter(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-ackdlq`;
  const client = getClient();

  const items = [
    { item: { outcome: "ack", n: 1 } },
    { item: { outcome: "deadletter", n: 2 } },
    { item: { outcome: "expire", n: 3 } },
  ];
  await client.enqueue(queueId, items);

  const steps: ScenarioStep[] = [];
  items.forEach(({ item }) => {
    const { outcome, n } = item as { outcome: string; n: number };
    const detail = `Enqueued item ${n}/${items.length} (outcome=${outcome}) to queue ${queueId}`;
    log("INFO", detail);
    steps.push({ action: "enqueue", detail });
  });

  return { queueId, steps };
}

/**
 * priority - enqueues 3 normal-priority (1) items first, then 3 fast-lane (0) items, deliberately
 * priority-inverted so the consumer scenario can demonstrate the fast-lane items surfacing first.
 */
async function runPriority(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-priority`;
  const client = getClient();
  const steps: ScenarioStep[] = [];

  const normalItems = [1, 2, 3].map((n) => ({ item: { priority: 1, n }, priority: 1 }));
  await client.enqueue(queueId, normalItems);
  normalItems.forEach(({ item }) => {
    const { n } = item as { n: number };
    const detail = `Enqueued item n=${n} to queue ${queueId} (priority=1, normal)`;
    log("INFO", detail);
    steps.push({ action: "enqueue", detail });
  });

  const fastLaneItems = [4, 5, 6].map((n) => ({ item: { priority: 0, n }, priority: 0 }));
  await client.enqueue(queueId, fastLaneItems);
  fastLaneItems.forEach(({ item }) => {
    const { n } = item as { n: number };
    const detail = `Enqueued item n=${n} to queue ${queueId} (priority=0, fast-lane)`;
    log("INFO", detail);
    steps.push({ action: "enqueue", detail });
  });

  return { queueId, steps };
}

/** sessions - enqueues 4 items, 2 per session, each carrying a per-session sequence number. */
async function runSessions(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-sessions`;
  const client = getClient();

  const items = [
    { item: { sessionId: "session-a", seq: 1 }, sessionId: "session-a" },
    { item: { sessionId: "session-a", seq: 2 }, sessionId: "session-a" },
    { item: { sessionId: "session-b", seq: 1 }, sessionId: "session-b" },
    { item: { sessionId: "session-b", seq: 2 }, sessionId: "session-b" },
  ];
  await client.enqueue(queueId, items);

  const steps: ScenarioStep[] = [];
  items.forEach((entry) => {
    const { sessionId, seq } = entry.item as { sessionId: string; seq: number };
    const detail = `Enqueued item seq=${seq} for session=${sessionId} to queue ${queueId}`;
    log("INFO", detail);
    steps.push({ action: "enqueue", detail });
  });

  return { queueId, steps };
}

/**
 * idempotency - enqueues one item with a fresh idempotencyKey, then a second item reusing the
 * SAME key (expected to be silently deduplicated), then a third item with a different key
 * (expected to enqueue normally). The key is suffixed with the current time so repeated runs of
 * this scenario don't collide with a previous run's key still inside the dedup TTL window.
 */
async function runIdempotency(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-idempotency`;
  const client = getClient();
  const steps: ScenarioStep[] = [];
  const key = `idempotency-demo-${Date.now()}`;

  const first = await client.enqueue(queueId, [{ item: { n: 1 }, idempotencyKey: key }]);
  let detail = `Enqueued item n=1 with idempotencyKey=${key} (itemsEnqueued=${first.itemsEnqueued}, itemsDeduplicated=${first.itemsDeduplicated})`;
  log("INFO", detail);
  steps.push({ action: "enqueue", detail });

  const duplicate = await client.enqueue(queueId, [{ item: { n: 2 }, idempotencyKey: key }]);
  detail = `Enqueued item n=2 reusing idempotencyKey=${key} (itemsEnqueued=${duplicate.itemsEnqueued}, itemsDeduplicated=${duplicate.itemsDeduplicated}) - expected to be deduplicated`;
  log("INFO", detail);
  steps.push({ action: "enqueue", detail });

  const distinctKey = `${key}-b`;
  const distinct = await client.enqueue(queueId, [{ item: { n: 3 }, idempotencyKey: distinctKey }]);
  detail = `Enqueued item n=3 with a different idempotencyKey=${distinctKey} (itemsEnqueued=${distinct.itemsEnqueued}, itemsDeduplicated=${distinct.itemsDeduplicated})`;
  log("INFO", detail);
  steps.push({ action: "enqueue", detail });

  return { queueId, steps };
}

export const SCENARIOS: ScenarioDescriptor[] = [
  { name: "basic", role: "producer", description: "Enqueues 3 plain items.", run: runBasic },
  {
    name: "ack-deadletter",
    role: "producer",
    description: "Enqueues 3 items tagged ack/deadletter/expire.",
    run: runAckDeadletter,
  },
  {
    name: "priority",
    role: "producer",
    description: "Enqueues normal-priority items, then fast-lane items.",
    run: runPriority,
  },
  { name: "sessions", role: "producer", description: "Enqueues items across two sessions.", run: runSessions },
  {
    name: "idempotency",
    role: "producer",
    description: "Enqueues an item, a duplicate reusing its idempotency key, then a distinct item.",
    run: runIdempotency,
  },
];
