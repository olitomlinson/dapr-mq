# ADR 0003: Disable Dapr actor reentrancy

- **Status:** Accepted
- **Date:** 2026-10-19
- **Supersedes:** [0002](0002-enable-actor-reentrancy.md)

## Context

Reentrancy was turned on (ADR 0002) for one call chain. A session `QueueActor` registered with its
`SessionCoordinatorActor` in `OnActivateAsync`, so a coordinator call to a cold session actor ran
`SessionCoordinatorActor` → `QueueActor` → `SessionCoordinatorActor`, which deadlocks without reentrancy.

That chain no longer exists. Since 2b85dbe (2026-10-15) a session actor registers in `AfterActorMethodAsync`, after a
method rather than on activation, and skips `SetSessionLease` and `ClearSessionLease` - the only calls the coordinator
makes into a queue actor ([QueueActor.cs](../../server/src/DaprMQ/QueueActor.cs)). `RegisterSession` makes no
outbound calls. No other actor calls back into its caller: HttpSink → Queue, Topic → Queue / HttpSink, and
Queue → its DLQ queue / BlobReaper / Coordinator.

Reentrancy has a cost on every call. With it on, daprd gives each method call a reentrancy id, and `Dapr.Actors` gives
each id a fresh, empty state tracker. Every call therefore re-reads `metadata`, segments, lock rows and lock-index
buckets from the state store, rather than from the actor's cache.

## Decision

Turn reentrancy off for every actor type: the `ReentrancyConfig` line is removed from
[Program.cs](../../server/src/DaprMQ.ApiServer/Program.cs), and the legacy `Actor.Reentrancy` feature flag from the
Dapr configurations ([config.yml](../../server/dapr/config/config.yml),
[dapr-configuration.yaml](../../helm/templates/dapr-configuration.yaml)).

## Consequences

- State read in one call is served from the actor's cache in the next, for the life of the activation. On kind,
  `tools/k8s-throughput` (20 replicas × 10 handlers, window 20, 1 ms work, 100k messages) drained in 261 s and 254 s,
  against 284 s and 281 s with reentrancy on, with 25% fewer state-table scans (340k against 453k).
- An A → B → A chain added later deadlocks until the actor call times out. `SessionReentrancyTests` still covers the
  session chain (a coordinator call into a cold session actor). A new call from a callee back to its caller needs
  either reentrancy back on or to be moved out of the calling turn.
- Cycles between separate call chains were never solved by reentrancy; `RegisterSessionTimeout` bounds the session one.
  (Update, 2026-10-25: session actors no longer call the coordinator at all. Their worker's reporter registers them
  outside the turn, so that cycle and `RegisterSessionTimeout` are gone; see
  [async-session-registration.md](../../proposals/async-session-registration.md).)
- `ActorTurnGuard` (dapr/dapr#10835) stayed: the overlap happened with reentrancy on and off. (Update, 2026-10-26:
  the Dapr 1.19.0 runtime fixes #10835, so the guard is removed.)
