# SDK retries and readiness

The behaviour every DaprMQ client SDK implements for failed requests and for waiting on a server.
It is phase 5 of [readiness-and-retries.md](../../proposals/readiness-and-retries.md), which has the
reasoning; this is the contract. Names are language-neutral. Each SDK maps them to its own
conventions, e.g. `RetryTimeout` / `retry_timeout` / `retryTimeout`.

## Options

| Option | Default | Meaning |
|---|---|---|
| `RetryTimeout` | 30 s | How long one call may keep retrying. It is also the deadline sent to the server. `0` turns client retries off and sends no deadline, so the server's own cap applies. |
| `AutoIdempotencyKeys` | off | Give every enqueued item without an `IdempotencyKey` a fresh random one (a UUID), so an enqueue whose outcome is unknown can be retried safely. Each key costs the server one extra state write, which is why it is opt-in. |

## Sending the deadline

On every unary request:

- **REST:** send `daprmq-timeout: <remaining ms>`, a positive integer.
- **gRPC:** set the call deadline to the remaining time.

"Remaining" is what is left of `RetryTimeout` when that attempt starts. The server retries
undelivered calls within it and bounds each of its own attempts by it.

## Classifying a failure

| Response / failure | Class |
|---|---|
| REST `503` with `daprmq-delivery: not-delivered` (body `errorCode: UNAVAILABLE`); gRPC `UNAVAILABLE` with trailer `daprmq-delivery: not-delivered` | **not delivered** |
| Nothing was sent: connection refused, or the host name didn't resolve | **not delivered** |
| REST `504` with `daprmq-delivery: unknown` (`errorCode: DELIVERY_UNKNOWN`); gRPC `UNKNOWN` with that trailer | **unknown** |
| The request was sent and then the connection broke, or the attempt timed out | **unknown** |
| Any other response, including every existing DaprMQ error | not a delivery failure: map it as today |

Only the marker decides the class. A `503` or `UNAVAILABLE` without the marker isn't treated as
"not delivered": for example, a proxy in front of DaprMQ may return one after forwarding the request.

## Retrying

| Class | Operation | Retry? |
|---|---|---|
| not delivered | any | yes |
| unknown | Enqueue where **every** item has an `IdempotencyKey`, given or generated | yes. The server de-duplicates on the key |
| unknown | anything else | **no**. Throw `DeliveryUnknown` straight away |

- **Backoff:** start at 100 ms, double each time, cap at 2 s, with full jitter (a uniformly random
  wait between 0 and the current backoff).
- **Minimum attempt window: 6 s.** Don't start an attempt with less than 6 s of `RetryTimeout` left.
  daprd takes about 5 s to answer "no host", so a shorter attempt would be cut off on the server and
  come back as unknown. Instead, stop and throw `Unavailable`. The first attempt is always made.
  Consequence: with `RetryTimeout` under about 6 s, a missing worker may surface as `DeliveryUnknown`.
- **Caller cancellation** stops retrying immediately and surfaces as the language's normal
  cancellation, never as `Unavailable` or `DeliveryUnknown`.
- **Streams** (`ConsumeSession`) are out of scope: session consumers already reconnect.

## Errors

Both derive from the SDK's base DaprMQ error, so existing handlers keep working. Public names never
mention actors.

| Error | When | Carries |
|---|---|---|
| `DaprMQUnavailableException` (or the language's equivalent) | not delivered, and retries ran out (time, or the minimum window) | the operation name and queue id |
| `DeliveryUnknownException` | unknown, and not retried | the operation name, queue id and, for Enqueue, the items' idempotency keys |

What a caller can do after `DeliveryUnknown` depends on the operation (document it in each SDK's
guide):

- **Enqueue without keys:** re-send and accept a possible duplicate.
- **Dequeue with a lock:** don't re-send. If it ran, the items return when their locks expire.
- **Acknowledge / ExtendLock / DeadLetter:** re-sending is effectively safe. If the first attempt
  worked, the re-send gets `LockNotFound`.

## Waiting for a server

`WaitForReady(service = "daprmq.DaprMQ.operations")` opens `grpc.health.v1.Health/Watch` for
`service` and returns on the first `SERVING`. It reconnects with capped backoff while the server
isn't listening (gRPC `UNAVAILABLE`) or the stream ends. It fails with the language's "not supported"
error on `UNIMPLEMENTED`, and it is bounded only by the caller's cancellation.

| Service | `SERVING` means |
|---|---|
| `daprmq.DaprMQ.operations` (default) | Queue operations can be served end to end: on a gateway, at least one worker is available |
| `daprmq.DaprMQ` | This server instance is ready to take requests (Kubernetes readiness) |

Test fixtures and tools wait on `daprmq.DaprMQ.operations` instead of sending a probe enqueue.

## Integration scenarios

Rows `R-01` to `R-05` in the [coverage matrix](INTEGRATION_TESTS.md#coverage-matrix):

- **R-01:** `WaitForReady()` returns against a running stack, and an enqueue then succeeds.
- **R-02:** on a split stack with every worker stopped, an enqueue started with
  `RetryTimeout = 45 s` succeeds once a worker is started 3 s later, and the item is stored once.
- **R-03:** with every worker stopped and `RetryTimeout = 8 s`, an enqueue throws
  `DaprMQUnavailableException` in under 8 s.
- **R-04:** server unreachable, with `RetryTimeout` short: `DaprMQUnavailableException` (connection
  refused is "not delivered"), not a hang.
- **R-05:** `AutoIdempotencyKeys` fills in a key for each item that has none, and leaves keys the
  caller set unchanged.
