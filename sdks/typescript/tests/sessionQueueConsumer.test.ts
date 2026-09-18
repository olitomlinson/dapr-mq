import { describe, expect, it, vi } from "vitest";
import { NoSessionsAvailableError } from "../src/errors.js";
import { SessionQueueConsumer, type SessionCapableClient, type SessionMessageContext } from "../src/sessionQueueConsumer.js";
import type { SessionDelivery } from "../src/types.js";

function makeDelivery(sessionId: string, lockId: string): { delivery: SessionDelivery; acked: () => boolean; deadLettered: () => boolean } {
  let acked = false;
  let deadLettered = false;
  const delivery: SessionDelivery = {
    sessionId,
    lockId,
    item: {},
    priority: 0,
    lockExpiresAt: 0,
    ack: async () => {
      acked = true;
    },
    deadLetter: async () => {
      deadLettered = true;
    },
  };
  return { delivery, acked: () => acked, deadLettered: () => deadLettered };
}

function macrotaskYield(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

async function* singleItemThenComplete(delivery: SessionDelivery): AsyncIterable<SessionDelivery> {
  await macrotaskYield();
  yield delivery;
}

async function* throwingSequence(err: Error): AsyncIterable<SessionDelivery> {
  await macrotaskYield();
  throw err;
}

async function waitUntil(condition: () => boolean, timeoutMs: number): Promise<void> {
  const start = Date.now();
  while (!condition() && Date.now() - start < timeoutMs) {
    await new Promise((r) => setTimeout(r, 10));
  }
}

describe("SessionQueueConsumer", () => {
  it("claims, delivers, handles, and acks on the happy path", async () => {
    const { delivery, acked, deadLettered } = makeDelivery("s1", "L1");
    let handlerCalled: () => void;
    const handlerCalledPromise = new Promise<void>((resolve) => {
      handlerCalled = resolve;
    });

    const client: SessionCapableClient = {
      consumeSession: () => singleItemThenComplete(delivery),
    };

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1 }, async (ctx: SessionMessageContext) => {
      expect(ctx.sessionId).toBe("s1");
      expect(ctx.lockId).toBe("L1");
      handlerCalled();
    });

    consumer.start();
    await handlerCalledPromise;
    await waitUntil(acked, 2000);
    await consumer.stop();

    expect(acked()).toBe(true);
    expect(deadLettered()).toBe(false);
  });

  it("backs off doubling on repeated NoSessionsAvailableError, then resets on a claim", async () => {
    let callCount = 0;
    const delays: number[] = [];

    const client: SessionCapableClient = {
      consumeSession: () => {
        callCount++;
        return callCount <= 3 ? throwingSequence(new NoSessionsAvailableError("none")) : singleItemThenComplete(makeDelivery("s1", "L1").delivery);
      },
    };

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1, minBackoffSeconds: 1, maxBackoffSeconds: 60 }, async () => {});
    consumer.delay = (ms) =>
      new Promise((resolve) => {
        delays.push(ms / 1000);
        setTimeout(resolve, 0);
      });

    consumer.start();
    await waitUntil(() => delays.length >= 3, 2000);
    await consumer.stop();

    expect(delays.length).toBeGreaterThanOrEqual(3);
    expect(delays[0]).toBe(1);
    expect(delays[1]).toBe(2);
    expect(delays[2]).toBe(4);
  });

  it("caps backoff at maxBackoffSeconds", async () => {
    const delays: number[] = [];

    const client: SessionCapableClient = {
      consumeSession: () => throwingSequence(new NoSessionsAvailableError("none")),
    };

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1, minBackoffSeconds: 1, maxBackoffSeconds: 4 }, async () => {});
    consumer.delay = (ms) =>
      new Promise((resolve) => {
        delays.push(ms / 1000);
        setTimeout(resolve, 0);
      });

    consumer.start();
    await waitUntil(() => delays.length >= 5, 2000);
    await consumer.stop();

    expect(delays.length).toBeGreaterThanOrEqual(5);
    expect(delays[0]).toBe(1);
    expect(delays[1]).toBe(2);
    expect(delays[2]).toBe(4);
    expect(delays[3]).toBe(4); // capped
    expect(delays[4]).toBe(4);
  });

  it("dead-letters the message by default when the handler throws", async () => {
    const { delivery, acked, deadLettered } = makeDelivery("s1", "L1");
    let deadLetterTriggered: () => void;
    const deadLetterTriggeredPromise = new Promise<void>((resolve) => {
      deadLetterTriggered = resolve;
    });

    const client: SessionCapableClient = {
      consumeSession: () => singleItemThenComplete(delivery),
    };

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1, onHandlerException: "deadLetterMessage" }, async () => {
      deadLetterTriggered();
      throw new Error("handler blew up");
    });

    consumer.start();
    await deadLetterTriggeredPromise;
    await waitUntil(deadLettered, 2000);
    await consumer.stop();

    expect(deadLettered()).toBe(true);
    expect(acked()).toBe(false);
  });

  it("abandonSession does not dead-letter and keeps the slot alive", async () => {
    const { delivery, acked, deadLettered } = makeDelivery("s1", "L1");
    const consumeSession = vi.fn(() => singleItemThenComplete(delivery));
    const client: SessionCapableClient = { consumeSession };

    let handlerRan: () => void;
    const handlerRanPromise = new Promise<void>((resolve) => {
      handlerRan = resolve;
    });

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1, onHandlerException: "abandonSession" }, async () => {
      handlerRan();
      throw new Error("handler blew up");
    });

    consumer.start();
    await handlerRanPromise;
    await waitUntil(() => consumeSession.mock.calls.length >= 2, 2000);
    await consumer.stop();

    expect(deadLettered()).toBe(false);
    expect(acked()).toBe(false);
    expect(consumeSession.mock.calls.length).toBeGreaterThanOrEqual(2);
  });

  it("stop() drains gracefully without throwing", async () => {
    const client: SessionCapableClient = {
      consumeSession: () => throwingSequence(new NoSessionsAvailableError("none")),
    };

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 2 }, async () => {});
    consumer.delay = () => macrotaskYield();

    consumer.start();
    await new Promise((r) => setTimeout(r, 20));
    await expect(consumer.stop()).resolves.toBeUndefined();
  });

  it("throws when targetSessionId is set without maxConcurrentSessions === 1", () => {
    const client: SessionCapableClient = { consumeSession: () => singleItemThenComplete(makeDelivery("s1", "L1").delivery) };

    expect(() => new SessionQueueConsumer(client, "q", { targetSessionId: "s1", maxConcurrentSessions: 2 }, async () => {})).toThrow();
  });
});
