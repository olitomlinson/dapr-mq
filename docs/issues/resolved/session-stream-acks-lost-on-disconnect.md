# Acks sent on a session stream are lost if the consumer disconnects straight after

**Status:** Fixed on branch `fix/session-stream-acks-lost-on-disconnect` (see [Fix](#fix)). Regression tests:
`ConsumeSession_ClientCancelsWhileAckInFlight_AckStillApplied` (server) and
`ConsumeSessionAsync_ConsumerStopsAfterAcking_WaitsForServerToFinishBeforeCancellingTheCall` (.NET SDK).

**Severity:** Medium. No message is lost: an unapplied ack leaves its lock in place, and the item is
redelivered when the session is next claimed, which at-least-once delivery allows. But `AckAsync`
returned successfully for every one of those messages, so the consumer believes it settled work that
will come back, and does it twice.

---

## Summary

On a `ConsumeSession` stream, an ack is a fire-and-forget frame. The SDK's `AckAsync` returns as soon
as the frame is written to the request stream
([DaprMQClient.cs:274](../../../sdks/dotnet/src/DaprMQ.Client/DaprMQClient.cs#L274)). There is no reply.

If the consumer stops reading right after acking — `break`, cancelling its token, disposing, shutting
down — the call is torn down before the server has applied those acks:

1. **The SDK cancels the call immediately.** Its `finally` half-closes the request stream
   (`CompleteAsync`) and then disposes the call (`using var call`,
   [DaprMQClient.cs:238](../../../sdks/dotnet/src/DaprMQ.Client/DaprMQClient.cs#L238)). Disposing a call
   that hasn't finished cancels it, so the half-close never gets the chance to drain.
2. **The server abandons acks on cancellation.** Its reader loop reads frames with, and invokes
   `Acknowledge` with, a token linked to the call's cancellation token
   ([DaprMQGrpcService.cs:665-676](../../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L665-L676)).
   Once the call is cancelled, the ack in flight is cancelled and frames not yet read are never read.

The server then releases the session. The locks for the unapplied acks are still recorded against the
lapsed lease, so the next `AcceptSession` restores those items to the front of the session queue.

`SessionQueueConsumer`'s normal path isn't affected: a stream that ends because the server sent
`SessionDrained` has, by construction, applied every ack (the server only drains with nothing
outstanding). It is affected whenever a consumer stops with messages acked but not yet applied, for
example on `StopAsync` or application shutdown.

## Reproduction

Found by the perf harness's `consume-session` step (`./run-session-perf-test.sh --benchmark
state-reads`). An early version consumed 10 sessions of 3 messages each, acked every message, and
cancelled the stream as soon as the third ack's `AckAsync` returned. The Postgres statement log showed
only 2–3 `*-lock` reads across all 30 acks: about 27 of 30 never reached the actor. Waiting 300 ms
before cancelling brought all 30 back, which is the workaround the step now uses.

The two unit tests reproduce each half deterministically:

- **Server:** an ack that the server has read and is applying is cancelled when the client cancels the
  call ([DaprMQGrpcServiceConsumeSessionTests.cs](../../../server/tests/DaprMQ.Tests/DaprMQGrpcServiceConsumeSessionTests.cs)).
- **SDK:** after a consumer acks and breaks, the SDK disposes (cancels) the call before the server has
  ended its response stream ([DaprMQClientConsumeSessionTests.cs](../../../sdks/dotnet/tests/DaprMQ.Client.Tests/DaprMQClientConsumeSessionTests.cs)).

## Fix direction

Both halves are needed: the server can only finish acks it receives, and the SDK can only help by
giving it the time.

- **Server:** apply an ack the reader has already read with a token that isn't tied to the client
  call (for example `CancellationToken.None`, bounded by a short timeout), so a disconnect can't
  abandon it half-way. On a clean half-close the reader already drains every buffered frame before
  the stream ends.
- **SDK:** when enumeration stops early, half-close the request stream and then wait for the server to
  end its response stream, bounded by a short timeout, before disposing the call. Only a timeout, or
  the caller's own cancellation, should cancel the call outright.

Options considered:
- **Confirm each ack with a reply frame**, and make `AckAsync` await it. That makes `AckAsync` mean
  "applied", which is the stronger contract, but changes the protocol and adds a round trip per ack.
- **Do nothing and document it.** At-least-once delivery permits this, but `AckAsync` succeeding for a
  message that is then redelivered will surprise people.

## Related

- [STATE_READS_BREAKDOWN.md](../../STATE_READS_BREAKDOWN.md#consume-session-19-reads-20-writes-per-session)
  — the benchmark step that surfaced this.
- Batching acks on the stream (discussed alongside the benchmark) would make this window larger, so it
  should land after this fix.

## Fix

- **Server** ([DaprMQGrpcService.cs](../../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)):
  Ack and DeadLetter frames are applied on their own token, bounded by `SettleTimeout` (10 s), instead
  of the call's. An ack the server has read is finished even if the client goes away.
- **SDK** ([DaprMQClient.cs](../../../sdks/dotnet/src/DaprMQ.Client/DaprMQClient.cs)): the caller's
  token is no longer bound to the gRPC call. However the consumer stops - token, `break`, error - the
  SDK half-closes the request stream and reads until the server ends the call, then disposes it. Only
  if the server hasn't finished within `SessionDrainTimeout` (5 s) is the call cancelled. Deliveries
  that arrive while stopping aren't handed out; they stay locked and return with the session. Writes
  are serialised, since a stop can now race an ack; settling on a stream that is closing throws.
- **Verified** with the `consume-session` benchmark step, changed to `break` straight after the last
  ack: all 30 acks reach the actor (30 `*-lock` reads, 60 writes), where the original code lost ~27.
