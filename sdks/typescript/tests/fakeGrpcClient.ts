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

  write(message: ConsumeSessionRequestMessage): boolean {
    this.written.push(message);
    return true;
  }

  end(): void {
    this.ended = true;
  }

  cancel(): void {
    this.cancelled = true;
    this.emit("error", new Error("Cancelled"));
  }

  /** Test helper: deliver a server frame asynchronously, like a real stream would. */
  emitData(message: ConsumeSessionResponseMessage): void {
    queueMicrotask(() => this.emit("data", message));
  }

  emitEnd(): void {
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
