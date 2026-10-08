import * as grpc from "@grpc/grpc-js";
import { randomUUID } from "node:crypto";
import {
  ActorNotFoundError,
  DaprMQError,
  DaprMQUnavailableError,
  DeliveryUnknownError,
  InvalidLeaseIdError,
  LockExpiredError,
  LockNotFoundError,
  NoSessionsAvailableError,
  SessionActorUnavailableError,
  SessionLeaseExpiredError,
  SessionLockedError,
  SessionLostError,
  SessionNotFoundError,
  ValidationError,
} from "./errors.js";
import {
  createDaprMQGrpcClient,
  type ConsumeSessionRequestMessage,
  type ConsumeSessionResponseMessage,
  type DaprMQGrpcClient,
} from "./grpc/daprmqGrpcClient.js";
import { createHealthGrpcClient, type HealthGrpcClient } from "./grpc/healthGrpcClient.js";
import { AsyncMessageQueue } from "./asyncMessageQueue.js";
import type { AcknowledgeBatchResult, DequeueLockedResult, EnqueueItem, EnqueueResult, NackResult, SessionDelivery, SessionLease } from "./types.js";
import type {
  AcceptSessionResponseWire,
  AcknowledgeBatchResponseWire,
  AcknowledgeResponseWire,
  DeadLetterResponseWire,
  NackResponseWire,
  DequeueLockedResponseWire,
  EnqueueResponseWire,
  LockedResponseWire,
  RenewSessionLeaseResponseWire,
} from "./wireTypes.js";

/** The health service `waitForReady` watches by default: queue operations can be served. */
export const OPERATIONS_HEALTH_SERVICE = "daprmq.DaprMQ.operations";

const DELIVERY_MARKER = "daprmq-delivery";
const RETRY_TIMEOUT_HEADER = "daprmq-retry-timeout";
/** Fetch failure causes that mean nothing was sent (the connection never opened). */
const NOT_SENT_CODES = new Set(["ECONNREFUSED", "ENOTFOUND", "EAI_AGAIN", "EHOSTUNREACH", "ENETUNREACH", "UND_ERR_CONNECT_TIMEOUT"]);

/** How calls ride out a DaprMQ that can't serve them yet (sdks/testing/RETRIES_AND_READINESS.md). */
export interface RetryOptions {
  /** How long one call may keep retrying a DaprMQ that can't serve it; also sent to the server as its retry window. Never cuts a call that was delivered short. 0 = retries off. Default 30 000. */
  timeoutMs?: number;
  /** Give each enqueued item without an idempotencyKey a fresh one, so an enqueue whose outcome is unknown is retried safely. Costs the server one extra state write per item. */
  autoIdempotencyKeys?: boolean;
  /** Tuning (normally left alone): no attempt starts with less left, since the server takes ~5 s to report it can't serve. Default 6 000. */
  minAttemptWindowMs?: number;
  initialBackoffMs?: number;
  maxBackoffMs?: number;
}

export interface DaprMQClientOptions {
  httpBaseUrl: string;
  /** Required unless `grpcClient` is supplied directly (e.g. in tests). */
  grpcAddress?: string;
  grpcCredentials?: grpc.ChannelCredentials;
  /** Test/DI seam - substitute a fetch implementation instead of the global one. */
  fetch?: typeof fetch;
  /** Test/DI seam - substitute a pre-built (or mocked) gRPC client instead of dialing grpcAddress. */
  grpcClient?: DaprMQGrpcClient;
  /** Test/DI seam - substitute a health client instead of dialing grpcAddress. */
  healthClient?: HealthGrpcClient;
  retry?: RetryOptions;
}

export class DaprMQClient {
  /**
   * How long a stopping consumeSession stream waits, after half-closing, for the server to apply
   * what it was sent and end the stream, before it cancels the call. Overridable for tests.
   * @internal
   */
  static sessionDrainTimeoutMs = 5_000;

