import { describe, expect, it } from "vitest";
import { DaprMQClient } from "../src/client.js";
import { DaprMQError, LockNotFoundError, StreamClosedError, ValidationError } from "../src/errors.js";
import { FakeDuplexStream, fakeGrpcClient } from "./fakeGrpcClient.js";
import type { ConsumeResponseMessage } from "../src/grpc/daprmqGrpcClient.js";
import type { QueueDelivery } from "../src/types.js";

function createClient(stream: FakeDuplexStream): DaprMQClient {
  return new DaprMQClient({ httpBaseUrl: "http://localhost:5000", grpcClient: fakeGrpcClient(stream) });
}

function delivered(lockId: string, deliveryCount = 1): ConsumeResponseMessage {
  return {
    payload: "delivered",
    delivered: { lockId, itemJson: '{"task":"x"}', priority: 1, lockExpiresAt: 123.0, deliveryCount },
  };
}

describe("DaprMQClient.consume", () => {
  it("yields a delivered item and ack() writes an ack frame", async () => {
    const stream = new FakeDuplexStream();
    stream.emitData(delivered("L1", 2));
    stream.emitEnd();

    const deliveries: QueueDelivery[] = [];
    for await (const delivery of createClient(stream).consume("q")) {
      deliveries.push(delivery);
      await delivery.ack();
    }

    expect(deliveries).toHaveLength(1);
    const d = deliveries[0];
    expect([d.lockId, d.item, d.priority, d.lockExpiresAt, d.deliveryCount]).toEqual(["L1", { task: "x" }, 1, 123.0, 2]);
    expect(stream.written).toHaveLength(2); // Start, then Ack
    expect(stream.written[1].ack?.lockId).toBe("L1");
  });

  it("sends the defaults on the start frame", async () => {
    const stream = new FakeDuplexStream();
    stream.emitEnd();

    for await (const _ of createClient(stream).consume("orders")) {
      // drain
    }

    expect(stream.written[0].start).toEqual({ queueId: "orders", prefetchCount: 1, lockTtlSeconds: 30, allowCompetingConsumers: false });
  });

  it("sends the options on the start frame with the lock TTL rounded up", async () => {
    const stream = new FakeDuplexStream();
    stream.emitEnd();

    for await (const _ of createClient(stream).consume("orders", { prefetchCount: 50, lockTtlMs: 10_200, allowCompetingConsumers: true })) {
      // drain
    }

    expect(stream.written[0].start).toEqual({ queueId: "orders", prefetchCount: 50, lockTtlSeconds: 11, allowCompetingConsumers: true });
  });

  it("nack() and deadLetter() write their frames", async () => {
    const stream = new FakeDuplexStream();
    stream.emitData(delivered("L1"));
    stream.emitData(delivered("L2"));
    stream.emitEnd();

    for await (const delivery of createClient(stream).consume("q", { prefetchCount: 2 })) {
      await (delivery.lockId === "L1" ? delivery.nack() : delivery.deadLetter());
    }

    expect(stream.written[1].nack?.lockId).toBe("L1");
    expect(stream.written[2].deadLetter?.lockId).toBe("L2");
  });

  it("sends a settleFailed frame to onSettleFailed and carries on", async () => {
    const stream = new FakeDuplexStream();
    stream.emitData({ payload: "settleFailed", settleFailed: { lockId: "L0", errorCode: "LOCK_NOT_FOUND", message: "gone" } });
    stream.emitData(delivered("L1"));
    stream.emitEnd();
    const failures: [string, DaprMQError][] = [];

    const seen: string[] = [];
    for await (const delivery of createClient(stream).consume("q", { onSettleFailed: (lockId, err) => failures.push([lockId, err]) })) {
      seen.push(delivery.lockId);
    }

    expect(seen).toEqual(["L1"]);
    expect(failures).toHaveLength(1);
    expect(failures[0][0]).toBe("L0");
    expect(failures[0][1]).toBeInstanceOf(LockNotFoundError);
  });

  it("throws the mapped error on an error frame", async () => {
    const stream = new FakeDuplexStream();
    stream.emitData({ payload: "error", error: { errorCode: "INVALID_ARGUMENT", message: "bad start" } });
    stream.emitEnd();

    await expect(async () => {
      for await (const _ of createClient(stream).consume("q")) {
        // drain
      }
    }).rejects.toBeInstanceOf(ValidationError);
  });

  it("rejects a settle after the stream ended with StreamClosedError", async () => {
    const stream = new FakeDuplexStream();
    stream.emitData(delivered("L1"));
    stream.emitEnd();

    const kept: QueueDelivery[] = [];
    for await (const delivery of createClient(stream).consume("q")) {
      kept.push(delivery);
    }

    await expect(kept[0].ack()).rejects.toBeInstanceOf(StreamClosedError);
  });

  it("breaking after an ack half-closes and waits for the server before cancelling", async () => {
    const stream = new FakeDuplexStream(100);
    stream.emitData(delivered("L1"));

    for await (const delivery of createClient(stream).consume("q")) {
      await delivery.ack();
      break;
    }

    expect(stream.written[1].ack?.lockId).toBe("L1");
    expect(stream.ended).toBe(true);
    expect(stream.cancelledBeforeServerEnded).toBe(false);
  });

  it("aborting the signal hands out nothing more and waits for the server", async () => {
    const stream = new FakeDuplexStream();
    stream.onHalfClose = () => {
      stream.emitData(delivered("L2"));
      stream.emitEnd();
    };
    stream.emitData(delivered("L1"));
    const abort = new AbortController();

    const seen: string[] = [];
    for await (const delivery of createClient(stream).consume("q", { signal: abort.signal })) {
      seen.push(delivery.lockId);
      abort.abort();
    }

    expect(seen).toEqual(["L1"]);
    expect(stream.cancelledBeforeServerEnded).toBe(false);
  });

  it("cancels the call if the server never ends it after the half-close", async () => {
    const original = DaprMQClient.sessionDrainTimeoutMs;
    DaprMQClient.sessionDrainTimeoutMs = 100;
    try {
      const stream = new FakeDuplexStream(); // never ends on its own
      const abort = new AbortController();
      abort.abort();

      for await (const _ of createClient(stream).consume("q", { signal: abort.signal })) {
        // nothing
      }

      expect(stream.ended).toBe(true);
      expect(stream.cancelled).toBe(true);
    } finally {
      DaprMQClient.sessionDrainTimeoutMs = original;
    }
  });
});
