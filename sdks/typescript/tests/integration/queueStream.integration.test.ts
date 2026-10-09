import { randomUUID } from "node:crypto";
import net from "node:net";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { DaprMQClient } from "../../src/client.js";
import { QueueConsumer, type QueueMessageContext } from "../../src/queueConsumer.js";
import type { QueueDelivery } from "../../src/types.js";
import { dockerAvailable, startDaprMQServer, type DaprMQServer } from "./daprmqServer.js";

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
const seqOf = (item: unknown) => (item as { seq: number }).seq;

async function waitFor(condition: () => boolean, because: string, timeoutMs = 30_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error(`timed out waiting for ${because}`);
    }
    await sleep(50);
  }
}

/**
 * Forwards a local port to the server so QC-06 can break every open connection without restarting
 * a container, which would re-map its host ports.
 */
class TcpProxy {
  private readonly sockets = new Set<net.Socket>();
  private readonly server: net.Server;

  constructor(target: string) {
    const [host, port] = [target.slice(0, target.lastIndexOf(":")), Number(target.slice(target.lastIndexOf(":") + 1))];
    this.server = net.createServer((inbound) => {
      const outbound = net.connect(port, host);
      for (const s of [inbound, outbound]) {
        this.sockets.add(s);
        s.on("close", () => this.sockets.delete(s));
        s.on("error", () => {});
      }
      inbound.pipe(outbound);
      outbound.pipe(inbound);
    });
  }

  async start(): Promise<string> {
    await new Promise<void>((resolve) => this.server.listen(0, "127.0.0.1", resolve));
    return `127.0.0.1:${(this.server.address() as net.AddressInfo).port}`;
  }

  /** Drops every connection open now; new ones are still accepted. */
  breakConnections(): void {
    for (const s of this.sockets) {
      s.resetAndDestroy();
    }
  }

  close(): void {
    this.breakConnections();
    this.server.close();
  }
}

