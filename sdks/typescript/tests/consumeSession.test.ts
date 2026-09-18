import { describe, expect, it } from "vitest";
import { DaprMQClient } from "../src/client.js";
import { NoSessionsAvailableError, SessionLostError } from "../src/errors.js";
import { FakeDuplexStream, fakeGrpcClient } from "./fakeGrpcClient.js";
import type { SessionDelivery } from "../src/types.js";

function createClient(stream: FakeDuplexStream): DaprMQClient {
  return new DaprMQClient({ httpBaseUrl: "http://localhost:5000", grpcClient: fakeGrpcClient(stream) });
}

describe("DaprMQClient.consumeSession", () => {
  it("yields a delivered item and ack() writes an ack frame", async () => {
    const stream = new FakeDuplexStream();
    const client = createClient(stream);

    stream.emitData({ payload: "sessionAssigned", sessionAssigned: { sessionId: "order-42", leaseExpiresAt: 1780000200.0 } });
    stream.emitData({ payload: "delivered", delivered: { lockId: "L1", itemJson: '{"task":"x"}', priority: 0, lockExpiresAt: 123.0 } });
    stream.emitEnd();

    const deliveries: SessionDelivery[] = [];
    for await (const delivery of client.consumeSession("q", {})) {
      deliveries.push(delivery);
      await delivery.ack();
    }

    expect(deliveries).toHaveLength(1);
    expect(deliveries[0].sessionId).toBe("order-42");
    expect(deliveries[0].lockId).toBe("L1");
    expect(deliveries[0].item).toEqual({ task: "x" });

    expect(stream.written).toHaveLength(2); // Start, then Ack
    expect(stream.written[0].start).toBeDefined();
    expect(stream.written[1].ack?.lockId).toBe("L1");
  });

  it("throws the mapped exception on an error frame", async () => {
    const stream = new FakeDuplexStream();
    const client = createClient(stream);

    stream.emitData({ payload: "error", error: { errorCode: "NO_SESSIONS_AVAILABLE", message: "none free" } });
    stream.emitEnd();

    await expect(async () => {
      for await (const _ of client.consumeSession("q", {})) {
        // drain
      }
    }).rejects.toBeInstanceOf(NoSessionsAvailableError);
  });

  it("throws SessionLostError on a sessionLost frame", async () => {
    const stream = new FakeDuplexStream();
    const client = createClient(stream);

    stream.emitData({ payload: "sessionAssigned", sessionAssigned: { sessionId: "order-42", leaseExpiresAt: 1780000200.0 } });
    stream.emitData({ payload: "sessionLost", sessionLost: { message: "lease lost" } });
    stream.emitEnd();

    await expect(async () => {
      for await (const _ of client.consumeSession("q", {})) {
        // drain
      }
    }).rejects.toBeInstanceOf(SessionLostError);
  });

  it("deadLetter() writes a deadLetter frame", async () => {
    const stream = new FakeDuplexStream();
    const client = createClient(stream);

    stream.emitData({ payload: "sessionAssigned", sessionAssigned: { sessionId: "s1", leaseExpiresAt: 1.0 } });
    stream.emitData({ payload: "delivered", delivered: { lockId: "L2", itemJson: "{}", priority: 1, lockExpiresAt: 1.0 } });
    stream.emitEnd();

    for await (const delivery of client.consumeSession("q", { sessionId: "s1" })) {
      await delivery.deadLetter();
    }

    expect(stream.written[1].deadLetter?.lockId).toBe("L2");
  });
});
