# Stopping a session consumer drops acks and in-flight work

**Status:** Open. Found 2026-10-08 while adding the Go SDK (`feature/go-sdk`). The Go SDK already
behaves as described in [Target behaviour](#target-behaviour); the other four SDKs don't.

**Severity:** Medium. No message is lost: an unsettled lock returns with its session, and delivery is
at-least-once. But a message the consumer acked, or was about to ack, comes back and is processed
twice, and in Python, TypeScript and Java `ack()` reported success for it.

---

## Summary

On a `ConsumeSession` stream an ack is a fire-and-forget frame: there is no reply, and `ack()` returns
once the frame is written. Two things go wrong when a consumer stops:

1. **Acks already sent are dropped (Python, TypeScript, Java).** Stopping cancels the gRPC call
   straight away. Frames the server hasn't read yet are never read, so those messages are
   redelivered. This is the bug fixed for .NET in
   [session-stream-acks-lost-on-disconnect.md](resolved/session-stream-acks-lost-on-disconnect.md)
   (commit `554997c`). That fix had a server half, which applies to every SDK, and an SDK half,
   which was only ever made in .NET.
2. **The handler that's running loses its work (all four).** Stop either cancels the handler's
   token at once (.NET), or cancels the stream under it, so its ack fails or is lost (Python,
   TypeScript, Java). The message is redelivered although the handler finished it. This contradicts
   matrix row K-09, "`Stop` drains in-flight handlers within `DrainTimeout`".

| SDK | Stream on stop | Handler already running | Its ack |
|---|---|---|---|
| .NET | half-closes, waits up to 5 s, then cancels | its token is cancelled at once | throws ("stream is closing"); redelivered |
| Python | `call.cancel()` at once | keeps running, never cancelled even after `drain_timeout_seconds` | lost, or the write fails |
| TypeScript | `call.cancel()` on abort | gets the stop signal, already aborted | lost, or the write fails |
| Java | `stream.cancel()` on every stream | keeps running; not interrupted after the drain timeout | lost, or the write fails |
| Go | half-closes once the running handler has settled, waits for the server, then cancels | keeps its context until `DrainTimeout` | sent and applied |

## Target behaviour

Decided 2026-10-08: every SDK does what the Go SDK does.

**Low-level stream** (`ConsumeSession` / `consume_session` / `consumeSession`). However it stops
(caller cancellation, `break`, close, or an error):

- Half-close the request stream; don't cancel the call.
- Keep reading until the server ends the response stream. It does so once it has applied every
  frame it was sent, then it releases the session.
- Hand out no further deliveries while stopping. They stay locked and return with the session.
- Cancel the call only if the server hasn't ended it within **5 s** (.NET's `SessionDrainTimeout`;
  give the others the same internal constant).
- A settle call (`ack` / `deadLetter` / `nack`) after the half-close fails with the SDK's normal
  error ("stream is closing"); it doesn't send a frame.
- Serialise writes, since a stop can now race an ack.

**`SessionQueueConsumer.Stop`:**

1. Stop claiming sessions. An idle slot (waiting for a delivery or in backoff) stops at once: it
   half-closes its stream as above.
2. A slot whose handler is running lets the handler finish. It then settles the message per
   `OnHandlerError`, or acks on success, and half-closes. It never starts another message, even one
   already prefetched. If the handler fails while stopping, leave the message unsettled; it returns
   with the session (all SDKs already do this).
3. The handler's cancellation signal fires only when `DrainTimeout` (default 30 s) runs out, not when
   `Stop` is called. Past that point, cancel handlers and streams; `Stop` returns.
4. `Stop` returns once every slot has finished: the sessions are released at that point, not merely
   on their way to being released.

The Go implementation is the reference:
[session_queue_consumer.go](../../sdks/go/session_queue_consumer.go) (`consumeOneSession`,
`handle`, `stop`) and [session_stream.go](../../sdks/go/session_stream.go) (`Close`, `send`). Its
tests are in [session_queue_consumer_test.go](../../sdks/go/session_queue_consumer_test.go) and
[session_stream_test.go](../../sdks/go/session_stream_test.go).

## Work, one commit per SDK

