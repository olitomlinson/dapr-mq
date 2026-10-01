# `SessionLost` cannot be triggered deterministically from any SDK

**Status:** Accepted gap, won't fix. **Not a defect** — the server behaviour here looks correct;
what is missing is any supported way to provoke it, so no SDK can test it. Direction 3 below was
taken: `C-09` is permanently out of scope for integration, covered only by unit tests, and this
doc records why.

**Severity:** Low as a bug, medium as a coverage gap. `SessionLost` is the terminal frame that
tells a consumer its session is gone mid-flight, and every SDK has mapping code for it
(`SessionLostException` and its siblings) that is currently exercised only by unit tests with fakes.

**Blocks:** `C-09` in [sdks/testing/INTEGRATION_TESTS.md](../../../sdks/testing/INTEGRATION_TESTS.md),
for **all four** SDK columns, not just .NET.

---

## Summary

The server emits `SessionLost` on a `ConsumeSession` stream in exactly two situations
([DaprMQGrpcService.cs:702](../../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L702)
and [:729](../../../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L729)):

1. its background `RenewSessionLease` call fails, or
2. a `DequeueLocked` on the session actor comes back with an error code.

Both require the stream's lease to have become invalid *while the stream is still open*. A client
cannot arrange that, because **`ConsumeSession` never tells the client the lease id.** That is
deliberate and documented — it is what makes the managed loop simpler than the manual API (see
`SessionDelivery`'s doc comment in [Models.cs](../../../sdks/dotnet/src/DaprMQ.Client/Models.cs)). The
server holds the id, renews on its own schedule, and applies it internally on the client's behalf.

With no lease id, there is no supported call that invalidates it: `ReleaseSession` requires the id,
and `AcceptSession` on a leased session is correctly refused with `SESSION_LOCKED`.

## Why the obvious workaround does not hold

The one remaining lever is to race the renewal interval against the lease TTL by opening the stream
with `leaseSeconds = 1`. The server renews when `now - lastRenewalAt >= leaseSeconds / 2`, and the
coordinator treats a lease as expired when `now >= ExpiresAt`. Whether the first renewal lands on
the expired side depends on sub-second truncation at accept time:

- Accept truncates to the same second the handler started → `ExpiresAt = T0 + 1`, the renewal at
  `T0 + 1` sees `now >= ExpiresAt`, the lease is lost, `SessionLost` is emitted. ✅
- Accept truncates one second later → `ExpiresAt = T0 + 2`, the renewal at `T0 + 1` succeeds and
  pushes expiry to `T0 + 3`. Each subsequent renewal outruns expiry by one second, so the lease is
  never lost and the test hangs until its timeout. ❌

Roughly a coin flip, decided by where the accept call falls within a wall-clock second. Not a test
worth having in a suite.

## Suggested directions

1. **A test-only revocation hook.** The precedent already exists: `POST /queue/{queueId}/test-unsafe-unload`
   ([QueueController.cs:575](../../../server/src/DaprMQ.ApiServer/Controllers/QueueController.cs#L575))
   is a test-only endpoint on the queue actor. An equivalent that force-expires a session's lease
   record on the coordinator would make `C-09` a three-line test in every SDK. Cheapest option, and
   consistent with how the project already handles this.
2. **Fault injection at the invoker.** Make the coordinator invoker substitutable so an integration
   build can fail one `RenewSessionLease`. More faithful, considerably more plumbing, and it only
   covers path (1) of the two.
3. **Leave it to unit tests and say so.** Each SDK already covers the `SessionLost` → exception
   mapping against a fake stream. Mark `C-09` permanently out of scope for integration and record
   why. Honest, but it leaves the server's own emit path untested end to end.

**(3) was taken** — see Resolution below. (1) remains the fallback if this decision is revisited;
it is cheap and still has the precedent of `test-unsafe-unload` to follow.

## Acceptance criteria (for direction 3, as resolved)

- `C-09` marked permanently out of scope for integration (🚫, not ⬜) in every SDK column of
  [INTEGRATION_TESTS.md](../../../sdks/testing/INTEGRATION_TESTS.md), with the reasoning recorded
  there rather than only in this doc.
- Each SDK's integration suite documents, at the `C-09` test site, why the scenario is absent
  rather than leaving a silent gap — .NET's is
  [ConsumeSessionTests.cs:327](../../../sdks/dotnet/tests/DaprMQ.Client.IntegrationTests/ConsumeSessionTests.cs#L327).
- Unit-level coverage of the `SessionLost` → exception mapping, and of `SessionQueueConsumer`
  treating it as a claimed-then-lost session (resetting backoff, not treating it as a failed
  claim — see the `catch (SessionLostException)` branch in
  [SessionQueueConsumer.cs](../../../sdks/dotnet/src/DaprMQ.Client/SessionQueueConsumer.cs)), stays in
  place as the only coverage for this path; it is not a substitute for integration coverage of the
  server's own emit path, which remains untested end to end.

<details>
<summary>Original acceptance criteria (for direction 1, not pursued)</summary>

- A supported, test-only way to invalidate a live `ConsumeSession` stream's lease.
- `C-09` implemented and ticked for .NET, with the shape documented well enough that the Python,
  TypeScript and Java columns can follow it.
- The test asserts the consumer surfaces the SDK's `SessionLost` equivalent, and that
  `SessionQueueConsumer` treats it as a claimed-then-lost session (resetting backoff, not treating
  it as a failed claim) — see the `catch (SessionLostException)` branch in
  [SessionQueueConsumer.cs](../../../sdks/dotnet/src/DaprMQ.Client/SessionQueueConsumer.cs).

</details>

---

## Resolution

**Won't fix — direction 3 taken** (decided 2026-10-01). No test-only lease-revocation seam was
built. The only test-only endpoint on the server is still `POST /queue/{queueId}/test-unsafe-unload`
([QueueController.cs:575](../../../server/src/DaprMQ.ApiServer/Controllers/QueueController.cs#L575)),
which unloads a queue actor and cannot invalidate a session lease on the coordinator.

Rather than building direction (1), `C-09` is now recorded as permanently out of scope for
integration in every SDK column of
[INTEGRATION_TESTS.md](../../../sdks/testing/INTEGRATION_TESTS.md) (🚫, under "Permanently
out-of-scope rows"), and
[ConsumeSessionTests.cs:327](../../../sdks/dotnet/tests/DaprMQ.Client.IntegrationTests/ConsumeSessionTests.cs#L327)
spells out why. The `SessionLost` → exception mapping stays covered at the unit level only, per
SDK (.NET: `DaprMQClientConsumeSessionTests.ConsumeSessionAsync_SessionLostFrame_ThrowsSessionLostException`).

This trades end-to-end coverage of the server's emit path for not carrying a coin-flip-reliability
test (or the plumbing of direction 1 or 2) indefinitely. If a server-side test seam is ever added
for other reasons, direction (1) should be revisited and this row unblocked.
