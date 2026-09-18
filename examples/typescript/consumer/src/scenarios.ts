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
  role: "consumer";
  description: string;
  run: () => Promise<ScenarioRunResult>;
}

const EMPTY_QUEUE_DETAIL = "queue empty — run the producer's scenario first";

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** basic - dequeues with ack (count=5, default ttl) and immediately acknowledges every item returned. */
async function runBasic(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-basic`;
  const client = getClient();
  const steps: ScenarioStep[] = [];

  const result = await client.dequeueLocked(queueId, { count: 5 });
  if (!result || result.items.length === 0) {
    log("WARN", EMPTY_QUEUE_DETAIL);
    steps.push({ action: "dequeue", detail: EMPTY_QUEUE_DETAIL });
    return { queueId, steps };
  }

  const dequeueDetail = `Dequeued ${result.items.length} item(s) from queue ${queueId}`;
  log("INFO", dequeueDetail);
  steps.push({ action: "dequeue", detail: dequeueDetail });

  for (const dequeued of result.items) {
    await client.acknowledge(queueId, dequeued.lockId);
    const detail = `Acknowledged item ${JSON.stringify(dequeued.item)} (lockId=${dequeued.lockId})`;
    log("INFO", detail);
    steps.push({ action: "acknowledge", detail });
  }

  return { queueId, steps };
}

/**
 * ack-deadletter - dequeues 3 items (ttl=10s) and branches on each item's `outcome` field: acks
 * "ack", dead-letters "deadletter" (logging the resulting DLQ id), and leaves "expire" locked.
 * Then waits for the lock to expire, re-dequeues to show redelivery, and finally drains the DLQ.
 */
async function runAckDeadletter(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-ackdlq`;
  const dlqId = `${queueId}-deadletter`;
  const client = getClient();
  const steps: ScenarioStep[] = [];

  const result = await client.dequeueLocked(queueId, { count: 3, ttlSeconds: 10 });
  if (!result || result.items.length === 0) {
    log("WARN", EMPTY_QUEUE_DETAIL);
    steps.push({ action: "dequeue", detail: EMPTY_QUEUE_DETAIL });
    return { queueId, steps };
  }

  const lockIds = result.items.map((i) => i.lockId);
  const dequeueDetail = `dequeued ${result.items.length} items, lockIds=[${lockIds.join(", ")}]`;
  log("INFO", dequeueDetail);
  steps.push({ action: "dequeue", detail: dequeueDetail });

  for (const dequeued of result.items) {
    const payload = dequeued.item as { outcome?: string; n?: number };
    const outcome = payload.outcome ?? "ack";

    if (outcome === "deadletter") {
      await client.deadLetter(queueId, dequeued.lockId);
      const detail = `dead-lettered item outcome=deadletter -> dlqId=${dlqId}`;
      log("INFO", detail);
      steps.push({ action: "deadletter", detail });
    } else if (outcome === "expire") {
      const detail = `leaving item outcome=expire locked to expire (n=${payload.n})`;
      log("INFO", detail);
      steps.push({ action: "skip", detail });
    } else {
      await client.acknowledge(queueId, dequeued.lockId);
      const detail = `acked item outcome=${outcome} (n=${payload.n})`;
      log("INFO", detail);
      steps.push({ action: "acknowledge", detail });
    }
  }

  const waitDetail = "waiting 11s for outcome=expire item's lock to expire";
  log("INFO", waitDetail);
  steps.push({ action: "wait", detail: waitDetail });
  await sleep(11000);

  const redelivered = await client.dequeueLocked(queueId, { count: 3, ttlSeconds: 10 });
  if (redelivered && redelivered.items.length > 0) {
    const redeliverDetail = `dequeued ${redelivered.items.length} item(s) again via redelivery`;
    log("INFO", redeliverDetail);
    steps.push({ action: "dequeue", detail: redeliverDetail });

    for (const dequeued of redelivered.items) {
      await client.acknowledge(queueId, dequeued.lockId);
      const detail = `acknowledged redelivered item ${JSON.stringify(dequeued.item)}`;
      log("INFO", detail);
      steps.push({ action: "acknowledge", detail });
    }
  } else {
    const detail = "no redelivered item found on second dequeue";
    log("WARN", detail);
    steps.push({ action: "dequeue", detail });
  }

  const dlqResult = await client.dequeueLocked(dlqId, { count: 1 });
  if (dlqResult && dlqResult.items.length > 0) {
    const dlqItem = dlqResult.items[0];
    const dequeueDlqDetail = `dequeued dead-lettered item from ${dlqId}: ${JSON.stringify(dlqItem.item)}`;
    log("INFO", dequeueDlqDetail);
    steps.push({ action: "dequeue", detail: dequeueDlqDetail });

    await client.acknowledge(dlqId, dlqItem.lockId);
    const ackDlqDetail = `acknowledged item in ${dlqId} (drained)`;
    log("INFO", ackDlqDetail);
    steps.push({ action: "acknowledge", detail: ackDlqDetail });
  } else {
    const detail = `${dlqId} empty — no dead-lettered item to drain`;
    log("WARN", detail);
    steps.push({ action: "dequeue", detail });
  }

  return { queueId, steps };
}

