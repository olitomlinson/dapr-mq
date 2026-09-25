import { randomUUID } from "node:crypto";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { DaprMQClient } from "../../src/client.js";
import { SessionQueueConsumer } from "../../src/sessionQueueConsumer.js";
import { dockerAvailable, startDaprMQServer, type DaprMQServer } from "./daprmqServer.js";

const ITEMS_PER_SESSION = 5;
const SLOW_HANDLER_MS = 300;

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

describe.skipIf(!dockerAvailable())("SessionQueueConsumer (integration)", () => {
  let server: DaprMQServer;

  beforeAll(async () => {
    server = await startDaprMQServer();
  }, 180_000);

  afterAll(async () => {
    await server?.stop();
  }, 60_000);

  it("K02_MultiSession_PreservesPerSessionOrder_AndIsolatesThroughput", async () => {
    const queueId = `ts-it-${randomUUID().replaceAll("-", "")}`;
    const client = new DaprMQClient({ httpBaseUrl: server.httpUrl, grpcAddress: server.grpcAddress });

    try {
      for (let seq = 1; seq <= ITEMS_PER_SESSION; seq++) {
        for (const sessionId of ["fast", "slow"]) {
          await client.enqueue(queueId, [{ item: { sessionId, seq }, priority: 1, sessionId }]);
        }
      }

      const observed: Record<string, number[]> = { fast: [], slow: [] };
      const started = Date.now();
      let fastCompletedMs: number | undefined;
      let resolveDone!: () => void;
      const allDone = new Promise<void>((resolve) => (resolveDone = resolve));

      const consumer = new SessionQueueConsumer(
        client,
        queueId,
        { maxConcurrentSessions: 2, leaseSeconds: 30, prefetchCount: 10, minBackoffSeconds: 1, maxBackoffSeconds: 2 },
        async (ctx) => {
          if (ctx.sessionId === "slow") {
            await sleep(SLOW_HANDLER_MS);
          }
          observed[ctx.sessionId]!.push((ctx.item as { seq: number }).seq);
          if (ctx.sessionId === "fast" && observed.fast!.length === ITEMS_PER_SESSION) {
            fastCompletedMs = Date.now() - started;
          }
          if (observed.fast!.length === ITEMS_PER_SESSION && observed.slow!.length === ITEMS_PER_SESSION) {
            resolveDone();
          }
        },
      );

      consumer.start();
      try {
        await Promise.race([
          allDone,
          sleep(30_000).then(() => {
            throw new Error("timed out waiting for both sessions to drain");
          }),
        ]);
      } finally {
        await consumer.stop();
      }

      const expected = Array.from({ length: ITEMS_PER_SESSION }, (_, i) => i + 1);
      expect(observed.fast).toEqual(expected);
      expect(observed.slow).toEqual(expected);

      // "slow" needs >= ITEMS_PER_SESSION * 300ms (sequential within its own stream); "fast" must
      // finish well inside that, proving the sessions run on independent streams/slots.
      const slowFloorMs = ITEMS_PER_SESSION * SLOW_HANDLER_MS;
      expect(fastCompletedMs).toBeDefined();
      expect(fastCompletedMs!).toBeLessThan(slowFloorMs);
    } finally {
      client.close();
    }
  }, 60_000);
});