  private readonly httpBaseUrl: string;
  private readonly fetchImpl: typeof fetch;
  private readonly grpcClient: DaprMQGrpcClient;
  private readonly ownsGrpcClient: boolean;
  private readonly healthClient?: HealthGrpcClient;
  private readonly ownsHealthClient: boolean;
  private readonly retry: Required<RetryOptions>;

  constructor(options: DaprMQClientOptions) {
    this.httpBaseUrl = options.httpBaseUrl.replace(/\/+$/, "");
    this.fetchImpl = options.fetch ?? fetch;
    this.retry = {
      timeoutMs: 30_000,
      autoIdempotencyKeys: false,
      minAttemptWindowMs: 6_000,
      initialBackoffMs: 100,
      maxBackoffMs: 2_000,
      ...options.retry,
    };
    this.healthClient = options.healthClient;
    this.ownsHealthClient = false;
    if (!options.healthClient && options.grpcAddress) {
      this.healthClient = createHealthGrpcClient(options.grpcAddress, options.grpcCredentials ?? grpc.credentials.createInsecure());
      this.ownsHealthClient = true;
    }

    if (options.grpcClient) {
      this.grpcClient = options.grpcClient;
      this.ownsGrpcClient = false;
    } else {
      if (!options.grpcAddress) {
        throw new Error("DaprMQClientOptions.grpcAddress is required unless grpcClient is supplied.");
      }
      this.grpcClient = createDaprMQGrpcClient(
        options.grpcAddress,
        options.grpcCredentials ?? grpc.credentials.createInsecure(),
      );
      this.ownsGrpcClient = true;
    }
  }

  async enqueue(queueId: string, items: EnqueueItem[], signal?: AbortSignal): Promise<EnqueueResult> {
    // Keys are fixed before the first attempt, so a retry re-sends the same ones.
    const keys = items.map((i) => i.idempotencyKey ?? (this.retry.autoIdempotencyKeys ? randomUUID().replaceAll("-", "") : undefined));
    const body = {
      items: items.map((i, n) => ({
        item: i.item,
        priority: i.priority ?? 1,
        idempotencyKey: keys[n],
        sessionId: i.sessionId,
      })),
    };

    // An unknown outcome is only safe to repeat when the server can de-duplicate every item.
    const response = await this.send("enqueue", queueId, this.path(queueId, "enqueue"), {
      body,
      signal,
      unknownIsRetryable: keys.every((k) => k !== undefined),
      idempotencyKeys: keys,
    });
    if (!response.ok) {
      const text = await this.readBodyText(response);
      throw this.mapGenericError(response.status, text);
    }

    const text = await this.readBodyText(response);
    const result = this.requireParsed<EnqueueResponseWire>(text, response.url);
    return {
      success: result.success,
      message: result.message,
      itemsEnqueued: result.itemsEnqueued,
      itemsDeduplicated: result.itemsDeduplicated ?? 0,
    };
  }

  async dequeueLocked(
    queueId: string,
    options: {
      count?: number;
      ttlSeconds?: number;
      leaseId?: string;
      allowCompetingConsumers?: boolean;
      signal?: AbortSignal;
    } = {},
  ): Promise<DequeueLockedResult | null> {
    const { count = 1, ttlSeconds = 30, leaseId, allowCompetingConsumers = false, signal } = options;
    const headers: Record<string, string> = {
      "require-ack": "true",
      count: String(count),
      "ttl-seconds": String(ttlSeconds),
    };
    if (leaseId != null) {
      headers["lease-id"] = leaseId;
    }
    if (allowCompetingConsumers) {
      headers["allow-competing-consumers"] = "true";
    }

    const response = await this.send("dequeueLocked", queueId, this.path(queueId, "dequeue"), { headers, signal });

    if (response.status === 204) {
      return null;
    }

    if (response.status === 423) {
      const text = await this.readBodyText(response);
      const locked = this.tryParseJson<LockedResponseWire>(text);
      return { items: [], locked: true, message: locked?.message };
    }

    if (!response.ok) {
      const text = await this.readBodyText(response);
      const message = this.errorMessageFrom(text, response.status);
      // Dequeue's guard rejection carries no wire error code - 410 here is unambiguously the
      // session-lease guard (plain lock expiry doesn't apply to Dequeue), 400 covers both a
      // bad/missing lease-id and ordinary request validation (e.g. bad count).
      throw response.status === 410 ? new SessionLeaseExpiredError(message) : new ValidationError(message);
    }

    const text = await this.readBodyText(response);
    const result = this.requireParsed<DequeueLockedResponseWire>(text, response.url);
    return {
      items: result.items.map((i) => ({ item: i.item, priority: i.priority, lockId: i.lockId, lockExpiresAt: i.lockExpiresAt })),
      locked: result.locked,
      message: result.message,
    };
  }

