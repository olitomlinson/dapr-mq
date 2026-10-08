import { describe, expect, it, vi } from "vitest";
import { NoSessionsAvailableError } from "../src/errors.js";
import { SessionQueueConsumer, type SessionCapableClient, type SessionMessageContext } from "../src/sessionQueueConsumer.js";
import type { SessionDelivery } from "../src/types.js";

function makeDelivery(
  sessionId: string,
  lockId: string,
): { delivery: SessionDelivery; acked: () => boolean; deadLettered: () => boolean; nacked: () => boolean } {
  let acked = false;
  let deadLettered = false;
  let nacked = false;
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
    nack: async () => {
      nacked = true;
    },
  };
  return { delivery, acked: () => acked, deadLettered: () => deadLettered, nacked: () => nacked };
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

  it("nackMessage nacks instead of dead-lettering when the handler throws", async () => {
    const { delivery, acked, deadLettered, nacked } = makeDelivery("s1", "L1");
    const client: SessionCapableClient = {
      consumeSession: () => singleItemThenComplete(delivery),
    };

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1, onHandlerException: "nackMessage" }, async () => {
      throw new Error("handler blew up");
    });

    consumer.start();
    await waitUntil(nacked, 2000);
    await consumer.stop();

    expect(nacked()).toBe(true);
    expect(deadLettered()).toBe(false);
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

  /**
   * Hands out `items` on each stream, then waits; like the real stream, it hands out nothing
   * further once its signal aborts, then ends. Records how each stream was closed.
   */
  function streamingClient(...items: SessionDelivery[]) {
    const signals: AbortSignal[] = [];
    const closed: boolean[] = [];
    const client: SessionCapableClient = {
      consumeSession: (_queueId, { signal }) => {
        signals.push(signal!);
        const index = closed.push(false) - 1;
        return (async function* () {
          try {
            for (const item of items) {
              if (signal!.aborted) return;
              yield item;
            }
            if (!signal!.aborted) {
              await new Promise((resolve) => signal!.addEventListener("abort", resolve, { once: true }));
            }
          } finally {
            closed[index] = true;
          }
        })();
      },
    };
    return { client, signals, closed };
  }

  it("stop() during a handler lets it finish and ack before closing the stream", async () => {
    const { delivery, acked } = makeDelivery("s1", "L1");
    const { client, signals, closed } = streamingClient(delivery);
    let entered!: () => void;
    const enteredPromise = new Promise<void>((resolve) => (entered = resolve));
    let handlerAborted: boolean | undefined;
    let streamClosedUnderHandler: boolean | undefined;

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1 }, async (_ctx, signal) => {
      entered();
      await new Promise((r) => setTimeout(r, 200));
      handlerAborted = signal.aborted;
      streamClosedUnderHandler = closed[0] || signals[0].aborted;
    });

    consumer.start();
    await enteredPromise;
    await consumer.stop();

    expect(handlerAborted).toBe(false);
    expect(streamClosedUnderHandler).toBe(false);
    expect(acked()).toBe(true);
    expect(closed[0]).toBe(true);
  });

  it("stop() past drainTimeoutMs aborts the handler's signal", async () => {
    const { delivery, acked, deadLettered } = makeDelivery("s1", "L1");
    const { client } = streamingClient(delivery);
    let entered!: () => void;
    const enteredPromise = new Promise<void>((resolve) => (entered = resolve));
    let handlerAborted = false;

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1, drainTimeoutMs: 100 }, async (_ctx, signal) => {
      entered();
      await new Promise((resolve) => signal.addEventListener("abort", resolve, { once: true }));
      handlerAborted = true;
      throw new Error("aborted");
    });

    consumer.start();
    await enteredPromise;
    const started = Date.now();
    await consumer.stop();

    expect(Date.now() - started).toBeLessThan(2000);
    expect(handlerAborted).toBe(true);
    expect(acked()).toBe(false);
    expect(deadLettered()).toBe(false);
  });

  it("stop() while idle closes the stream promptly", async () => {
    const { client, signals, closed } = streamingClient();
    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1 }, async () => {});

    consumer.start();
    await waitUntil(() => signals.length === 1, 2000);
    const started = Date.now();
    await consumer.stop();

    expect(Date.now() - started).toBeLessThan(1000);
    expect(signals[0].aborted).toBe(true);
    expect(closed[0]).toBe(true);
  });

  it("stop() during a handler does not start a prefetched message", async () => {
    const first = makeDelivery("s1", "L1");
    const second = makeDelivery("s1", "L2");
    const { client } = streamingClient(first.delivery, second.delivery);
    let entered!: () => void;
    const enteredPromise = new Promise<void>((resolve) => (entered = resolve));
    const handled: string[] = [];

    const consumer = new SessionQueueConsumer(client, "q", { maxConcurrentSessions: 1, prefetchCount: 2 }, async (ctx) => {
      handled.push(ctx.lockId);
      entered();
      await new Promise((r) => setTimeout(r, 100));
    });

    consumer.start();
    await enteredPromise;
    await consumer.stop();

    expect(handled).toEqual(["L1"]);
    expect(first.acked()).toBe(true);
    expect(second.acked()).toBe(false);
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
