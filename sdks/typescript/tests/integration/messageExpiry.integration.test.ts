import { randomUUID } from "node:crypto";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { DaprMQClient } from "../../src/client.js";
import { dockerAvailable, startDaprMQServer, type DaprMQServer } from "./daprmqServer.js";

// E-01..E-03 from sdks/testing/INTEGRATION_TESTS.md: message expiry. The stack sets
// DAPRMQ_MIN_EXPIRY_TTL_SECONDS to 1 so an item can expire within a test.

// Server default, DAPRMQ_DEADLETTER_EXPIRY_TTL_SECONDS unset.
const DEAD_LETTER_EXPIRY_TTL_SECONDS = 2_419_200;

const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/** Allows for the API server stamping the expiry slightly after the test read its clock. */
function expectNear(expiryAt: number | undefined, want: number): void {
  expect(expiryAt).toBeGreaterThanOrEqual(want - 5);
  expect(expiryAt).toBeLessThanOrEqual(want + 30);
}

describe.skipIf(!dockerAvailable())("Message expiry (integration)", () => {
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

  it("E01_ExpiredItem_IsNotDelivered", async () => {
    const queueId = newQueueId();
    await client.enqueue(queueId, [{ item: { seq: 1 }, expiryTtlSeconds: 1 }]);

    await sleep(2500);

    expect(await client.dequeueLocked(queueId)).toBeNull();
  });

  it("E02_DequeuedAndConsumedItems_CarryTheirExpiry", async () => {
    const queueId = newQueueId();
    const before = Date.now() / 1000;
    await client.enqueue(queueId, [
      { item: { seq: 1 }, expiryTtlSeconds: 3600 },
      { item: { seq: 2 }, expiryTtlSeconds: 7200 },
    ]);

    const [locked] = (await client.dequeueLocked(queueId))!.items;
    expect(locked.expiryTtlSeconds).toBe(3600);
    expectNear(locked.expiryAt, before + 3600);
    expect(locked.originalExpiryTtlSeconds).toBeUndefined();
    await client.acknowledge(queueId, locked.lockId);

    for await (const delivery of client.consume(queueId)) {
      expect(delivery.expiryTtlSeconds).toBe(7200);
      expectNear(delivery.expiryAt, before + 7200);
      await delivery.ack();
      break;
    }
  });

  it("E03_DeadLetteredItem_CarriesTheOriginalTimeToLive", async () => {
    const queueId = newQueueId();
    await client.enqueue(queueId, [{ item: { seq: 1 }, expiryTtlSeconds: 3600 }]);
    const [locked] = (await client.dequeueLocked(queueId))!.items;
    await client.deadLetter(queueId, locked.lockId);

    const [dead] = (await client.dequeueLocked(`${queueId}-deadletter`))!.items;

    expect(dead.expiryTtlSeconds).toBe(DEAD_LETTER_EXPIRY_TTL_SECONDS);
    expect(dead.originalExpiryTtlSeconds).toBe(3600);
  });
});