  async acknowledge(
    queueId: string,
    lockId: string,
    options: { leaseId?: string; signal?: AbortSignal } = {},
  ): Promise<void> {
    const response = await this.postJson("acknowledge", queueId, this.path(queueId, "acknowledge"), { lockId }, options.leaseId, options.signal);
    if (response.ok) {
      return;
    }

    const text = await this.readBodyText(response);
    const body = this.tryParseJson<AcknowledgeResponseWire>(text);
    throw this.mapLockError(body?.errorCode, body?.message ?? this.errorMessageFrom(text, response.status));
  }

  /**
   * Acknowledges up to 1,000 locks in one call, with an outcome per lock: one lock that expired or
   * was already settled does not fail the rest. Throws only for whole-call failures (bad lease,
   * invalid request). An unknown outcome is retried automatically, since re-sending is harmless;
   * after such a retry, LOCK_NOT_FOUND can mean "already settled".
   */
  async acknowledgeBatch(
    queueId: string,
    lockIds: string[],
    options: { leaseId?: string; signal?: AbortSignal } = {},
  ): Promise<AcknowledgeBatchResult> {
    const headers: Record<string, string> = {};
    if (options.leaseId != null) {
      headers["lease-id"] = options.leaseId;
    }
    const response = await this.send("acknowledgeBatch", queueId, this.path(queueId, "acknowledge-batch"), {
      body: { lockIds },
      headers,
      signal: options.signal,
      unknownIsRetryable: true,
    });
    const text = await this.readBodyText(response);
    const body = this.tryParseJson<AcknowledgeBatchResponseWire>(text);
    if (response.ok) {
      return {
        itemsAcknowledged: body?.itemsAcknowledged ?? 0,
        results: (body?.results ?? []).map((r) => ({ lockId: r.lockId, outcome: r.outcome })),
      };
    }

    throw this.mapLockError(body?.errorCode, body?.message ?? this.errorMessageFrom(text, response.status));
  }

  async extendLock(
    queueId: string,
    lockId: string,
    additionalTtlSeconds: number,
    options: { leaseId?: string; signal?: AbortSignal } = {},
  ): Promise<void> {
    const response = await this.postJson(
      "extendLock",
      queueId,
      this.path(queueId, "extend-lock"),
      { lockId, additionalTtlSeconds },
      options.leaseId,
      options.signal,
    );
    if (response.ok) {
      return;
    }

    const text = await this.readBodyText(response);
    const message = this.errorMessageFrom(text, response.status);
    // ExtendLock's error body carries no wire error code (unlike Acknowledge/DeadLetter), so this
    // is a best-effort status-based mapping only.
    throw response.status === 410
      ? new LockExpiredError(message)
      : response.status === 404
        ? new LockNotFoundError(message)
        : new ValidationError(message);
  }

