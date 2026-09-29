# A consumer never reaches any session beyond its first `MaxConcurrentSessions`

**Status:** Confirmed, reproduced on demand (not intermittent). Found while implementing the .NET
SDK integration matrix.

**Severity:** High. This is a correctness problem for the headline use case of the sessions
feature — "different sessions on the same queue can be consumed in parallel" — not a performance
nit. Any deployment with more sessions than consumer slots silently drops the excess on the floor.

**Blocks:** `K-11` in [sdks/testing/INTEGRATION_TESTS.md](../../sdks/testing/INTEGRATION_TESTS.md)
(currently `[Fact(Skip = …)]` in
[SessionQueueConsumerTests.cs](../../sdks/dotnet/tests/DaprMQ.Client.IntegrationTests/SessionQueueConsumerTests.cs)),
and constrains the shape of `K-07`.

---

## Summary

A `SessionQueueConsumer` consumes its first `MaxConcurrentSessions` sessions and then never
advances. Once a slot drains its session, it re-claims **the same, now-empty session** on its next
loop, idles out, releases, and re-claims it again — indefinitely. Sessions further down the queue's
session directory are never consumed at all, no matter how long the consumer runs and no matter how
many items they hold.

Nothing errors. The consumer looks healthy, the drained session keeps being claimed and released on
schedule, and the starved sessions simply sit there.

## Reproduction

Measured directly, not inferred:

- One queue, three sessions (`alpha`, `beta`, `gamma`), two items each.
- A `SessionQueueConsumer` with `MaxConcurrentSessions = 1`, `SessionIdleTimeoutSeconds = 2`,
  `PrefetchCount = 1`, left running for 30 seconds.

Result:

```
handled (2): alpha/1, alpha/2
distinct sessions: alpha
```

`alpha` drained in about a second. For the remaining ~29 seconds the single slot cycled on `alpha`
— claim, idle 2s, drain, release, re-claim — and never once touched `beta` or `gamma`.

The same shape at larger scale: with `MaxConcurrentSessions = 2` and six sessions, only the first
two are ever worked.

## Root cause

Two behaviours combine.

**1. An untargeted claim takes the first *free* session, not the first session with work.**
[SessionCoordinatorActor.cs:186-196](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L186-L196):

```csharp
// Any-available claim: first directory entry with no live lease.
string? found = null;
foreach (var candidate in metadata.SessionDirectory.Keys)
{
    var candidateLock = await StateManager.TryGetStateAsync<SessionLockState>($"session-lock_{candidate}");
    if (!candidateLock.HasValue || now >= candidateLock.Value.ExpiresAt)
    {
        found = candidate;
        break;
    }
}
```

The only test is "does this session have a live lease". Emptiness is never consulted, and iteration
always restarts from the front of `SessionDirectory` — so whoever is earliest in directory order
and unleased wins every single claim.

**2. A drained session stays in the directory for a long time.** Eviction requires two
consecutive confirmed-empty sweeps at least `SecondPassDelaySeconds` apart, and that constant is
300 seconds ([SessionCoordinatorActor.cs:83-87](../../server/src/DaprMQ/SessionCoordinatorActor.cs#L83-L87)).
So the session a slot just emptied remains first in line, and is handed straight back to it.

The consumer side is not at fault. `SessionQueueConsumer`'s slot loop does exactly what it should:
the stream ends on idle-drain, the slot resets its backoff and immediately claims again. `C-06`
proves the idle timeout itself works correctly — the stream really does end and the session really
is released. The claim that follows is what picks the wrong session.

## Suggested directions

Not prescriptive — whoever picks this up should weigh these against the sweep's design.

1. **Prefer a non-empty session.** Have the any-available branch skip candidates already known to
   be empty. The sweep already has an emptiness probe (`IsSessionEmptyAsync`), but calling it
   per-candidate on the claim path would put a cross-actor round trip into the hot path, so this
   probably wants a cheap cached hint on the directory entry rather than a live check.
2. **Rotate.** Keep a round-robin cursor so consecutive untargeted claims don't all start from the
   front of the directory. Cheap, and it bounds the damage regardless of emptiness information,
   but it only spreads the starvation rather than removing it.
3. **Evict drained sessions promptly.** Attack it from the sweep side instead. Riskier: the two-
   confirmation delay exists to avoid evicting a session that is about to receive more items.

(1) and (2) are complementary and probably both worth having.

## Acceptance criteria

- Unskip `K11_SessionIdleTimeout_LetsTheConsumerMoveOnToAnotherSession` and tick `K-11` for .NET.
- A `MaxConcurrentSessions = 1` consumer against N sessions drains all N.
- `K-07` can go back to asserting on a full drain rather than a fixed observation window — see the
  comment in that test explaining why it currently cannot.
