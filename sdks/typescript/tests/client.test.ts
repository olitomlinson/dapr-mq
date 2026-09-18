import { describe, expect, it } from "vitest";
import * as grpc from "@grpc/grpc-js";
import { DaprMQClient } from "../src/client.js";
import {
  InvalidLeaseIdError,
  LockExpiredError,
  LockNotFoundError,
  SessionActorUnavailableError,
  SessionLeaseExpiredError,
  SessionLockedError,
  SessionNotFoundError,
  ValidationError,
} from "../src/errors.js";
import { fakeFetch } from "./fakeFetch.js";
import type { DaprMQGrpcClient } from "../src/grpc/daprmqGrpcClient.js";

const NOOP_GRPC_CLIENT = {} as DaprMQGrpcClient;

function createClient(fetchImpl: typeof fetch): DaprMQClient {
  return new DaprMQClient({ httpBaseUrl: "http://localhost:5000", fetch: fetchImpl, grpcClient: NOOP_GRPC_CLIENT });
}

describe("DaprMQClient.enqueue", () => {
  it("maps a successful response", async () => {
    const fake = fakeFetch(200, { success: true, message: "Enqueued 1 items", itemsEnqueued: 1, itemsDeduplicated: 0 });
    const client = createClient(fake.fetch);

    const result = await client.enqueue("my-queue", [{ item: { task: "send_email" }, priority: 0, sessionId: "order-42" }]);

    expect(result.success).toBe(true);
    expect(result.itemsEnqueued).toBe(1);
    expect(fake.lastRequest!.method).toBe("POST");
    expect(fake.lastRequest!.url).toBe("http://localhost:5000/queue/my-queue/enqueue");
    expect(fake.lastRequest!.body).toContain('"sessionId":"order-42"');
    expect(fake.lastRequest!.body).toContain('"priority":0');
  });

  it("throws ValidationError on 400", async () => {
    const fake = fakeFetch(400, { message: "bad item", success: false });
    const client = createClient(fake.fetch);

    await expect(client.enqueue("q", [{ item: {} }])).rejects.toBeInstanceOf(ValidationError);
  });
});

describe("DaprMQClient.dequeueLocked", () => {
  it("sends headers and maps items", async () => {
    const fake = fakeFetch(200, {
      items: [{ item: { task: "x" }, priority: 0, lockId: "L1", lockExpiresAt: 123.0 }],
      locked: false,
    });
    const client = createClient(fake.fetch);

    const result = await client.dequeueLocked("q", { count: 5, ttlSeconds: 60, leaseId: "lease-1" });

    expect(result?.items).toHaveLength(1);
    expect(result?.items[0].lockId).toBe("L1");
    expect(fake.lastRequest!.headers["require-ack"]).toBe("true");
    expect(fake.lastRequest!.headers["count"]).toBe("5");
    expect(fake.lastRequest!.headers["ttl-seconds"]).toBe("60");
    expect(fake.lastRequest!.headers["lease-id"]).toBe("lease-1");
  });

  it("returns null on 204", async () => {
    const fake = fakeFetch(204);
    const client = createClient(fake.fetch);

    expect(await client.dequeueLocked("q")).toBeNull();
  });

  it("returns a locked result on 423", async () => {
    const fake = fakeFetch(423, { message: "locked", lockExpiresAt: 999.0 });
    const client = createClient(fake.fetch);

    const result = await client.dequeueLocked("q");

    expect(result?.locked).toBe(true);
    expect(result?.items).toHaveLength(0);
  });

  it("throws SessionLeaseExpiredError on 410", async () => {
    const fake = fakeFetch(410, { message: "lease expired", success: false });
    const client = createClient(fake.fetch);

    await expect(client.dequeueLocked("q", { leaseId: "stale" })).rejects.toBeInstanceOf(SessionLeaseExpiredError);
  });
});

describe("DaprMQClient.acknowledge", () => {
  it("sends the lease-id header on success", async () => {
    const fake = fakeFetch(200, { success: true, message: "ok", itemsAcknowledged: 1 });
    const client = createClient(fake.fetch);

    await client.acknowledge("q", "L1", { leaseId: "lease-1" });

    expect(fake.lastRequest!.headers["lease-id"]).toBe("lease-1");
    expect(fake.lastRequest!.body).toContain('"lockId":"L1"');
  });

  it.each([
    ["LOCK_NOT_FOUND", LockNotFoundError],
    ["LOCK_EXPIRED", LockExpiredError],
    ["SESSION_LEASE_EXPIRED", SessionLeaseExpiredError],
    ["INVALID_LEASE_ID", InvalidLeaseIdError],
  ] as const)("maps errorCode %s to %s", async (errorCode, expectedError) => {
    const fake = fakeFetch(400, { success: false, message: "failed", errorCode });
    const client = createClient(fake.fetch);

    await expect(client.acknowledge("q", "L1")).rejects.toBeInstanceOf(expectedError);
  });
});