/**
 * priority - dequeues with ack (count=10) in one call and acknowledges each item as it's returned,
 * demonstrating that the fast-lane (priority=0) items surface before the normal ones despite
 * being enqueued later.
 */
async function runPriority(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-priority`;
  const client = getClient();
  const steps: ScenarioStep[] = [];

  const result = await client.dequeueLocked(queueId, { count: 10 });
  if (!result || result.items.length === 0) {
    log("WARN", EMPTY_QUEUE_DETAIL);
    steps.push({ action: "dequeue", detail: EMPTY_QUEUE_DETAIL });
    return { queueId, steps };
  }

  const order = result.items.map((i) => (i.item as { n?: number }).n);
  const dequeueDetail = `dequeued ${result.items.length} item(s) in order n=[${order.join(", ")}]`;
  log("INFO", dequeueDetail);
  steps.push({ action: "dequeue", detail: dequeueDetail });

  for (const dequeued of result.items) {
    await client.acknowledge(queueId, dequeued.lockId);
    const n = (dequeued.item as { n?: number }).n;
    const detail = `acknowledged item n=${n} (priority=${dequeued.priority})`;
    log("INFO", detail);
    steps.push({ action: "acknowledge", detail });
  }

  return { queueId, steps };
}

const KNOWN_SESSION_IDS = ["session-a", "session-b"];

async function drainSession(
  queueId: string,
  sessionId: string,
  leaseId: string,
  steps: ScenarioStep[],
): Promise<void> {
  const client = getClient();
  const sessionQueueId = `${queueId}-session-${sessionId}`;
  const dequeued = await client.dequeueLocked(sessionQueueId, { count: 10, leaseId });

  if (!dequeued || dequeued.items.length === 0) {
    const detail = `session queue ${sessionQueueId} empty`;
    log("WARN", detail);
    steps.push({ action: "dequeue", detail });
  } else {
    const order = dequeued.items.map((i) => (i.item as { seq?: number }).seq);
    const dequeueDetail = `dequeued ${dequeued.items.length} item(s) from ${sessionQueueId} in order seq=[${order.join(", ")}]`;
    log("INFO", dequeueDetail);
    steps.push({ action: "dequeue", detail: dequeueDetail });

    for (const item of dequeued.items) {
      await client.acknowledge(sessionQueueId, item.lockId, { leaseId });
      const seq = (item.item as { seq?: number }).seq;
      const detail = `acknowledged item seq=${seq} for session=${sessionId}`;
      log("INFO", detail);
      steps.push({ action: "acknowledge", detail });
    }
  }

  await client.releaseSession(queueId, sessionId, leaseId);
  const releaseDetail = `released session=${sessionId} leaseId=${leaseId}`;
  log("INFO", releaseDetail);
  steps.push({ action: "release-session", detail: releaseDetail });
}

/**
 * sessions - round 1 accepts "any available" session (server picks whichever of the known
 * sessions is unclaimed); round 2 targets whichever known session round 1 didn't return (both,
 * if round 1 found nothing available). Each round dequeues with ack against the derived session
 * queue id, acknowledges each item, then releases the session.
 */
async function runSessions(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-sessions`;
  const client = getClient();
  const steps: ScenarioStep[] = [];

  // Round 1: "any available" mode (no sessionId) - the server picks whichever of the
  // known sessions is currently unclaimed.
  let claimedFirst: string | undefined;
  const anyLease = await client.acceptSession(queueId, { leaseSeconds: 30 });
  if (!anyLease) {
    const detail = "no session currently available (any-available mode)";
    log("WARN", detail);
    steps.push({ action: "accept-session", detail });
  } else {
    claimedFirst = anyLease.sessionId;
    const detail = `accepted session=${claimedFirst} via any-available mode leaseId=${anyLease.leaseId}`;
    log("INFO", detail);
    steps.push({ action: "accept-session", detail });
    await drainSession(queueId, claimedFirst, anyLease.leaseId, steps);
  }

  // Round 2: targeted mode - accept whichever known session round 1 didn't return
  // (both, if round 1 found nothing available).
  for (const sessionId of KNOWN_SESSION_IDS) {
    if (sessionId === claimedFirst) {
      continue;
    }

    const lease = await client.acceptSession(queueId, { sessionId, leaseSeconds: 30 });
    if (!lease) {
      const detail = `session ${sessionId} not available (targeted mode) — run the producer's scenario first`;
      log("WARN", detail);
      steps.push({ action: "accept-session", detail });
      continue;
    }

    const acceptDetail = `accepted session=${sessionId} via targeted mode leaseId=${lease.leaseId}`;
    log("INFO", acceptDetail);
    steps.push({ action: "accept-session", detail: acceptDetail });
    await drainSession(queueId, sessionId, lease.leaseId, steps);
  }

  return { queueId, steps };
}

