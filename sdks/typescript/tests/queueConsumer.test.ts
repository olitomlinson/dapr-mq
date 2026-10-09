import { describe, expect, it } from "vitest";
import { StreamClosedError } from "../src/errors.js";
import { QueueConsumer, type QueueCapableClient, type QueueMessageContext } from "../src/queueConsumer.js";
import type { ConsumeOptions, QueueDelivery } from "../src/types.js";

/**
 * Stands in for one consume stream: the test pushes deliveries and settlements are recorded. Like
 * the real stream, aborting its signal ends it, and settling afterwards rejects.
 */
class FakeStream {
  readonly settled: [string, string][] = [];
  closed = false;
  private readonly items: (QueueDelivery | Error)[] = [];
  private wake: (() => void) | undefined;

  deliver(lockId: string, deliveryCount = 1): void {
    const settle = async (action: string) => {
      if (this.closed) {
        throw new StreamClosedError("closed");
      }
      this.settled.push([action, lockId]);
    };
    this.push({
      lockId,
      item: { n: 1 },
      priority: 1,
      lockExpiresAt: 0,
      deliveryCount,
      ack: () => settle("ack"),
      nack: () => settle("nack"),
      deadLetter: () => settle("deadLetter"),
    });
  }

  /** The stream breaks, as when the gateway goes away. */
  break(): void {
    this.push(new Error("stream broke"));
  }

  private push(item: QueueDelivery | Error): void {
    this.items.push(item);
    this.wake?.();
  }

  async *read(signal: AbortSignal): AsyncGenerator<QueueDelivery, void, void> {
    try {
      while (true) {
        while (this.items.length === 0) {
          if (signal.aborted) {
            return;
          }
          await new Promise<void>((resolve) => {
            this.wake = resolve;
            signal.addEventListener("abort", () => resolve(), { once: true });
          });
        }
        if (signal.aborted) {
          return;
        }
        const item = this.items.shift()!;
        if (item instanceof Error) {
          throw item;
        }
        yield item;
      }
    } finally {
      this.closed = true;
    }
  }
}

class FakeClient implements QueueCapableClient {
  readonly opened: ConsumeOptions[] = [];
  constructor(private readonly streams: FakeStream[]) {}

  consume(queueId: string, options: ConsumeOptions): AsyncIterable<QueueDelivery> {
    expect(queueId).toBe("q");
    this.opened.push(options);
    const stream = this.streams[Math.min(this.opened.length, this.streams.length) - 1];
    return stream.read(options.signal!);
  }
}

async function waitUntil(condition: () => boolean, timeoutMs = 2000): Promise<void> {
  const start = Date.now();
  while (!condition()) {
    if (Date.now() - start > timeoutMs) {
      throw new Error("condition not met");
    }
    await new Promise((r) => setTimeout(r, 5));
  }
}

function makeConsumer(
  client: FakeClient,
  options: ConstructorParameters<typeof QueueConsumer>[2],
  handler: (ctx: QueueMessageContext, signal: AbortSignal) => Promise<void>,
): { consumer: QueueConsumer; delays: number[] } {
  const consumer = new QueueConsumer(client, "q", options, handler);
  const delays: number[] = [];
  consumer.delay = async (ms) => {
    delays.push(ms);
    await new Promise((r) => setTimeout(r, 0));
  };
  return { consumer, delays };
}

const noop = async () => {};
const boom = async () => {
  throw new Error("boom");
};