  async deadLetter(
    queueId: string,
    lockId: string,
    options: { leaseId?: string; signal?: AbortSignal } = {},
  ): Promise<void> {
    const response = await this.postJson("deadLetter", queueId, this.path(queueId, "deadletter"), { lockId }, options.leaseId, options.signal);
    if (response.ok) {
      return;
    }

    const text = await this.readBodyText(response);
    const body = this.tryParseJson<DeadLetterResponseWire>(text);
    throw this.mapLockError(body?.errorCode, body?.message ?? this.errorMessageFrom(text, response.status));
  }

  /**
   * Returns a locked item to its original position in the queue. Counts as a delivery attempt:
   * past the server's max delivery count the item is dead-lettered instead (deadLettered: true).
   */
  async nack(
    queueId: string,
    lockId: string,
    options: { leaseId?: string; signal?: AbortSignal } = {},
  ): Promise<NackResult> {
    const response = await this.postJson("nack", queueId, this.path(queueId, "nack"), { lockId }, options.leaseId, options.signal);
    const text = await this.readBodyText(response);
    const body = this.tryParseJson<NackResponseWire>(text);
    if (response.ok) {
      return { deadLettered: body?.deadLettered ?? false, deliveryCount: body?.deliveryCount ?? 0, dlqId: body?.dlqId ?? undefined };
    }

    throw this.mapLockError(body?.errorCode, body?.message ?? this.errorMessageFrom(text, response.status));
  }

  async acceptSession(
    queueId: string,
    options: { sessionId?: string; leaseSeconds?: number; signal?: AbortSignal } = {},
  ): Promise<SessionLease | null> {
    const { sessionId, leaseSeconds = 30, signal } = options;
    const response = await this.postJson("acceptSession", queueId, this.path(queueId, "sessions/accept"), { sessionId, leaseSeconds }, undefined, signal);

    if (response.status === 204) {
      return null;
    }

    if (!response.ok) {
      const text = await this.readBodyText(response);
      const message = this.errorMessageFrom(text, response.status);
      switch (response.status) {
        case 404:
          throw new SessionNotFoundError(message);
        case 423:
          throw new SessionLockedError(message);
        case 502:
          throw new SessionActorUnavailableError(message);
        default:
          throw new ValidationError(message);
      }
    }

    const text = await this.readBodyText(response);
    const result = this.requireParsed<AcceptSessionResponseWire>(text, response.url);
    return { sessionId: result.sessionId, leaseId: result.leaseId, leaseExpiresAt: result.leaseExpiresAt };
  }

  async renewSessionLease(
    queueId: string,
    sessionId: string,
    leaseId: string,
    options: { additionalSeconds?: number; signal?: AbortSignal } = {},
  ): Promise<SessionLease> {
    const { additionalSeconds = 30, signal } = options;
    const response = await this.postJson(
      "renewSessionLease",
      queueId,
      this.path(queueId, `sessions/${encodeURIComponent(sessionId)}/renew`),
      { leaseId, additionalSeconds },
      undefined,
      signal,
    );

    if (!response.ok) {
      const text = await this.readBodyText(response);
      const message = this.errorMessageFrom(text, response.status);
      throw response.status === 410 ? new SessionLeaseExpiredError(message) : new InvalidLeaseIdError(message);
    }

    const text = await this.readBodyText(response);
    const result = this.requireParsed<RenewSessionLeaseResponseWire>(text, response.url);
    return { sessionId, leaseId, leaseExpiresAt: result.newExpiresAt };
  }

  async releaseSession(queueId: string, sessionId: string, leaseId: string, signal?: AbortSignal): Promise<void> {
    const response = await this.postJson(
      "releaseSession",
      queueId,
      this.path(queueId, `sessions/${encodeURIComponent(sessionId)}/release`),
      { leaseId },
      undefined,
      signal,
    );

    if (!response.ok) {
      const text = await this.readBodyText(response);
      throw new InvalidLeaseIdError(this.errorMessageFrom(text, response.status));
    }
  }

