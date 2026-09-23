<!--
Draft for a new issue on dapr/dotnet-sdk. Not posted yet - review and edit before it goes anywhere.
Unrelated to PR #1908 - this affects the SDK unconditionally, with or without reentrancy enabled.
-->

## Title

Actor state manager never caches "key not found" - every check for an absent key round-trips forever

## Body

### Summary

`ActorStateManager`'s state tracker caches the result of a state read once it's fetched from the
runtime - but only if the key exists. If a key doesn't exist, `TryGetStateAsync` returns
`(false, default)` without adding anything to the tracker, so the *next* check for that same key
is another full round trip to the state store, indefinitely. There's no "confirmed absent" cache
state - only positive results are ever cached.

This means any code that periodically checks for something that's usually or currently absent -
circuit-breaker state, existence checks, idempotency lookups, "has this been claimed yet" checks -
pays a full state-store round trip on every single check, for as long as the key stays absent, no
matter how many times it's asked. It's not a correctness issue (nothing returns stale or wrong
data), but it's a real, avoidable cost that scales with how often that code path runs.

### Where

`src/Dapr.Actors/Runtime/ActorStateManager.cs`, `TryGetStateAsync`:

```csharp
public async Task<ConditionalValue<T>> TryGetStateAsync<T>(string stateName, CancellationToken cancellationToken)
{
    ...
    if (stateChangeTracker.ContainsKey(stateName))
    {
        // cache hit path - only reachable if something was cached before
    }

    var conditionalResult = await this.TryGetStateFromStateProviderAsync<T>(stateName, cancellationToken);
    if (conditionalResult.HasValue)
    {
        stateChangeTracker.Add(stateName, StateMetadata.Create(conditionalResult.Value.Value, StateChangeKind.None, ttlExpireTime: conditionalResult.Value.TTLExpireTime));
        return new ConditionalValue<T>(true, conditionalResult.Value.Value);
    }

    // <-- key not found: returns directly, nothing added to stateChangeTracker
    return new ConditionalValue<T>(false, default);
}
```

The same applies to `ContainsStateAsync` (`DaprStateProvider.cs`), which internally just calls
`GetStateAsync` and checks whether the result is empty - so it inherits the same non-caching
behavior for negative results.

### Reproduction / measurement

Observed while comparing two Dapr.Actors builds for an unrelated reentrancy investigation
(actor reentrancy + reminder relay). In a test actor with a circuit-breaker-style key that's
checked every ~1s and never actually written (no failures occur), the key was expected to be
read once (not found), then served from a "confirmed absent" cache on every subsequent check.
Instead, captured via `pg_stat_statements` and raw Postgres statement logging
(`log_statement=all`), the same never-existing key was read from the state store **on every
single check**, over 20 checks in a ~20s window - 44 round trips for what should have needed at
most 1 (or 0, given the key is confirmed absent from the very first check).

Confirmed via the raw statement log: the key in question was *only ever the target of SELECTs*
across the whole run - no write, no delete - ruling out "it got created and removed each time" as
an alternative explanation. It genuinely never existed, and every check still round-tripped.

### Suggested fix

Cache negative results too, distinguished from "not yet checked." This needs a representation for
"confirmed absent" that's distinct from "unknown" (currently the tracker just doesn't have an
entry for either case) - e.g. a new `StateChangeKind` value (`ConfirmedAbsent`/`NotFound`), or a
sentinel on the existing `StateMetadata.Value`. `TryGetStateAsync` would then check that cached
"confirmed absent" state and return `(false, default)` without a round trip, the same way it
already returns cached positive values without one. Cache invalidation on a subsequent write
already exists as a mechanism (see #1908's `InvalidateDefaultTracker`) and could be extended to
also invalidate a "confirmed absent" entry once something writes that key.

### Scope note

This is unrelated to reentrancy or to PR #1908 - it reproduces identically with reentrancy
disabled entirely, and affects `defaultTracker` and any reentrancy-scoped tracker equally. Filing
separately since it's a distinct, more broadly-applicable gap.
