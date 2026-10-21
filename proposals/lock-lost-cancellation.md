# Cancel a handler when its message's lock is lost

## Context

A handler has no way to know that its message's lock has gone.

- **`Consume` (queue).** The handler's cancellation token is `_handlerCts.Token`
  ([QueueConsumer.cs](../sdks/dotnet/src/DaprMQ.Client/QueueConsumer.cs)). It's cancelled only on
  shutdown (`StopAsync`, or the drain timeout).
  - The server drops a lock from a stream in two places in `RenewDueLocksAsync`
    ([DaprMQGrpcService.cs](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs)):
    - a lock it no longer renews has lapsed. Either `MaxAutoLockRenewalDuration` was reached, or the
      lock was never started under `RENEW_STARTED`.
    - an `ExtendLockBatch` outcome other than `EXTENDED`, such as `LOCK_NOT_FOUND` or `LOCK_EXPIRED`.
  - In both cases it only logs. Its own comment says the client "learns that when it tries to settle
    it", through a `settle_failed` frame after the work is done.
- **`ConsumeSession` (sessions).** `SessionLost` exists, but the SDKs read the stream in the
  delivery loop (`await foreach` in .NET). So the frame is only seen after the current handler
  returns.

Either way, the handler keeps working on a message whose result can't be acked, while another
consumer may already be processing it. The work is wasted and its side effects run twice. That gets
more likely now that renewal is capped at 5 minutes by default (`MaxAutoLockRenewalDuration`, #89),
and with session leases that lapse when renewals queue behind a busy coordinator.

## Proposal

**The server says when a lock is lost, and the SDKs cancel that message's handler.**

### Server

1. **A new non-terminal `Consume` frame:**

   ```proto
   message ConsumeResponse {
     oneof payload {
       ConsumeDelivered delivered = 1;
       ConsumeSettleFailed settle_failed = 2;
       ConsumeError error = 3;
       ConsumeLockLost lock_lost = 4;   // new
     }
   }

   // Not terminal. The item's lock is gone and it may be delivered again; its settle will fail.
   message ConsumeLockLost {
     string lock_id = 1;
     string reason = 2;   // RENEWAL_LIMIT, NOT_STARTED, LOCK_NOT_FOUND, LOCK_EXPIRED
   }
   ```

2. **Sent at the two places that already drop the lock** in `RenewDueLocksAsync`.
   - **A lapsed, unrenewed lock** is reported when it actually expires (`ExpiresAt <= now`), not when
     renewal stops. The lock is still valid until then, so the handler may still finish and ack.
   - **A failed renewal outcome** is reported straight away.
   - It goes through the stream's existing write lock.
3. **`ConsumeSession` needs no new frame.** `SessionLost` already means every lock in the session is
   gone.

A client that doesn't know frame 4 ignores it, as protobuf does for an unknown `oneof` case, and
behaves as today.

### SDKs

1. **One cancellation token per message,** linked to the consumer's shutdown token. The handler gets
   that token (as `ct` in .NET), so handlers that already respect it stop without any change.
2. **`Consume`:** a `lock_lost` frame cancels that message's token. This follows the same pattern as
   `settle_failed`: a callback on `ConsumeOptions` (`OnLockLost`, next to `OnSettleFailed`) that
   `QueueConsumer` maps from lock ID to token. A prefetched message whose lock is lost before its
   handler starts is dropped instead of started.
3. **Sessions:** the SDK reads the session stream on its own task, separate from the handler. On
   `SessionLost` it cancels every token in that session, then ends the slot's stream as today.
4. **Why a token was cancelled:** the message context gets `LockLostReason` (null while the lock is
   held), so a handler can tell "someone else has this message" from "we're shutting down", which it
   checks with the consumer's own stopping state.
5. **All five SDKs** (.NET, Python, TypeScript, Java, Go), in the same release as the server change.

### Not doing

- **Exactly-once.** The frame can arrive after the handler has already done its side effects, and a
  broken stream can't deliver it at all. Handlers still need to be idempotent. This only cuts down
  wasted and duplicated work.
- **Forcing a handler to stop.** Cancellation is cooperative. CPU-bound code, or calls that don't
  take a token, keep running.
- **Changing the settle path.** An ack after a lost lock still fails with `settle_failed`, exactly as
  today, which also covers the race where the lock lapses a moment before the frame arrives.

## Alternatives considered

| Alternative | Why not |
|---|---|
| A separate lock-lost signal (an event or a second token), leaving the handler's token for shutdown only. Azure Service Bus does this, I believe, with `MessageLockLostAsync` and `SessionLockLostAsync`; worth confirming. | Handlers would have to opt in to something new, so existing ones get no benefit. A single token plus `LockLostReason` gives the same information, and every handler that already respects `ct` benefits. Revisit if users need to keep working after a lock loss, for example to clean up. |
| The SDK works out lock loss locally, from `lock_expires_at` and the renewal cap | The client can't see server-side renewals or `ExtendLockBatch` outcomes, so it would cancel healthy handlers or miss real losses. |
| Report loss at the moment renewal stops, rather than at expiry | Cancels a handler that could still finish and ack inside the remaining lock TTL. |

## Tests (TDD order, per CLAUDE.md)

1. **Integration (gRPC).**
   - Under `RENEW_STARTED` with a small `max_renewal_seconds`, a slow handler receives `lock_lost`
     (`RENEWAL_LIMIT`) once its lock expires.
   - A lock removed on the server (for example by dead-lettering it through another client) produces
     `lock_lost` (`LOCK_NOT_FOUND`) after the next renewal.
2. **Server unit tests (`RenewDueLocksAsync`, mocked invoker).**
   - A frame is sent for each dropped lock, with the right reason, through the write lock.
   - Nothing is sent for a lock that a settle removed first.
3. **.NET SDK.**
   - `QueueConsumer` cancels only the affected handler's token and sets `LockLostReason`.
   - Shutdown still cancels every handler.
   - A prefetched lost message isn't started.
   - `SessionQueueConsumer` cancels the running handlers on `SessionLost`.
4. **Other SDKs:** the same cases in each SDK's consumer tests.

## Open questions

1. **A token per message or per slot?** Per message is exact for `Consume`. For sessions, one token
   per stream covers it, because `SessionLost` affects the whole session.
2. **Should a lost prefetched message be reported to the application** (for example through
   `OnLockLost`), or only dropped?