  /**
   * Managed consume loop for exactly one session: claims a session (any-available or targeted),
   * streams delivered items back, and lets the caller ack/deadLetter each one. No LeaseId is
   * exposed here (unlike the unary session API) - the server tracks the lease internally and the
   * grpc call itself renews it for as long as the stream stays open.
   *
   * However the stream stops (`signal`, leaving the loop, or an error), it half-closes rather than
   * cancelling the call: the server applies every settlement already sent, then ends the stream and
   * releases the session. Nothing more is handed out meanwhile, settling afterwards rejects, and the
   * call is cancelled only if the server hasn't ended it within 5 s.
   */
  async *consumeSession(
    queueId: string,
    options: {
      sessionId?: string;
      leaseSeconds?: number;
      prefetchCount?: number;
      sessionIdleTimeoutSeconds?: number;
      signal?: AbortSignal;
    } = {},
  ): AsyncGenerator<SessionDelivery, void, void> {
    const { sessionId, leaseSeconds = 30, prefetchCount = 1, sessionIdleTimeoutSeconds = 0, signal } = options;
    const call = this.grpcClient.consumeSession();

    const queue = new AsyncMessageQueue<ConsumeSessionResponseMessage>();
    let serverEnded!: () => void;
    const finished = new Promise<void>((resolve) => (serverEnded = resolve));
    call.on("data", (msg) => queue.push(msg));
    call.on("end", () => {
      queue.end();
      serverEnded();
    });
    call.on("error", (err: Error) => {
      queue.fail(err);
      serverEnded();
    });

    let halfClosed = false;
    let drainExpired = false;
    let drainTimer: ReturnType<typeof setTimeout> | undefined;
    const halfClose = () => {
      if (halfClosed) {
        return;
      }
      halfClosed = true;
      try {
        call.end();
      } catch {
        // best-effort - the stream may already be broken
      }
      drainTimer = setTimeout(() => {
        drainExpired = true;
        call.cancel();
      }, DaprMQClient.sessionDrainTimeoutMs);
    };
    const settle = async (request: ConsumeSessionRequestMessage) => {
      if (halfClosed) {
        throw new DaprMQError("The session stream is closing; this message can no longer be settled on it.");
      }
      call.write(request);
    };

    try {
      call.write({ start: { queueId, sessionId, leaseSeconds, prefetchCount, sessionIdleTimeoutSeconds } });
      signal?.addEventListener("abort", halfClose);
      if (signal?.aborted) {
        halfClose();
      }

      let assignedSessionId = sessionId ?? "";

      for await (const response of queue) {
        if (halfClosed) {
          continue; // stopping: let the server finish, but hand out nothing more
        }
        switch (response.payload) {
          case "sessionAssigned":
            assignedSessionId = response.sessionAssigned!.sessionId;
            break;

          case "delivered": {
            const delivered = response.delivered!;
            yield {
              sessionId: assignedSessionId,
              lockId: delivered.lockId,
              item: JSON.parse(delivered.itemJson),
              priority: delivered.priority,
              lockExpiresAt: delivered.lockExpiresAt,
              ack: () => settle({ ack: { lockId: delivered.lockId } }),
              deadLetter: () => settle({ deadLetter: { lockId: delivered.lockId } }),
              nack: () => settle({ nack: { lockId: delivered.lockId } }),
            };
            break;
          }

          case "error":
            throw this.mapSessionError(response.error!.errorCode, response.error!.message);

          case "sessionLost":
            throw new SessionLostError(response.sessionLost!.message);

          case "sessionDrained":
            // Terminal, not an error - end the generator cleanly, same as the stream ending on
            // its own. SessionQueueConsumer's runSlot loop already treats a clean end as "claim
            // another".
            return;
        }
      }
    } catch (err) {
      if (!drainExpired) {
        throw err; // a drain that ran out cancels the call, which fails the read
      }
    } finally {
      signal?.removeEventListener("abort", halfClose);
      // However the stream stopped, let the server apply what it was sent before the call ends.
      halfClose();
      await finished;
      clearTimeout(drainTimer);
    }
  }

