import { afterEach, describe, expect, it } from "vitest";
import { DaprMQClient } from "../src/client.js";
import { DaprMQError, NoSessionsAvailableError, SessionLostError } from "../src/errors.js";
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

  it("ends enumeration cleanly (no throw) on a sessionDrained frame", async () => {
    const stream = new FakeDuplexStream();
    const client = createClient(stream);

    stream.emitData({ payload: "sessionAssigned", sessionAssigned: { sessionId: "order-42", leaseExpiresAt: 1780000200.0 } });
    stream.emitData({ payload: "sessionDrained", sessionDrained: { sessionId: "order-42" } });
    stream.emitEnd();

    const deliveries: SessionDelivery[] = [];
    for await (const delivery of client.consumeSession("q", {})) {
      deliveries.push(delivery);
    }

    expect(deliveries).toHaveLength(0);
  });

  it("forwards sessionIdleTimeoutSeconds on the start frame", async () => {
    const stream = new FakeDuplexStream();
    const client = createClient(stream);
    stream.emitEnd();

    for await (const _ of client.consumeSession("q", { sessionIdleTimeoutSeconds: 5 })) {
      // drain
    }

    expect(stream.written[0].start?.sessionIdleTimeoutSeconds).toBe(5);
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

  it("nack() writes a nack frame", async () => {
    const stream = new FakeDuplexStream();
    const client = createClient(stream);

    stream.emitData({ payload: "sessionAssigned", sessionAssigned: { sessionId: "s1", leaseExpiresAt: 1.0 } });
    stream.emitData({ payload: "delivered", delivered: { lockId: "L3", itemJson: "{}", priority: 1, lockExpiresAt: 1.0 } });
    stream.emitEnd();

    for await (const delivery of client.consumeSession("q", { sessionId: "s1" })) {
      await delivery.nack();
    }

    expect(stream.written[1].nack?.lockId).toBe("L3");
  });

  describe("stopping", () => {
    const delivered = (lockId: string) =>
      ({ payload: "delivered", delivered: { lockId, itemJson: "{}", priority: 0, lockExpiresAt: 1 } }) as const;
    const originalDrainTimeout = DaprMQClient.sessionDrainTimeoutMs;
    afterEach(() => {
      DaprMQClient.sessionDrainTimeoutMs = originalDrainTimeout;
    });

    it("break after acking half-closes and waits for the server before cancelling", async () => {
      const stream = new FakeDuplexStream(50);
      const client = createClient(stream);
      stream.emitData(delivered("L1"));

      for await (const delivery of client.consumeSession("q", {})) {
        await delivery.ack();
        break; // consumer shutting down
      }

      expect(stream.written[1].ack?.lockId).toBe("L1");
      expect(stream.ended).toBe(true);
      expect(stream.cancelledBeforeServerEnded).toBe(false);
    });

    it("aborting after acking half-closes and waits for the server before cancelling", async () => {
      const stream = new FakeDuplexStream(50);
      const client = createClient(stream);
      stream.emitData(delivered("L1"));
      const stop = new AbortController();

      for await (const delivery of client.consumeSession("q", { signal: stop.signal })) {
        await delivery.ack();
        stop.abort();
      }

      expect(stream.written[1].ack?.lockId).toBe("L1");
      expect(stream.ended).toBe(true);
      expect(stream.cancelledBeforeServerEnded).toBe(false);
    });

    it("cancels the call if the server never ends it after the half-close", async () => {
      DaprMQClient.sessionDrainTimeoutMs = 50;
      const stream = new FakeDuplexStream(); // never ends on its own
      const client = createClient(stream);
      const stop = new AbortController();
      stop.abort();

      for await (const _ of client.consumeSession("q", { signal: stop.signal })) {
        // nothing is handed out
      }

      expect(stream.ended).toBe(true);
      expect(stream.cancelled).toBe(true);
    });

    it("hands out nothing that arrives after the half-close", async () => {
      const stream = new FakeDuplexStream();
      stream.onHalfClose = () => {
        stream.emitData(delivered("L2"));
        stream.emitEnd();
      };
      const client = createClient(stream);
      stream.emitData(delivered("L1"));
      const stop = new AbortController();
      const seen: string[] = [];

      for await (const delivery of client.consumeSession("q", { signal: stop.signal })) {
        seen.push(delivery.lockId);
        stop.abort();
      }

      expect(seen).toEqual(["L1"]);
    });

    it("settling after the half-close rejects without writing", async () => {
      const stream = new FakeDuplexStream(0);
      const client = createClient(stream);
      stream.emitData(delivered("L1"));
      const stop = new AbortController();
      const kept: SessionDelivery[] = [];

      for await (const delivery of client.consumeSession("q", { signal: stop.signal })) {
        kept.push(delivery);
        stop.abort();
      }

      await expect(kept[0].ack()).rejects.toThrow(DaprMQError);
      expect(stream.written).toHaveLength(1); // just the Start frame
    });
  });
});
