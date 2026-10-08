import { EventEmitter } from "node:events";
import type {
  ConsumeSessionRequestMessage,
  ConsumeSessionResponseMessage,
  DaprMQGrpcClient,
} from "../src/grpc/daprmqGrpcClient.js";

/** Minimal fake duplex stream standing in for grpc-js's ClientDuplexStream in tests. */
export class FakeDuplexStream extends EventEmitter {
  readonly written: ConsumeSessionRequestMessage[] = [];
  ended = false;
  cancelled = false;
  cancelledBeforeServerEnded = false;
  private serverEnded = false;

  /**
   * endsAfterHalfCloseMs: like the real server, end the response stream this long after the client
   * half-closes (undefined: never, unless the test ends it). onHalfClose: called on the half-close.
   */
  constructor(
    private readonly endsAfterHalfCloseMs?: number,
    public onHalfClose?: () => void,
  ) {
    super();
  }

  write(message: ConsumeSessionRequestMessage): boolean {
    if (this.ended) {
      throw new Error("write after end");
    }
    this.written.push(message);
    return true;
  }

  end(): void {
    this.ended = true;
    this.onHalfClose?.();
    if (this.endsAfterHalfCloseMs !== undefined) {
      setTimeout(() => this.emitEnd(), this.endsAfterHalfCloseMs);
    }
  }

  cancel(): void {
    this.cancelled = true;
    this.cancelledBeforeServerEnded ||= !this.serverEnded;
    this.emit("error", new Error("Cancelled"));
  }

  /** Test helper: deliver a server frame asynchronously, like a real stream would. */
  emitData(message: ConsumeSessionResponseMessage): void {
    queueMicrotask(() => this.emit("data", message));
  }

  emitEnd(): void {
    this.serverEnded = true;
    queueMicrotask(() => this.emit("end"));
  }

  emitError(err: Error): void {
    queueMicrotask(() => this.emit("error", err));
  }
}

export function fakeGrpcClient(stream: FakeDuplexStream): DaprMQGrpcClient {
  return {
    consumeSession: () => stream as unknown as ReturnType<DaprMQGrpcClient["consumeSession"]>,
    close: () => {},
  } as unknown as DaprMQGrpcClient;
}
