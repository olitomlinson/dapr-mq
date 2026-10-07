// The shared retry contract (sdks/testing/RETRIES_AND_READINESS.md): retry what certainly wasn't
// delivered, retry an unknown outcome only for a fully keyed enqueue, within the retry timeout.
import { describe, expect, it } from "vitest";
import { DaprMQClient, type RetryOptions } from "../src/client.js";
import { DaprMQError, DaprMQUnavailableError, DeliveryUnknownError } from "../src/errors.js";
import type { DaprMQGrpcClient } from "../src/grpc/daprmqGrpcClient.js";

const OK = { success: true, message: "ok", itemsEnqueued: 1, itemsDeduplicated: 0 };
const FAST: RetryOptions = { timeoutMs: 5000, minAttemptWindowMs: 10, initialBackoffMs: 1, maxBackoffMs: 5 };

type Answer = () => Response | Promise<Response>;

const json = (status: number, body: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json", ...headers } });

const notDelivered: Answer = () =>
  json(503, { message: "unavailable", success: false, errorCode: "UNAVAILABLE" }, { "daprmq-delivery": "not-delivered" });
const unknown: Answer = () =>
  json(504, { message: "unknown", success: false, errorCode: "DELIVERY_UNKNOWN" }, { "daprmq-delivery": "unknown" });
const ok = (body: unknown = {}): Answer => () => json(200, body);

function fetchError(code: string): Error {
  return Object.assign(new TypeError("fetch failed"), { cause: Object.assign(new Error(code), { code }) });
}

/** Answers each attempt in turn; the last answer repeats. */
function sequence(...answers: Answer[]) {
  const requests: { headers: Record<string, string>; body?: string }[] = [];
  const fetchImpl = (async (_input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({ headers: { ...(init?.headers as Record<string, string>) }, body: init?.body as string | undefined });
    return answers[Math.min(requests.length, answers.length) - 1]();
  }) as typeof fetch;
  return { fetchImpl, requests };
}

const client = (fetchImpl: typeof fetch, retry: RetryOptions = FAST) =>
  new DaprMQClient({ httpBaseUrl: "http://localhost:5000", fetch: fetchImpl, grpcClient: {} as DaprMQGrpcClient, retry });

describe("retries", () => {
  it("retries not-delivered until it succeeds, for any operation", async () => {
    const { fetchImpl, requests } = sequence(notDelivered, notDelivered, ok());

    await client(fetchImpl).acknowledge("q", "L1");

    expect(requests).toHaveLength(3);
  });

  it("retries a not-delivered nack until it succeeds", async () => {
    const { fetchImpl, requests } = sequence(notDelivered, ok({ success: true, deadLettered: false, deliveryCount: 1 }));

    const result = await client(fetchImpl).nack("q", "L1");

    expect(requests).toHaveLength(2);
    expect(result.deadLettered).toBe(false);
  });

  it("never retries an unknown nack, and names the operation", async () => {
    const { fetchImpl, requests } = sequence(unknown);

    const error = await client(fetchImpl).nack("q", "L1").catch((e) => e);

    expect(error).toBeInstanceOf(DeliveryUnknownError);
    expect(error.operation).toBe("nack");
    expect(requests).toHaveLength(1);
  });

  it("sends the remaining retry time on every attempt, not a call deadline", async () => {
    const { fetchImpl, requests } = sequence(ok(OK));

    await client(fetchImpl).enqueue("q", [{ item: { n: 1 } }]);

    const ms = Number(requests[0].headers["daprmq-retry-timeout"]);
    expect(ms).toBeGreaterThanOrEqual(4000);
    expect(ms).toBeLessThanOrEqual(5000);
    expect(requests[0].headers["daprmq-timeout"]).toBeUndefined();
  });

  it("lets a slow response outlive the retry timeout", async () => {
    // e.g. queued behind thousands of calls on one busy queue: slow, but progressing.
    const fetchImpl = (async () => {
      await new Promise((r) => setTimeout(r, 300));
      return json(200, OK);
    }) as typeof fetch;

    const result = await client(fetchImpl, { ...FAST, timeoutMs: 50 }).enqueue("q", [{ item: { n: 1 } }]);

    expect(result.success).toBe(true);
  });

  it("throws DaprMQUnavailableError when time runs out", async () => {
    const { fetchImpl, requests } = sequence(notDelivered);

    const error = await client(fetchImpl, { ...FAST, timeoutMs: 200 }).acknowledge("q", "L1").catch((e) => e);

    expect(error).toBeInstanceOf(DaprMQUnavailableError);
    expect(error.code).toBe("UNAVAILABLE");
    expect(error.operation).toBe("acknowledge");
    expect(error.queueId).toBe("q");
    expect(requests.length).toBeGreaterThan(1);
  });

  it("starts no attempt with less than the minimum window left", async () => {
    const { fetchImpl, requests } = sequence(notDelivered);

    await expect(client(fetchImpl, { ...FAST, timeoutMs: 1000, minAttemptWindowMs: 5000 }).acknowledge("q", "L1")).rejects.toBeInstanceOf(
      DaprMQUnavailableError,
    );
    expect(requests).toHaveLength(1);
  });

  it("treats a refused connection as not delivered and retries it", async () => {
    let attempts = 0;
    const fetchImpl = (async () => {
      if (++attempts < 3) {
        throw fetchError("ECONNREFUSED");
      }
      return json(200, OK);
    }) as typeof fetch;

    await client(fetchImpl).enqueue("q", [{ item: { n: 1 } }]);

    expect(attempts).toBe(3);
  });

  it("treats a connection broken after sending as unknown", async () => {
    let attempts = 0;
    const fetchImpl = (async () => {
      attempts++;
      throw fetchError("ECONNRESET");
    }) as typeof fetch;

    await expect(client(fetchImpl).acknowledge("q", "L1")).rejects.toBeInstanceOf(DeliveryUnknownError);
    expect(attempts).toBe(1);
  });

  it("never retries an unknown dequeue", async () => {
    const { fetchImpl, requests } = sequence(unknown);

    const error = await client(fetchImpl).dequeueLocked("q").catch((e) => e);

    expect(error).toBeInstanceOf(DeliveryUnknownError);
    expect(error.code).toBe("DELIVERY_UNKNOWN");
    expect(error.operation).toBe("dequeueLocked");
    expect(requests).toHaveLength(1);
  });

  it("doesn't retry an unknown enqueue with unkeyed items, and reports the keys", async () => {
    const { fetchImpl, requests } = sequence(unknown);

    const error = await client(fetchImpl)
      .enqueue("q", [{ item: { n: 1 }, idempotencyKey: "k1" }, { item: { n: 2 } }])
      .catch((e) => e);

    expect(error).toBeInstanceOf(DeliveryUnknownError);
    expect(error.idempotencyKeys).toEqual(["k1", undefined]);
    expect(requests).toHaveLength(1);
  });

  it("retries an unknown enqueue whose items are all keyed", async () => {
    const { fetchImpl, requests } = sequence(unknown, ok(OK));

    await client(fetchImpl).enqueue("q", [{ item: { n: 1 }, idempotencyKey: "k1" }]);

    expect(requests).toHaveLength(2);
  });

  it("autoIdempotencyKeys fills missing keys, keeps given ones, and makes unknown retryable", async () => {
    const { fetchImpl, requests } = sequence(unknown, ok(OK));

    await client(fetchImpl, { ...FAST, autoIdempotencyKeys: true }).enqueue("q", [
      { item: { n: 1 }, idempotencyKey: "mine" },
      { item: { n: 2 } },
    ]);

    expect(requests).toHaveLength(2);
    expect(requests[1].body).toBe(requests[0].body); // the retry re-sends the same generated key
    const keys = JSON.parse(requests[0].body!).items.map((i: { idempotencyKey: string }) => i.idempotencyKey);
    expect(keys[0]).toBe("mine");
    expect(keys[1]).toMatch(/^[0-9a-f]{32}$/);
  });

  it("doesn't treat a 503 without the marker as a delivery failure", async () => {
    const { fetchImpl, requests } = sequence(() => json(503, { message: "proxy says no" }));

    const error = await client(fetchImpl).acknowledge("q", "L1").catch((e) => e);

    expect(error).toBeInstanceOf(DaprMQError);
    expect(error).not.toBeInstanceOf(DaprMQUnavailableError);
    expect(requests).toHaveLength(1);
  });

  it("with retries off, sends no deadline and makes one attempt", async () => {
    const { fetchImpl, requests } = sequence(notDelivered);

    await expect(client(fetchImpl, { timeoutMs: 0 }).acknowledge("q", "L1")).rejects.toBeInstanceOf(DaprMQUnavailableError);
    expect(requests).toHaveLength(1);
    expect(requests[0].headers["daprmq-retry-timeout"]).toBeUndefined();
  });

  it("stops retrying when the caller aborts, as an abort", async () => {
    const { fetchImpl } = sequence(notDelivered);
    const signal = AbortSignal.timeout(50);

    const error = await client(fetchImpl, { ...FAST, timeoutMs: 30_000, initialBackoffMs: 20, maxBackoffMs: 20 })
      .acknowledge("q", "L1", { signal })
      .catch((e) => e);

    expect(error).not.toBeInstanceOf(DaprMQError);
    expect(signal.aborted).toBe(true);
  });
});
