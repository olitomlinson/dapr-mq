// R-01, R-04 and R-05 from sdks/testing/RETRIES_AND_READINESS.md against a real server.
// (R-02/R-03 need a split gateway/worker stack, which only the .NET fixture builds today.)
import { randomUUID } from "node:crypto";
import { createServer } from "node:net";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { DaprMQClient } from "../../src/client.js";
import { DaprMQUnavailableError } from "../../src/errors.js";
import { dockerAvailable, startDaprMQServer, type DaprMQServer } from "./daprmqServer.js";

const newQueueId = () => `ts-it-${randomUUID().replaceAll("-", "")}`;

/** A local port nothing listens on (bound, then closed), so connections are refused immediately.
 * Not port 1: fetch rejects it as a "bad port" before connecting at all. */
async function closedPort(): Promise<number> {
  const server = createServer();
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as { port: number };
  await new Promise<void>((resolve) => server.close(() => resolve()));
  return port;
}

describe.skipIf(!dockerAvailable())("retries and readiness (integration)", () => {
  let server: DaprMQServer;

  beforeAll(async () => {
    server = await startDaprMQServer();
  }, 180_000);

  afterAll(async () => {
    await server?.stop();
  }, 60_000);

  it("R01_WaitForReady_RunningStack_ReturnsAndEnqueueSucceeds", async () => {
    const client = new DaprMQClient({ httpBaseUrl: server.httpUrl, grpcAddress: server.grpcAddress });
    try {
      await client.waitForReady({ signal: AbortSignal.timeout(30_000) });

      const result = await client.enqueue(newQueueId(), [{ item: { seq: 1 } }]);
      expect(result.itemsEnqueued).toBe(1);
    } finally {
      client.close();
    }
  });

  it("R04_ServerUnreachable_ThrowsUnavailable_NotAHang", async () => {
    const port = await closedPort();
    const client = new DaprMQClient({ httpBaseUrl: `http://127.0.0.1:${port}`, grpcAddress: `127.0.0.1:${port}`, retry: { timeoutMs: 2000 } });
    try {
      const started = Date.now();
      const error = await client.enqueue(newQueueId(), [{ item: { seq: 1 } }]).catch((e) => e);

      expect(error).toBeInstanceOf(DaprMQUnavailableError);
      expect(error.operation).toBe("enqueue");
      expect(Date.now() - started).toBeLessThan(10_000);
    } finally {
      client.close();
    }
  });

  it("R05_AutoIdempotencyKeys_FillsMissingKeys_AndKeepsGivenOnes", async () => {
    const client = new DaprMQClient({ httpBaseUrl: server.httpUrl, grpcAddress: server.grpcAddress, retry: { autoIdempotencyKeys: true } });
    try {
      const queueId = newQueueId();
      const items = [{ item: { seq: 1 }, idempotencyKey: `mine-${randomUUID()}` }, { item: { seq: 2 } }];

      await client.enqueue(queueId, items);
      const second = await client.enqueue(queueId, items);

      // The caller's key is kept (the repeat is de-duplicated); the unkeyed item gets a fresh key per call.
      expect(second.itemsDeduplicated).toBe(1);
      expect(second.itemsEnqueued).toBe(1);
    } finally {
      client.close();
    }
  });
});