describe.skipIf(!dockerAvailable())("Consume stream and QueueConsumer (integration)", () => {
  let server: DaprMQServer;
  let client: DaprMQClient;

  beforeAll(async () => {
    server = await startDaprMQServer();
    client = new DaprMQClient({ httpBaseUrl: server.httpUrl, grpcAddress: server.grpcAddress });
  }, 180_000);

  afterAll(async () => {
    client?.close();
    await server?.stop();
  }, 60_000);

  const newQueueId = () => `ts-it-${randomUUID().replaceAll("-", "")}`;
  const enqueueSeqs = (queueId: string, ...seqs: number[]) => client.enqueue(queueId, seqs.map((seq) => ({ item: { seq } })));
  const range = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => from + i);

  it("QS01_Consume_DeliversInOrder_AndAckRemovesItems", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 1, 2, 3);

    const delivered: QueueDelivery[] = [];
    for await (const delivery of client.consume(queueId, { prefetchCount: 2, allowCompetingConsumers: true })) {
      delivered.push(delivery);
      await delivery.ack();
      if (delivered.length === 3) {
        break;
      }
    }

    expect(delivered.map((d) => seqOf(d.item))).toEqual([1, 2, 3]);
    expect(delivered.map((d) => d.deliveryCount)).toEqual([1, 1, 1]);
    expect(await client.dequeueLocked(queueId)).toBeNull();
  });

  it("QS02_Consume_NackRedeliversWithTheNextDeliveryCount", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 1);

    const delivered: QueueDelivery[] = [];
    for await (const delivery of client.consume(queueId)) {
      delivered.push(delivery);
      if (delivered.length === 1) {
        await delivery.nack();
        continue;
      }
      await delivery.ack();
      break;
    }

    expect(delivered.map((d) => seqOf(d.item))).toEqual([1, 1]);
    expect(delivered.map((d) => d.deliveryCount)).toEqual([1, 2]);
  });

  it("QS03_Consume_KeepsADeliveredItemLockedPastItsTtl", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 1);
    const settleFailures: string[] = [];

    for await (const delivery of client.consume(queueId, {
      lockTtlMs: 2_000,
      allowCompetingConsumers: true,
      onSettleFailed: (lockId) => settleFailures.push(lockId),
    })) {
      await sleep(6_000);
      expect(await client.dequeueLocked(queueId, { allowCompetingConsumers: true })).toBeNull();
      await delivery.ack();
      break;
    }

    expect(settleFailures).toEqual([]);
  }, 30_000);

  it("QS04_ClosingTheStream_ReturnsUnsettledItemsStraightAway", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 1);

    for await (const _ of client.consume(queueId, { lockTtlMs: 300_000 })) {
      break;
    }

    const back = await client.dequeueLocked(queueId);
    expect(back?.items.map((i) => seqOf(i.item))).toEqual([1]);
  });

  it("QC01_HandlerSuccess_Acks_AndTheQueueEndsEmpty", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, ...range(1, 20));
    const handled: number[] = [];

    const consumer = new QueueConsumer(client, queueId, {}, async (ctx) => {
      handled.push(seqOf(ctx.item));
    });
    consumer.start();
    await waitFor(() => handled.length === 20, "every message to be handled");
    await consumer.stop();

    expect([...handled].sort((a, b) => a - b)).toEqual(range(1, 20));
    expect(await client.dequeueLocked(queueId, { allowCompetingConsumers: true })).toBeNull();
  });

  it("QC02_HandlerError_NackRedeliversWithDeliveryCount2", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 1);
    const counts: number[] = [];

    const consumer = new QueueConsumer(client, queueId, {}, async (ctx) => {
      counts.push(ctx.deliveryCount);
      if (ctx.deliveryCount === 1) {
        throw new Error("first attempt fails");
      }
    });
    consumer.start();
    await waitFor(() => counts.length === 2, "the nacked message to be redelivered");
    await consumer.stop();

    expect(counts).toEqual([1, 2]);
    expect(await client.dequeueLocked(queueId, { allowCompetingConsumers: true })).toBeNull();
  });

  it("QC02_HandlerError_DeadLetterMovesItToTheDeadLetterQueue", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 7);

    const consumer = new QueueConsumer(client, queueId, { onHandlerError: "deadLetter" }, async () => {
      throw new Error("poison");
    });
    consumer.start();
    let dead = null;
    const deadline = Date.now() + 30_000;
    while (dead === null) {
      expect(Date.now()).toBeLessThan(deadline);
      dead = await client.dequeueLocked(`${queueId}-deadletter`);
      await sleep(100);
    }
    await consumer.stop();

    expect(dead.items.map((i) => seqOf(i.item))).toEqual([7]);
  });

  it("QC03_MaxConcurrentHandlers_IsNeverExceeded", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, ...range(1, 10));
    let running = 0;
    let peak = 0;
    let handled = 0;

    const consumer = new QueueConsumer(client, queueId, { maxConcurrentHandlers: 2 }, async () => {
      peak = Math.max(peak, ++running);
      await sleep(100);
      running--;
      handled++;
    });
    consumer.start();
    await waitFor(() => handled === 10, "every message to be handled");
    await consumer.stop();

    expect(peak).toBe(2);
  });

  it("QC04_StrictOrder_HandlesInQueueOrder_IncludingAfterANack", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 1, 2, 3, 4, 5);
    const succeeded: number[] = [];
    let failedOnce = false;

    const consumer = new QueueConsumer(client, queueId, { strictOrder: true }, async (ctx: QueueMessageContext) => {
      const seq = seqOf(ctx.item);
      if (seq === 2 && !failedOnce) {
        failedOnce = true;
        throw new Error("nack 2 once");
      }
      succeeded.push(seq);
    });
    consumer.start();
    await waitFor(() => succeeded.length === 5, "every message to be handled");
    await consumer.stop();

    expect(succeeded).toEqual([1, 2, 3, 4, 5]);
  });

  it("QC05_Stop_DrainsRunningHandlers_AndReturnsUnstartedMessagesStraightAway", async () => {
    const queueId = newQueueId();
    await enqueueSeqs(queueId, 1, 2, 3);
    let started = false;
    const handled: number[] = [];

    const consumer = new QueueConsumer(client, queueId, { maxConcurrentHandlers: 1, maxActiveMessages: 10, lockTtlMs: 300_000 }, async (ctx) => {
      started = true;
      await sleep(500);
      handled.push(seqOf(ctx.item));
    });
    consumer.start();
    await waitFor(() => started, "the first handler to start");
    await consumer.stop();

    expect(handled).toEqual([1]);
    // Well inside the 300 s lock, so only the stream's close can have returned them.
    const back = await client.dequeueLocked(queueId, { count: 10, allowCompetingConsumers: true });
    expect(back?.items.map((i) => seqOf(i.item))).toEqual([2, 3]);
  });

  it("QC06_BrokenStream_Reconnects_AndEveryMessageIsHandledAtLeastOnce", async () => {
    const proxy = new TcpProxy(server.grpcAddress);
    const proxied = new DaprMQClient({ httpBaseUrl: server.httpUrl, grpcAddress: await proxy.start() });
    const queueId = newQueueId();
    await enqueueSeqs(queueId, ...range(1, 20));
    const handled = new Map<number, number>();

    try {
      const consumer = new QueueConsumer(proxied, queueId, { maxActiveMessages: 5, maxConcurrentHandlers: 2 }, async (ctx) => {
        await sleep(50);
        const seq = seqOf(ctx.item);
        handled.set(seq, (handled.get(seq) ?? 0) + 1);
      });
      consumer.start();
      await waitFor(() => handled.size >= 5, "some messages to be handled before the break");
      proxy.breakConnections();
      await waitFor(() => handled.size === 20, "every message to be handled after reconnecting");
      await consumer.stop();

      expect([...handled.keys()].sort((a, b) => a - b)).toEqual(range(1, 20));
      expect(await client.dequeueLocked(queueId, { allowCompetingConsumers: true })).toBeNull();
    } finally {
      proxied.close();
      proxy.close();
    }
  }, 60_000);
});
