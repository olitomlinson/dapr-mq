import { NoSessionsAvailableError, SessionActorUnavailableError, SessionLockedError, SessionLostError, SessionNotFoundError } from "./errors.js";
import type { SessionDelivery } from "./types.js";

export type SessionHandlerFailureAction = "deadLetterMessage" | "abandonSession" | "both";

export interface SessionQueueConsumerOptions {
  maxConcurrentSessions?: number;
  /** Sticky routing to one specific session. Must pair with maxConcurrentSessions === 1. */
  targetSessionId?: string;
  leaseSeconds?: number;
  prefetchCount?: number;
  minBackoffSeconds?: number;
  maxBackoffSeconds?: number;
  onHandlerException?: SessionHandlerFailureAction;
  /** Milliseconds to wait for in-flight handlers to finish on stop() before closing streams. */
  drainTimeoutMs?: number;
}

interface ResolvedOptions {
  maxConcurrentSessions: number;
  targetSessionId: string | undefined;
  leaseSeconds: number;
  prefetchCount: number;
  minBackoffSeconds: number;
  maxBackoffSeconds: number;
  onHandlerException: SessionHandlerFailureAction;
  drainTimeoutMs: number;
}

/**
 * No leaseId here (unlike the low-level unary session API) - the ConsumeSession wire protocol
 * never exposes one to the client, since the server tracks the lease internally. See
 * SessionDelivery.
 */
export interface SessionMessageContext {
  queueId: string;
  sessionId: string;
  lockId: string;
  item: unknown;
  priority: number;
}

/** The slice of DaprMQClient that SessionQueueConsumer needs - satisfied by DaprMQClient itself. */
export interface SessionCapableClient {
  consumeSession(
    queueId: string,
    options: { sessionId?: string; leaseSeconds?: number; prefetchCount?: number; signal?: AbortSignal },
  ): AsyncIterable<SessionDelivery>;
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(new DOMException("Aborted", "AbortError"));
      return;
    }
    const timer = setTimeout(resolve, ms);
    signal.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        reject(new DOMException("Aborted", "AbortError"));
      },
      { once: true },
    );
  });
}

/**
 * Manages a pool of `maxConcurrentSessions` independent slots, each looping over a
 * consumeSession stream: claim a session, hand each delivered item to the caller's handler,
 * ack/deadLetter it, and repeat until the session drains - then claim another. Backoff on a
 * failed claim doubles on a miss, resets on a successful claim, and is capped.
 */
export class SessionQueueConsumer {
  private readonly client: SessionCapableClient;
  private readonly queueId: string;
  private readonly options: ResolvedOptions;
  private readonly handler: (context: SessionMessageContext, signal: AbortSignal) => Promise<void>;
  private readonly stopController = new AbortController();

  private slotPromises: Promise<void>[] | undefined;

  /** Test seam - substituted by tests to assert on requested backoff durations without waiting real time. */
  delay: (ms: number, signal: AbortSignal) => Promise<void> = sleep;

  constructor(
    client: SessionCapableClient,
    queueId: string,
    options: SessionQueueConsumerOptions,
    handler: (context: SessionMessageContext, signal: AbortSignal) => Promise<void>,
  ) {
    const maxConcurrentSessions = options.maxConcurrentSessions ?? 4;
    if (options.targetSessionId != null && maxConcurrentSessions !== 1) {
      throw new Error("targetSessionId requires maxConcurrentSessions === 1.");
    }

    this.client = client;
    this.queueId = queueId;
    this.options = {
      maxConcurrentSessions,
      targetSessionId: options.targetSessionId,
      leaseSeconds: options.leaseSeconds ?? 30,
      prefetchCount: options.prefetchCount ?? 10,
      minBackoffSeconds: options.minBackoffSeconds ?? 1,
      maxBackoffSeconds: options.maxBackoffSeconds ?? 60,
      onHandlerException: options.onHandlerException ?? "deadLetterMessage",
      drainTimeoutMs: options.drainTimeoutMs ?? 30_000,
    };
    this.handler = handler;
  }

  start(): void {
    this.slotPromises = Array.from({ length: this.options.maxConcurrentSessions }, () => this.runSlot());
  }

  /**
   * Stops claiming new sessions, gives in-flight handlers up to drainTimeoutMs to finish, then
   * closes their streams - closing the stream is itself what releases the session, no separate
   * releaseSession call is needed here.
   */
  async stop(): Promise<void> {
    this.stopController.abort();

    if (this.slotPromises && this.slotPromises.length > 0) {
      await Promise.race([Promise.all(this.slotPromises), new Promise((resolve) => setTimeout(resolve, this.options.drainTimeoutMs))]);
    }
  }

  private async runSlot(): Promise<void> {
    const stopSignal = this.stopController.signal;
    let backoffSeconds = this.options.minBackoffSeconds;

    while (!stopSignal.aborted) {
      let sessionWasClaimed = false;
      try {
        const stream = this.client.consumeSession(this.queueId, {
          sessionId: this.options.targetSessionId,
          leaseSeconds: this.options.leaseSeconds,
          prefetchCount: this.options.prefetchCount,
          signal: stopSignal,
        });

        for await (const delivery of stream) {
          sessionWasClaimed = true;
          await this.handleDelivery(delivery, stopSignal);
        }

        sessionWasClaimed = true; // stream ended cleanly after a successful claim (drained)
      } catch (err) {
        if (stopSignal.aborted) {
          break;
        }
        if (err instanceof SessionLostError) {
          sessionWasClaimed = true; // claim succeeded; the lease was lost afterward
        } else if (
          err instanceof NoSessionsAvailableError ||
          err instanceof SessionNotFoundError ||
          err instanceof SessionLockedError ||
          err instanceof SessionActorUnavailableError
        ) {
          // claim itself failed - fall through to backoff below
        } else {
          // Any other exception surfacing mid-stream - including a handler exception re-thrown by
          // handleDelivery under abandonSession/both - ends this slot's current stream early.
          // sessionWasClaimed is already true by the time the loop body can throw, so the outer
          // loop retries immediately rather than backing off as if the claim itself had failed.
        }
      }

      if (stopSignal.aborted) {
        break;
      }

      if (sessionWasClaimed) {
        backoffSeconds = this.options.minBackoffSeconds;
        continue;
      }

      try {
        await this.delay(backoffSeconds * 1000, stopSignal);
      } catch {
        break;
      }

      backoffSeconds = Math.min(backoffSeconds * 2, this.options.maxBackoffSeconds);
    }
  }

  private async handleDelivery(delivery: SessionDelivery, signal: AbortSignal): Promise<void> {
    const context: SessionMessageContext = {
      queueId: this.queueId,
      sessionId: delivery.sessionId,
      lockId: delivery.lockId,
      item: delivery.item,
      priority: delivery.priority,
    };

    try {
      await this.handler(context, signal);
      await delivery.ack();
    } catch (err) {
      if (signal.aborted) {
        throw err;
      }
      switch (this.options.onHandlerException) {
        case "deadLetterMessage":
          await delivery.deadLetter();
          break;
        case "abandonSession":
          throw err; // unwinds the stream loop, ending this slot's stream early
        case "both":
          await delivery.deadLetter();
          throw err;
      }
    }
  }
}
