import type { ConsumeOptions, QueueDelivery } from "./types.js";

/** nack returns the message to its original position for redelivery (+1 delivery count). */
export type QueueHandlerFailureAction = "nack" | "deadLetter";

export interface QueueConsumerOptions {
  /**
   * Delivered but unsettled messages the server keeps in flight to this consumer: the stream's
   * prefetchCount (1-1000, default 100). Messages over maxConcurrentHandlers wait locked, and the
   * server keeps their locks alive.
   */
  maxActiveMessages?: number;
  /** Handlers running at once. 0/unset = unlimited, which maxActiveMessages bounds. */
  maxConcurrentHandlers?: number;
  /** Passed to the stream (default 30 s); the server renews each lock until its message is settled. */
  lockTtlMs?: number;
  /** Lets replicas share the queue, each holding its own locks. Default true. */
  allowCompetingConsumers?: boolean;
  /**
   * Handles messages one at a time in queue order, including after a nack: forces a window of 1,
   * one handler, and no competing consumers.
   */
  strictOrder?: boolean;
  /** Default "nack". */
  onHandlerError?: QueueHandlerFailureAction;
  /** Paces nacks after handler errors, so a failing handler doesn't spin. Default 10; 0 = unpaced. */
  maxRetriableErrorsPerSec?: number;
  /** Reconnect backoff after the stream breaks, doubling to the max (defaults 1 s, 60 s); resets after a delivery. */
  minBackoffSeconds?: number;
  maxBackoffSeconds?: number;
  /** Milliseconds stop() lets running handlers finish and settle before aborting their signal. */
  drainTimeoutMs?: number;
}

export interface QueueMessageContext {
  queueId: string;
  lockId: string;
  item: unknown;
  priority: number;
  /** 1 on a first delivery, 2 on the first redelivery, and so on. */
  deliveryCount: number;
}

/** The slice of DaprMQClient that QueueConsumer needs - satisfied by DaprMQClient itself. */
export interface QueueCapableClient {
  consume(queueId: string, options: ConsumeOptions): AsyncIterable<QueueDelivery>;
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
 * Runs a handler over a plain queue's consume stream: success acks the message, an error nacks or
 * dead-letters it, and a broken stream is reopened with backoff (the server has already returned
 * whatever was unsettled on it).
 */
export class QueueConsumer {
  private readonly client: QueueCapableClient;
  private readonly queueId: string;
  private readonly handler: (context: QueueMessageContext, signal: AbortSignal) => Promise<void>;
  private readonly streamOptions: Omit<ConsumeOptions, "signal">;
  private readonly handlerLimit: number;
  private readonly onHandlerError: QueueHandlerFailureAction;
  private readonly maxRetriableErrorsPerSec: number;
  private readonly minBackoffSeconds: number;
  private readonly maxBackoffSeconds: number;
  private readonly drainTimeoutMs: number;

  /** Aborted when stopping begins: no more messages are started. */
  private readonly stopController = new AbortController();
  /** Handlers' signal: aborted only once drainTimeoutMs runs out. */
  private readonly handlerController = new AbortController();
  /** Closes the current stream: aborted by stop() once running handlers have settled on it. */
  private streamController = new AbortController();
  private readonly handlers = new Set<Promise<void>>();
  private freeSlots: number;
  private readonly slotWaiters: (() => void)[] = [];
  private nextNackAt = 0;
  private running: Promise<void> | undefined;

  /** Test seam for the reconnect backoff and nack pacing. */
  delay: (ms: number, signal: AbortSignal) => Promise<void> = sleep;

  constructor(
    client: QueueCapableClient,
    queueId: string,
    options: QueueConsumerOptions,
    handler: (context: QueueMessageContext, signal: AbortSignal) => Promise<void>,
  ) {
    const strict = options.strictOrder ?? false;
    this.client = client;
    this.queueId = queueId;
    this.handler = handler;
    this.streamOptions = {
      prefetchCount: strict ? 1 : Math.max(options.maxActiveMessages ?? 100, 1),
      lockTtlMs: options.lockTtlMs ?? 30_000,
      allowCompetingConsumers: !strict && (options.allowCompetingConsumers ?? true),
    };
    this.handlerLimit = strict ? 1 : (options.maxConcurrentHandlers ?? 0);
    this.freeSlots = this.handlerLimit;
    this.onHandlerError = options.onHandlerError ?? "nack";
    this.maxRetriableErrorsPerSec = options.maxRetriableErrorsPerSec ?? 10;
    this.minBackoffSeconds = options.minBackoffSeconds ?? 1;
    this.maxBackoffSeconds = options.maxBackoffSeconds ?? 60;
    this.drainTimeoutMs = options.drainTimeoutMs ?? 30_000;
  }