/**
 * idempotency - dequeues from the same queue the producer's idempotency scenario just filled.
 * Expects exactly 2 items (n=1 and n=3) since n=2, the duplicate, should never have been
 * enqueued at all - logs that expectation explicitly and acknowledges whatever is returned.
 */
async function runIdempotency(): Promise<ScenarioRunResult> {
  const queueId = `${getQueuePrefix()}-idempotency`;
  const client = getClient();
  const steps: ScenarioStep[] = [];

  const result = await client.dequeueLocked(queueId, { count: 5 });
  if (!result || result.items.length === 0) {
    log("WARN", EMPTY_QUEUE_DETAIL);
    steps.push({ action: "dequeue", detail: EMPTY_QUEUE_DETAIL });
    return { queueId, steps };
  }

  const order = result.items.map((i) => (i.item as { n?: number }).n);
  const dequeueDetail = `dequeued ${result.items.length} item(s) n=[${order.join(", ")}] - expecting 2 (n=1, n=3); the duplicate n=2 should have been silently dropped by idempotency dedup`;
  log("INFO", dequeueDetail);
  steps.push({ action: "dequeue", detail: dequeueDetail });

  for (const dequeued of result.items) {
    await client.acknowledge(queueId, dequeued.lockId);
    const n = (dequeued.item as { n?: number }).n;
    const detail = `acknowledged item n=${n}`;
    log("INFO", detail);
    steps.push({ action: "acknowledge", detail });
  }

  return { queueId, steps };
}

export const SCENARIOS: ScenarioDescriptor[] = [
  { name: "basic", role: "consumer", description: "Dequeues items with ack and acknowledges each one.", run: runBasic },
  {
    name: "ack-deadletter",
    role: "consumer",
    description: "Acks, dead-letters, and lets one item expire, then drains the DLQ.",
    run: runAckDeadletter,
  },
  {
    name: "priority",
    role: "consumer",
    description: "Dequeues items to demonstrate fast-lane items surfacing before normal ones.",
    run: runPriority,
  },
  {
    name: "sessions",
    role: "consumer",
    description: "Accepts each session in turn, dequeues and acknowledges its items, then releases it.",
    run: runSessions,
  },
  {
    name: "idempotency",
    role: "consumer",
    description: "Dequeues and acknowledges the survivors of the producer's dedup demo.",
    run: runIdempotency,
  },
];
