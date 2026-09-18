import * as grpc from "@grpc/grpc-js";
import {
  ActorNotFoundError,
  DaprMQError,
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
  type ConsumeSessionResponseMessage,
  type DaprMQGrpcClient,
} from "./grpc/daprmqGrpcClient.js";
import { AsyncMessageQueue } from "./asyncMessageQueue.js";
import type { DequeueLockedResult, EnqueueItem, EnqueueResult, SessionDelivery, SessionLease } from "./types.js";
import type {
  AcceptSessionResponseWire,
  AcknowledgeResponseWire,
  DeadLetterResponseWire,
  DequeueLockedResponseWire,
  EnqueueResponseWire,
  LockedResponseWire,
  RenewSessionLeaseResponseWire,
} from "./wireTypes.js";

export interface DaprMQClientOptions {
  httpBaseUrl: string;
  /** Required unless `grpcClient` is supplied directly (e.g. in tests). */
  grpcAddress?: string;
  grpcCredentials?: grpc.ChannelCredentials;
  /** Test/DI seam - substitute a fetch implementation instead of the global one. */
  fetch?: typeof fetch;
  /** Test/DI seam - substitute a pre-built (or mocked) gRPC client instead of dialing grpcAddress. */
  grpcClient?: DaprMQGrpcClient;
}

export class DaprMQClient {
  private readonly httpBaseUrl: string;
  private readonly fetchImpl: typeof fetch;
  private readonly grpcClient: DaprMQGrpcClient;
  private readonly ownsGrpcClient: boolean;

  constructor(options: DaprMQClientOptions) {
    this.httpBaseUrl = options.httpBaseUrl.replace(/\/+$/, "");
    this.fetchImpl = options.fetch ?? fetch;

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
    const body = {
      items: items.map((i) => ({
        item: i.item,
        priority: i.priority ?? 1,
        idempotencyKey: i.idempotencyKey,
        sessionId: i.sessionId,
      })),
    };

    const response = await this.postJson(this.path(queueId, "enqueue"), body, undefined, signal);
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
    options: { count?: number; ttlSeconds?: number; leaseId?: string; signal?: AbortSignal } = {},
  ): Promise<DequeueLockedResult | null> {
    const { count = 1, ttlSeconds = 30, leaseId, signal } = options;
    const headers: Record<string, string> = {
      "require-ack": "true",
      count: String(count),
      "ttl-seconds": String(ttlSeconds),
    };
    if (leaseId != null) {
      headers["lease-id"] = leaseId;
    }

    const response = await this.fetchImpl(this.path(queueId, "dequeue"), { method: "POST", headers, signal });

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
    const response = await this.postJson(this.path(queueId, "acknowledge"), { lockId }, options.leaseId, options.signal);
    if (response.ok) {
      return;
    }

    const text = await this.readBodyText(response);
    const body = this.tryParseJson<AcknowledgeResponseWire>(text);
    throw this.mapLockError(body?.errorCode, body?.message ?? this.errorMessageFrom(text, response.status));
  }

  async extendLock(
    queueId: string,
    lockId: string,
    additionalTtlSeconds: number,
    options: { leaseId?: string; signal?: AbortSignal } = {},
  ): Promise<void> {
    const response = await this.postJson(
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
    const response = await this.postJson(this.path(queueId, "deadletter"), { lockId }, options.leaseId, options.signal);
    if (response.ok) {
      return;
    }

    const text = await this.readBodyText(response);
    const body = this.tryParseJson<DeadLetterResponseWire>(text);
    throw this.mapLockError(body?.errorCode, body?.message ?? this.errorMessageFrom(text, response.status));
  }

  async acceptSession(
    queueId: string,
    options: { sessionId?: string; leaseSeconds?: number; signal?: AbortSignal } = {},
  ): Promise<SessionLease | null> {
    const { sessionId, leaseSeconds = 30, signal } = options;
    const response = await this.postJson(this.path(queueId, "sessions/accept"), { sessionId, leaseSeconds }, undefined, signal);

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
   */
  async *consumeSession(
    queueId: string,
    options: { sessionId?: string; leaseSeconds?: number; prefetchCount?: number; signal?: AbortSignal } = {},
  ): AsyncGenerator<SessionDelivery, void, void> {
    const { sessionId, leaseSeconds = 30, prefetchCount = 10, signal } = options;
    const call = this.grpcClient.consumeSession();

    const onAbort = () => call.cancel();
    signal?.addEventListener("abort", onAbort);

    const queue = new AsyncMessageQueue<ConsumeSessionResponseMessage>();
    call.on("data", (msg) => queue.push(msg));
    call.on("end", () => queue.end());
    call.on("error", (err: Error) => queue.fail(err));

    try {
      call.write({ start: { queueId, sessionId, leaseSeconds, prefetchCount } });

      let assignedSessionId = sessionId ?? "";

      for await (const response of queue) {
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
              ack: async () => {
                call.write({ ack: { lockId: delivered.lockId } });
              },
              deadLetter: async () => {
                call.write({ deadLetter: { lockId: delivered.lockId } });
              },
            };
            break;
          }

          case "error":
            throw this.mapSessionError(response.error!.errorCode, response.error!.message);

          case "sessionLost":
            throw new SessionLostError(response.sessionLost!.message);
        }
      }
    } finally {
      signal?.removeEventListener("abort", onAbort);
      call.end();
    }
  }

  close(): void {
    if (this.ownsGrpcClient) {
      this.grpcClient.close();
    }
  }

  private path(queueId: string, suffix: string): string {
    return `${this.httpBaseUrl}/queue/${encodeURIComponent(queueId)}/${suffix}`;
  }

  private async postJson(path: string, body: unknown, leaseId: string | undefined, signal: AbortSignal | undefined): Promise<Response> {
    const headers: Record<string, string> = { "content-type": "application/json" };
    if (leaseId != null) {
      headers["lease-id"] = leaseId;
    }
    return this.fetchImpl(path, { method: "POST", headers, body: JSON.stringify(body), signal });
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