Write the failing tests first in each SDK, then fix. Run that SDK's unit tests and its integration
suite (see [INTEGRATION_TESTS.md](../../sdks/testing/INTEGRATION_TESTS.md#running)). Run
`./build-and-test.sh` before committing.

Tests every SDK needs (names follow each SDK's style):

- **Stream, stop after acking:** ack, then stop (cancel / `break` / close). The fake server sees the
  ack, then a half-close, and the call isn't cancelled before the server ends it. Port
  `.NET DaprMQClientConsumeSessionTests.ConsumeSessionAsync_ConsumerStopsAfterAcking_WaitsForServerToFinishBeforeCancellingTheCall`.
- **Stream, server never ends:** after a half-close, the call is cancelled once the 5 s drain runs
  out (make the constant overridable for the test, as .NET does).
- **Stream, nothing handed out while stopping:** a delivery that arrives after the half-close is not
  yielded.
- **Consumer, stop during a handler:** the handler isn't cancelled, its ack reaches the server, and
  `Stop` returns only after that.
- **Consumer, stop past `DrainTimeout`:** the handler's cancellation fires and `Stop` returns in
  about `DrainTimeout`.
- **Consumer, idle stop:** with no message in flight, `Stop` returns promptly and the stream was
  half-closed, not cancelled.
- **Consumer, prefetch not handed out:** with prefetch > 1, stopping during the first handler
  doesn't start the second message.

### 1. Go: shorten the drain wait

- [session_stream.go](../../sdks/go/session_stream.go): `closeGrace` is 30 s. Make it 5 s, to match.
  Nothing else changes.

### 2. .NET: let the running handler finish

The stream half is already done ([DaprMQClient.cs](../../sdks/dotnet/src/DaprMQ.Client/DaprMQClient.cs),
`ConsumeSessionAsync`). The consumer half isn't:

- [SessionQueueConsumer.cs](../../sdks/dotnet/src/DaprMQ.Client/SessionQueueConsumer.cs)
  `RunSlotAsync` passes `stopToken` both to `ConsumeSessionAsync` and to the handler and settle calls
  (`HandleDeliveryAsync`). Stop therefore half-closes the stream under a running handler, and cancels
  the handler.
- Fix: give each slot its own stream token, cancelled at stop if the slot is idle, and otherwise
  straight after the running message is settled. Give handlers a separate token that is cancelled
  only when `DrainTimeout` runs out. `StopAsync` currently just stops waiting after `DrainTimeout`;
  it must cancel that handler token instead, then wait for the slots.
- `catch (Exception) when (!ct.IsCancellationRequested)` in `HandleDeliveryAsync` must test the
  *stopping* state, not the handler token.

### 3. Python

- [client.py](../../sdks/python/src/daprmq_client/client.py) `consume_session`: the `cancel` event's
  watcher calls `call.cancel()`. Instead, on `cancel`: half-close (`done_writing()`), stop yielding,
  read until the call ends, and `call.cancel()` only after the 5 s drain. Do the same in the
  `finally`. Today it calls `done_writing()` but doesn't wait for the server to end the call. Serialise writes with an `asyncio.Lock`.
- [session_queue_consumer.py](../../sdks/python/src/daprmq_client/session_queue_consumer.py): `stop()`
  sets `_stop_event`, which is also the stream's `cancel`, so the stream half-closes under a running
  handler. Give each slot its own stream event, set when idle or after the running message is
  settled. After `drain_timeout_seconds`, `stop()` must cancel the slot tasks (`task.cancel()`),
  which today keep running. The handler takes no cancel argument: task cancellation is Python's
  handler-cancellation signal.

### 4. TypeScript

- [client.ts](../../sdks/typescript/src/client.ts) `consumeSession`: `signal` aborts with
  `call.cancel()`, and the `finally` calls `call.end()` with no wait. Instead, on abort or exit:
  `call.end()` (half-close), stop yielding, wait for `"end"`/`"error"`, and `call.cancel()` only
  after the 5 s drain. `ack`/`deadLetter`/`nack` after the half-close must reject, not write.
- [sessionQueueConsumer.ts](../../sdks/typescript/src/sessionQueueConsumer.ts): `stop()` aborts
  `stopController`, whose signal is both the stream's `signal` and the handler's. Give each slot its
  own stream controller (aborted when idle or after the running message settles). Pass handlers a
  separate signal that is aborted when `drainTimeoutMs` runs out. `stop()` aborts it at that point,
  where today it only stops waiting.

### 5. Java

- [SessionQueueConsumer.java](../../sdks/java/src/main/java/com/daprmq/client/SessionQueueConsumer.java)
  `stop()` calls `stream.cancel()` on every active stream. Instead, close (half-close) idle streams
  at once, and let a slot that's handling close its stream after it settles. After the drain timeout,
  `executor.shutdownNow()` so handlers are interrupted, then cancel what's left. Today it calls
  `shutdown()` and never interrupts.
- [SessionStream.java](../../sdks/java/src/main/java/com/daprmq/client/SessionStream.java): `close()`
  already half-closes (`onCompleted`). Add the rest: after `close()` the iterator yields no more
  deliveries but keeps draining until `DONE`/error. `cancel()` fires if that takes longer than 5 s.
  `send` after close throws (`DaprMQException`, "stream is closing"). `ack()` etc. are `Runnable`s,
  so throw unchecked.

### 6. Docs and matrix

- Each SDK's `docs/CLIENT_SDK.md` stop section: say that `Stop` lets running handlers finish and
  settle within `DrainTimeout`, cancels them after it, and returns once sessions are released.
  Prefetched, unhandled messages return with their session.
- [INTEGRATION_TESTS.md](../../sdks/testing/INTEGRATION_TESTS.md): K-09 only has a .NET test. Add
  K-09 for each SDK if its fixture makes it cheap; otherwise the unit tests above cover it. Tick
  only what exists and passes.
- When done, move this file to `resolved/` with a **Fix** section, like the earlier issue.

## Related

- [session-stream-acks-lost-on-disconnect.md](resolved/session-stream-acks-lost-on-disconnect.md):
  the original bug and the server half of the fix.
- Server: [DaprMQGrpcService.cs](../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)
  `ConsumeSession`. The reader applies every frame up to the client's half-close, then ends the
  stream and releases the session. No server change is needed.