  /**
   * Waits until the server reports SERVING for `service` over the standard gRPC health protocol
   * (grpc.health.v1.Health/Watch). The default means queue operations can be served end to end;
   * "daprmq.DaprMQ" means just this server instance is ready. Reconnects while the server isn't
   * listening; has no deadline of its own, so bound it with `signal`.
   */
  async waitForReady(options: { service?: string; signal?: AbortSignal } = {}): Promise<void> {
    const { service = OPERATIONS_HEALTH_SERVICE, signal } = options;
    const health = this.healthClient;
    if (!health) {
      throw new Error("This DaprMQClient was created without a gRPC address or health client.");
    }

    let backoffMs = 250;
    while (true) {
      signal?.throwIfAborted();
      const call = health.watch(service);
      const onAbort = () => call.cancel();
      signal?.addEventListener("abort", onAbort, { once: true });
      try {
        for await (const response of call) {
          if (response.status === "SERVING") {
            return;
          }
          backoffMs = 250;
        }
        // Stream ended before SERVING (e.g. server shutting down) - reconnect.
      } catch (err) {
        signal?.throwIfAborted();
        const code = (err as { code?: number }).code;
        if (code === grpc.status.UNIMPLEMENTED) {
          throw new Error("The DaprMQ server does not expose the gRPC health service; upgrade the server.", { cause: err });
        }
        if (code !== grpc.status.UNAVAILABLE) {
          throw err;
        }
        // Server not listening yet - retry with backoff.
      } finally {
        signal?.removeEventListener("abort", onAbort);
        call.cancel();
      }
      await sleep(backoffMs, signal);
      backoffMs = Math.min(backoffMs * 2, 2_000);
    }
  }

  close(): void {
    if (this.ownsGrpcClient) {
      this.grpcClient.close();
    }
    if (this.ownsHealthClient) {
      this.healthClient?.close();
    }
  }

  private path(queueId: string, suffix: string): string {
    return `${this.httpBaseUrl}/queue/${encodeURIComponent(queueId)}/${suffix}`;
  }

  private async postJson(
    operation: string,
    queueId: string,
    path: string,
    body: unknown,
    leaseId: string | undefined,
    signal: AbortSignal | undefined,
  ): Promise<Response> {
    const headers: Record<string, string> = {};
    if (leaseId != null) {
      headers["lease-id"] = leaseId;
    }
    return this.send(operation, queueId, path, { body, headers, signal });
  }