describe("DaprMQClient.extendLock", () => {
  it("sends the additional TTL in the body", async () => {
    const fake = fakeFetch(200, { newExpiresAt: 123, lockId: "L1" });
    const client = createClient(fake.fetch);

    await client.extendLock("q", "L1", 30);

    expect(fake.lastRequest!.body).toContain('"additionalTtlSeconds":30');
  });

  it("throws LockExpiredError on 410", async () => {
    const fake = fakeFetch(410, { message: "expired", success: false });
    const client = createClient(fake.fetch);

    await expect(client.extendLock("q", "L1", 30)).rejects.toBeInstanceOf(LockExpiredError);
  });
});

describe("DaprMQClient.deadLetter", () => {
  it("resolves on success", async () => {
    const fake = fakeFetch(200, { success: true, message: "moved", dlqId: "q-deadletter" });
    const client = createClient(fake.fetch);

    await expect(client.deadLetter("q", "L1")).resolves.toBeUndefined();
  });

  it("maps errorCode to a typed exception", async () => {
    const fake = fakeFetch(404, { success: false, message: "not found", errorCode: "LOCK_NOT_FOUND" });
    const client = createClient(fake.fetch);

    await expect(client.deadLetter("q", "L1")).rejects.toBeInstanceOf(LockNotFoundError);
  });
});

describe("DaprMQClient sessions", () => {
  it("acceptSession maps a successful response", async () => {
    const fake = fakeFetch(200, { sessionId: "order-42", leaseId: "lease-1", leaseExpiresAt: 1780000200.0 });
    const client = createClient(fake.fetch);

    const lease = await client.acceptSession("q", { sessionId: "order-42", leaseSeconds: 30 });

    expect(lease?.sessionId).toBe("order-42");
    expect(lease?.leaseId).toBe("lease-1");
    expect(fake.lastRequest!.url).toBe("http://localhost:5000/queue/q/sessions/accept");
  });

  it("acceptSession returns null on 204", async () => {
    const fake = fakeFetch(204);
    const client = createClient(fake.fetch);

    expect(await client.acceptSession("q")).toBeNull();
  });

  it.each([
    [404, SessionNotFoundError],
    [423, SessionLockedError],
    [502, SessionActorUnavailableError],
    [400, ValidationError],
  ] as const)("acceptSession maps status %d to %s", async (status, expectedError) => {
    const fake = fakeFetch(status, { message: "failed", success: false });
    const client = createClient(fake.fetch);

    await expect(client.acceptSession("q", { sessionId: "order-42" })).rejects.toBeInstanceOf(expectedError);
  });

  it("renewSessionLease succeeds", async () => {
    const fake = fakeFetch(200, { newExpiresAt: 1780000230.0 });
    const client = createClient(fake.fetch);

    const lease = await client.renewSessionLease("q", "order-42", "lease-1", { additionalSeconds: 30 });

    expect(lease.leaseExpiresAt).toBe(1780000230.0);
    expect(fake.lastRequest!.url).toBe("http://localhost:5000/queue/q/sessions/order-42/renew");
  });

  it("renewSessionLease throws SessionLeaseExpiredError on 410", async () => {
    const fake = fakeFetch(410, { message: "expired", success: false });
    const client = createClient(fake.fetch);

    await expect(client.renewSessionLease("q", "order-42", "lease-1")).rejects.toBeInstanceOf(SessionLeaseExpiredError);
  });

  it("releaseSession succeeds", async () => {
    const fake = fakeFetch(200, { success: true });
    const client = createClient(fake.fetch);

    await client.releaseSession("q", "order-42", "lease-1");

    expect(fake.lastRequest!.url).toBe("http://localhost:5000/queue/q/sessions/order-42/release");
  });

  it("releaseSession throws InvalidLeaseIdError on 400", async () => {
    const fake = fakeFetch(400, { message: "bad lease", success: false });
    const client = createClient(fake.fetch);

    await expect(client.releaseSession("q", "order-42", "wrong")).rejects.toBeInstanceOf(InvalidLeaseIdError);
  });
});

describe("DaprMQClient constructor", () => {
  it("requires grpcAddress unless grpcClient is supplied", () => {
    expect(() => new DaprMQClient({ httpBaseUrl: "http://localhost:5000" })).toThrow(/grpcAddress/);
  });

  it("accepts insecure credentials via grpcAddress without throwing", () => {
    const client = new DaprMQClient({
      httpBaseUrl: "http://localhost:5000",
      grpcAddress: "localhost:5001",
      grpcCredentials: grpc.credentials.createInsecure(),
    });
    client.close();
  });
});