  start(): void {
    this.running ??= this.run();
  }

  /**
   * Stops handing out messages, lets running handlers finish and settle for up to drainTimeoutMs
   * (then aborts their signal), and closes the stream, so the server returns every message not yet
   * handled straight away.
   */
  async stop(): Promise<void> {
    this.stopController.abort();
    for (const wake of this.slotWaiters.splice(0)) {
      wake();
    }

    const running = [...this.handlers];
    if (running.length > 0) {
      let timer: ReturnType<typeof setTimeout> | undefined;
      const drained = await Promise.race([
        Promise.all(running).then(() => true),
        new Promise<boolean>((resolve) => (timer = setTimeout(() => resolve(false), this.drainTimeoutMs))),
      ]);
      clearTimeout(timer);
      if (!drained) {
        this.handlerController.abort();
        await Promise.all(running);
      }
    }
    this.streamController.abort();
    await this.running;
  }

  private async run(): Promise<void> {
    const stopSignal = this.stopController.signal;
    let backoffSeconds = this.minBackoffSeconds;

    while (!stopSignal.aborted) {
      let delivered = false;
      // On stop the stream stays open until running handlers have settled on it; closing it under
      // a handler would lose that message's ack.
      this.streamController = new AbortController();
      try {
        const stream = this.client.consume(this.queueId, { ...this.streamOptions, signal: this.streamController.signal });
        for await (const delivery of stream) {
          delivered = true;
          if (!(await this.takeSlot())) {
            continue; // stopping: left unsettled, returned when the stream closes
          }
          const handling = this.handle(delivery).finally(() => this.handlers.delete(handling));
          this.handlers.add(handling);
        }
      } catch {
        // the stream broke: reopen (or stop) below
      }

      await Promise.all([...this.handlers]);
      if (stopSignal.aborted) {
        break;
      }

      if (delivered) {
        backoffSeconds = this.minBackoffSeconds;
      }
      try {
        await this.delay(backoffSeconds * 1000, stopSignal);
      } catch {
        break;
      }
      if (stopSignal.aborted) {
        break;
      }
      backoffSeconds = Math.min(backoffSeconds * 2, this.maxBackoffSeconds);
    }
  }

  /** Waits for a handler slot; false once stopping. */
  private async takeSlot(): Promise<boolean> {
    if (this.stopController.signal.aborted) {
      return false;
    }
    if (this.handlerLimit <= 0) {
      return true;
    }
    while (this.freeSlots === 0) {
      await new Promise<void>((resolve) => this.slotWaiters.push(resolve));
      if (this.stopController.signal.aborted) {
        return false;
      }
    }
    this.freeSlots--;
    return true;
  }

  private releaseSlot(): void {
    if (this.handlerLimit <= 0) {
      return;
    }
    this.freeSlots++;
    this.slotWaiters.shift()?.();
  }

  /** Runs the handler and settles the message. Never rejects: a settle that fails because the stream broke needs nothing more, as the server returns the message. */
  private async handle(delivery: QueueDelivery): Promise<void> {
    const context: QueueMessageContext = {
      queueId: this.queueId,
      lockId: delivery.lockId,
      item: delivery.item,
      priority: delivery.priority,
      deliveryCount: delivery.deliveryCount,
    };
    try {
      try {
        await this.handler(context, this.handlerController.signal);
      } catch {
        if (this.stopController.signal.aborted) {
          return; // stopping: left unsettled, returned when the stream closes
        }
        if (this.onHandlerError === "deadLetter") {
          await delivery.deadLetter();
        } else {
          await this.paceNack();
          await delivery.nack();
        }
        return;
      }
      await delivery.ack();
    } catch {
      // the stream closed under the message: the server returns it
    } finally {
      this.releaseSlot();
    }
  }

  /** Waits for this nack's slot: at most maxRetriableErrorsPerSec nacks a second. */
  private async paceNack(): Promise<void> {
    if (this.maxRetriableErrorsPerSec <= 0) {
      return;
    }
    const now = performance.now();
    const slot = Math.max(this.nextNackAt, now);
    this.nextNackAt = slot + 1000 / this.maxRetriableErrorsPerSec;
    if (slot > now) {
      await this.delay(slot - now, this.handlerController.signal);
    }
  }
}