  /**
   * One REST call under the retry contract (sdks/testing/RETRIES_AND_READINESS.md): not-delivered
   * failures are retried within the retry timeout, unknown outcomes only when `unknownIsRetryable`.
   * Every attempt tells the server how much retry time is left (daprmq-retry-timeout); it never cuts
   * a delivered call short, which runs until the caller aborts. Returns any other response to map.
   */
  private async send(
    operation: string,
    queueId: string,
    path: string,
    options: {
      body?: unknown;
      headers?: Record<string, string>;
      signal?: AbortSignal;
      unknownIsRetryable?: boolean;
      idempotencyKeys?: (string | undefined)[];
    },
  ): Promise<Response> {
    const { body, signal, unknownIsRetryable = false, idempotencyKeys } = options;
    const retry = this.retry;
    const retries = retry.timeoutMs > 0;
    const deadline = Date.now() + retry.timeoutMs;
    let backoffMs = retry.initialBackoffMs;

    while (true) {
      const remaining = deadline - Date.now();
      const headers: Record<string, string> = { ...options.headers };
      if (body !== undefined) {
        headers["content-type"] = "application/json";
      }
      if (retries) {
        headers[RETRY_TIMEOUT_HEADER] = String(Math.max(1, Math.floor(remaining)));
      }
      signal?.throwIfAborted();

      let notDelivered: boolean;
      let reason: string;
      try {
        const response = await this.fetchImpl(path, {
          method: "POST",
          headers,
          body: body === undefined ? undefined : JSON.stringify(body),
          signal,
        });
        const marker = response.headers.get(DELIVERY_MARKER);
        if (marker !== "not-delivered" && marker !== "unknown") {
          return response;
        }
        notDelivered = marker === "not-delivered";
        const text = await this.readBodyText(response);
        reason = this.errorMessageFrom(text, response.status);
      } catch (err) {
        if (signal?.aborted) {
          throw err; // the caller gave up: cancellation, not a delivery failure
        }
        const code = ((err as { cause?: { code?: string } }).cause ?? {}).code;
        notDelivered = code !== undefined && NOT_SENT_CODES.has(code); // otherwise sent, then broke or timed out
        reason = code ?? (err as Error).message;
      }

      const retryable = notDelivered || unknownIsRetryable;
      const delayMs = Math.random() * backoffMs;
      const timeLeft = deadline - Date.now() - delayMs;
      if (!retries || !retryable || timeLeft < retry.minAttemptWindowMs) {
        throw notDelivered
          ? new DaprMQUnavailableError(`DaprMQ is unavailable; ${operation} was not performed: ${reason}`, operation, queueId)
          : new DeliveryUnknownError(
              `The outcome of ${operation} is unknown: it may or may not have been performed (${reason})`,
              operation,
              queueId,
              idempotencyKeys,
            );
      }

      await sleep(delayMs, signal);
      backoffMs = Math.min(backoffMs * 2, retry.maxBackoffMs);
    }
  }

  private async readBodyText(response: Response): Promise<string> {
    try {
      return await response.text();
    } catch {
      return "";
    }
  }

  private tryParseJson<T>(text: string): T | undefined {
    if (!text) {
      return undefined;
    }
    try {
      return JSON.parse(text) as T;
    } catch {
      return undefined;
    }
  }

  private requireParsed<T>(text: string, url: string): T {
    const parsed = this.tryParseJson<T>(text);
    if (parsed === undefined) {
      throw new DaprMQError(`Empty or malformed response body from ${url}`);
    }
    return parsed;
  }

  private errorMessageFrom(text: string, status: number): string {
    const body = this.tryParseJson<{ message?: string }>(text);
    return body?.message ?? `Request failed with status ${status}`;
  }

  private mapGenericError(status: number, text: string): DaprMQError {
    const message = this.errorMessageFrom(text, status);
    switch (status) {
      case 400:
        return new ValidationError(message);
      case 404:
        return new ActorNotFoundError(message);
      default:
        return new DaprMQError(message);
    }
  }

  private mapLockError(errorCode: string | undefined, message: string): DaprMQError {
    switch (errorCode) {
      case "LOCK_NOT_FOUND":
        return new LockNotFoundError(message);
      case "LOCK_EXPIRED":
        return new LockExpiredError(message);
      case "SESSION_LEASE_EXPIRED":
        return new SessionLeaseExpiredError(message);
      case "INVALID_LEASE_ID":
        return new InvalidLeaseIdError(message);
      case "INVALID_LOCK_ID":
      case "INVALID_TTL":
      case "VALIDATION_ERROR":
        return new ValidationError(message);
      default:
        return new DaprMQError(message, errorCode);
    }
  }

  private mapSessionError(errorCode: string, message: string): DaprMQError {
    switch (errorCode) {
      case "SESSION_NOT_FOUND":
        return new SessionNotFoundError(message);
      case "SESSION_LOCKED":
        return new SessionLockedError(message);
      case "NO_SESSIONS_AVAILABLE":
        return new NoSessionsAvailableError(message);
      case "SESSION_ACTOR_UNAVAILABLE":
        return new SessionActorUnavailableError(message);
      default:
        return new DaprMQError(message, errorCode);
    }
  }
}

function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason);
      return;
    }
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal!.reason);
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
