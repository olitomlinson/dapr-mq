# ADR 0002: Enable Dapr actor reentrancy

- **Status:** Accepted
- **Date:** 2026-09-23

## Context

A session `QueueActor` registers itself with its `SessionCoordinatorActor` on every activation. When the coordinator
calls a cold session actor, activation calls back into the coordinator mid-turn: `SessionCoordinatorActor` →
`QueueActor` → `SessionCoordinatorActor`. Non-reentrant actors deadlock on this A → B → A chain.

Dapr's documented fix is actor reentrancy. It had been left off because turning it on stopped `TopicActor`'s
reminder-driven publish relay. The cause was a `Dapr.Actors` client-side caching bug: reminder, timer and activation
callbacks read state through a default tracker that was not updated when a reentrant method call wrote the same key,
so the relay read stale state and did nothing
([reentrancy-breaks-reminder-relay.md](../issues/resolved/reentrancy-breaks-reminder-relay.md)).

## Decision

Enable reentrancy for every actor type: `options.ReentrancyConfig = new ActorReentrancyConfig { Enabled = true }` in
[Program.cs](../../server/src/DaprMQ.ApiServer/Program.cs).

The SDK bug was first worked around with a locally built `Dapr.Actors`. Two fixes were compared: evict-and-reload
(drop the default tracker's copy so the next read goes to the state store) and refresh-in-place (update the copy with
the value just saved). Refresh-in-place made fewer state-store reads and was more stable between runs
([reentrancy-fix-empirical-comparison.md](../issues/resolved/reentrancy-fix-empirical-comparison.md)), and is what
upstream shipped in [dapr/dotnet-sdk#1912](https://github.com/dapr/dotnet-sdk/pull/1912) (1.18.9). DaprMQ now uses
the official `Dapr.Actors` 1.18.10 with no local package source
([sdk-1.18.9-retest.md](../issues/resolved/sdk-1.18.9-retest.md)).

## Consequences

- The cold-session A → B → A deadlock is gone. `SessionReentrancyTests` forces the session actor cold (via
  `ACTOR_IDLE_TIMEOUT_SECONDS` / `ACTOR_SCAN_INTERVAL_SECONDS`) to cover it.
- `Dapr.Actors` must stay at 1.18.10 or later. Earlier versions bring back the stale-reminder bug (before 1.18.9) or
  the not-found sync regression (1.18.9).
- Reentrancy only lets a call chain re-enter itself; separate chains are still serialised per actor. Code must not
  assume a reentrant call sees writes another chain has not yet saved.
- Turning reentrancy off brings the deadlock back. Don't disable it to chase state-store load; see the triage notes in
  [reentrancy-fix-round-trip-impact.md](../issues/resolved/reentrancy-fix-round-trip-impact.md) first.
