/**
 * Bridges a push-based source (grpc-js's ClientDuplexStream 'data'/'end'/'error' events) to a
 * pull-based `for await` consumer. Single-consumer only - one `next()` outstanding at a time,
 * which is all a `for await` loop ever needs.
 */
export class AsyncMessageQueue<T> implements AsyncIterable<T> {
  private readonly buffer: T[] = [];
  private waiting: { resolve: (result: IteratorResult<T>) => void; reject: (err: Error) => void } | undefined;
  private error: Error | undefined;
  private ended = false;

  push(value: T): void {
    if (this.waiting) {
      const { resolve } = this.waiting;
      this.waiting = undefined;
      resolve({ value, done: false });
    } else {
      this.buffer.push(value);
    }
  }

  end(): void {
    this.ended = true;
    if (this.waiting) {
      const { resolve } = this.waiting;
      this.waiting = undefined;
      resolve({ value: undefined as unknown as T, done: true });
    }
  }

  fail(err: Error): void {
    this.error = err;
    if (this.waiting) {
      const { reject } = this.waiting;
      this.waiting = undefined;
      reject(err);
    }
  }

  [Symbol.asyncIterator](): AsyncIterator<T> {
    return {
      next: (): Promise<IteratorResult<T>> => {
        if (this.buffer.length > 0) {
          return Promise.resolve({ value: this.buffer.shift() as T, done: false });
        }
        if (this.error) {
          const err = this.error;
          this.error = undefined;
          return Promise.reject(err);
        }
        if (this.ended) {
          return Promise.resolve({ value: undefined as unknown as T, done: true });
        }
        return new Promise<IteratorResult<T>>((resolve, reject) => {
          this.waiting = { resolve, reject };
        });
      },
    };
  }
}