describe("QueueConsumer", () => {
  it("opens the stream with the defaults", async () => {
    const client = new FakeClient([new FakeStream()]);
    const { consumer } = makeConsumer(client, {}, noop);

    consumer.start();
    await waitUntil(() => client.opened.length === 1);
    await consumer.stop();

    const { prefetchCount, lockTtlMs, allowCompetingConsumers } = client.opened[0];
    expect([prefetchCount, lockTtlMs, allowCompetingConsumers]).toEqual([100, 30_000, true]);
  });

  it("acks on success and passes the delivery to the handler", async () => {
    const stream = new FakeStream();
    const contexts: QueueMessageContext[] = [];
    const { consumer } = makeConsumer(new FakeClient([stream]), {}, async (ctx) => {
      contexts.push(ctx);
    });

    consumer.start();
    stream.deliver("L1", 3);
    await waitUntil(() => stream.settled.length === 1);
    await consumer.stop();

    expect(stream.settled).toEqual([["ack", "L1"]]);
    expect(contexts).toEqual([{ queueId: "q", lockId: "L1", item: { n: 1 }, priority: 1, deliveryCount: 3 }]);
  });

  it("nacks a failed message by default", async () => {
    const stream = new FakeStream();
    const { consumer } = makeConsumer(new FakeClient([stream]), {}, boom);

    consumer.start();
    stream.deliver("L1");
    await waitUntil(() => stream.settled.length === 1);
    await consumer.stop();

    expect(stream.settled).toEqual([["nack", "L1"]]);
  });

  it("dead-letters a failed message when configured", async () => {
    const stream = new FakeStream();
    const { consumer } = makeConsumer(new FakeClient([stream]), { onHandlerError: "deadLetter" }, boom);

    consumer.start();
    stream.deliver("L1");
    await waitUntil(() => stream.settled.length === 1);
    await consumer.stop();

    expect(stream.settled).toEqual([["deadLetter", "L1"]]);
  });

  it("paces nacks at maxRetriableErrorsPerSec", async () => {
    const stream = new FakeStream();
    const { consumer, delays } = makeConsumer(new FakeClient([stream]), { maxRetriableErrorsPerSec: 10, strictOrder: true }, boom);

    consumer.start();
    for (const lockId of ["L1", "L2", "L3"]) {
      stream.deliver(lockId);
    }
    await waitUntil(() => stream.settled.length === 3);
    await consumer.stop();

    // The first nack goes straight away; each later one waits for its own slot, 100 ms after the
    // previous one (the fake delay doesn't pass that time).
    expect(delays).toHaveLength(2);
    expect(delays[0]).toBeGreaterThanOrEqual(50);
    expect(delays[0]).toBeLessThanOrEqual(100);
    expect(delays[1]).toBeGreaterThanOrEqual(150);
    expect(delays[1]).toBeLessThanOrEqual(200);
  });

  it("never exceeds maxConcurrentHandlers", async () => {
    const stream = new FakeStream();
    let running = 0;
    let peak = 0;
    const { consumer } = makeConsumer(new FakeClient([stream]), { maxConcurrentHandlers: 2 }, async () => {
      peak = Math.max(peak, ++running);
      await new Promise((r) => setTimeout(r, 30));
      running--;
    });

    consumer.start();
    for (let i = 0; i < 8; i++) {
      stream.deliver(`L${i}`);
    }
    await waitUntil(() => stream.settled.length === 8);
    await consumer.stop();

    expect(peak).toBe(2);
  });

  it("strictOrder opens a window of one without competing consumers and handles one at a time", async () => {
    const stream = new FakeStream();
    const client = new FakeClient([stream]);
    let running = 0;
    let peak = 0;
    const { consumer } = makeConsumer(client, { strictOrder: true }, async () => {
      peak = Math.max(peak, ++running);
      await new Promise((r) => setTimeout(r, 10));
      running--;
    });

    consumer.start();
    for (let i = 0; i < 4; i++) {
      stream.deliver(`L${i}`);
    }
    await waitUntil(() => stream.settled.length === 4);
    await consumer.stop();

    expect([client.opened[0].prefetchCount, client.opened[0].allowCompetingConsumers]).toEqual([1, false]);
    expect(peak).toBe(1);
    expect(stream.settled.map(([, lockId]) => lockId)).toEqual(["L0", "L1", "L2", "L3"]);
  });

  it("stop lets the running handler ack before closing the stream, and starts no more", async () => {
    const stream = new FakeStream();
    const started: string[] = [];
    let release!: () => void;
    const released = new Promise<void>((r) => (release = r));
    const { consumer } = makeConsumer(new FakeClient([stream]), { maxConcurrentHandlers: 1 }, async (ctx) => {
      started.push(ctx.lockId);
      await released;
    });

    consumer.start();
    stream.deliver("L1");
    stream.deliver("L2"); // prefetched; waits for the one handler slot
    await waitUntil(() => started.length === 1);

    const stopping = consumer.stop();
    await new Promise((r) => setTimeout(r, 50));
    expect(stream.closed).toBe(false); // the stream must stay open while a handler runs
    release();
    await stopping;

    expect(started).toEqual(["L1"]);
    expect(stream.settled).toEqual([["ack", "L1"]]);
    expect(stream.closed).toBe(true);
  });

  it("stop aborts handlers once drainTimeoutMs runs out", async () => {
    const stream = new FakeStream();
    let aborted = false;
    const { consumer } = makeConsumer(new FakeClient([stream]), { drainTimeoutMs: 100 }, (_ctx, signal) =>
      new Promise<void>((_, reject) =>
        signal.addEventListener("abort", () => {
          aborted = true;
          reject(new Error("aborted"));
        }),
      ),
    );

    consumer.start();
    stream.deliver("L1");
    await new Promise((r) => setTimeout(r, 50));
    await consumer.stop();

    expect(aborted).toBe(true);
    expect(stream.settled).toEqual([]); // left unsettled: the server returns it when the stream closes
  });

  it("reopens a broken stream, backing off until a delivery resets it", async () => {
    const [broken1, broken2, delivering, broken3, last] = [new FakeStream(), new FakeStream(), new FakeStream(), new FakeStream(), new FakeStream()];
    broken1.break();
    broken2.break();
    delivering.deliver("L1");
    const client = new FakeClient([broken1, broken2, delivering, broken3, last]);
    const { consumer, delays } = makeConsumer(client, { minBackoffSeconds: 1, maxBackoffSeconds: 60 }, noop);

    consumer.start();
    await waitUntil(() => delivering.settled.length === 1);
    delivering.break();
    await waitUntil(() => client.opened.length === 4);
    broken3.break();
    await waitUntil(() => client.opened.length === 5);
    await consumer.stop();

    expect(delays).toEqual([1000, 2000, 1000, 2000]);
  });
});
