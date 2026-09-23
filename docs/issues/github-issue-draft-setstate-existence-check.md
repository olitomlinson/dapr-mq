<!--
Draft for a new issue on dapr/dotnet-sdk. Not posted yet - review and edit before it goes anywhere.
Framed as a design tradeoff worth discussing, not a bug report - unlike #1909, there's a real
reason for the current behavior and removing it isn't a strict improvement.
-->

## Title

Discussion: SetStateAsync's existence check costs a round trip on every new key, to optimize a case that may be rare

## Body

Related to #1909, found during the same investigation, but a different kind of finding - this one
is a tradeoff worth discussing, not a clear-cut fix.

### What's happening

`SetStateAsync`, for a key not yet touched by the current call's own tracker, calls
`ContainsStateAsync` before staging the write:

```csharp
if (stateChangeTracker.ContainsKey(stateName))
{
    ...
}
else if (await this.actor.Host.StateProvider.ContainsStateAsync(this.actorTypeName, this.actor.Id.ToString(), stateName, cancellationToken))
{
    stateChangeTracker.Add(stateName, StateMetadata.Create(value, StateChangeKind.Update));
}
else
{
    stateChangeTracker[stateName] = StateMetadata.Create(value, StateChangeKind.Add);
}
```

`ContainsStateAsync` is a full round trip to the state store (internally just `GetStateAsync`,
checking whether the result is empty). So every `SetStateAsync` call on a key the current
transaction hasn't touched yet pays a full read, purely to classify the key as `Add` or `Update`.

### Why this seems avoidable

On the wire, the classification doesn't matter: `DaprStateProvider.cs` maps both `StateChangeKind
.Add` and `.Update` to the same `"upsert"` operation - Dapr's actor state transaction API doesn't
distinguish insert from update. So for `SetStateAsync` in isolation, the round trip buys nothing
observable.

The classification *is* used elsewhere, though: `RemoveStateAsync`, if it finds a locally-tracked
key already classified `Add`, drops the local entry entirely instead of staging an explicit
delete - since an `Add`-classified key was never persisted, there's nothing to delete server-side:

```csharp
case StateChangeKind.Add:
    stateChangeTracker.Remove(stateName);
    return true;
```

So the existence check in `SetStateAsync` is there to support this: if a key is `Set` and then
`Remove`d within the same uncommitted transaction, correctly knowing it was never persisted lets
the SDK skip sending anything to Dapr for it at all.

### The tradeoff

This means `SetStateAsync` pays a round trip on *every* new key, up front, to optimize the case
where that same key later gets removed in the same transaction. For the (probably far more common)
case - set a key once, never remove it in the same transaction - that round trip is pure overhead.

Measured in an actor that writes 3 brand-new keys per call with no matching removes: 3 avoidable
round trips per call, on top of whatever else that call reads. Over 20 calls in one of our tests,
that's 60 queries attributable to this pattern alone.

### A possible lazier alternative

Introduce a third `StateChangeKind` - e.g. `UnverifiedAdd` - distinct from both `Add` (confirmed
new) and `Update` (confirmed pre-existing). The name matters here: it should say what's actually
different about it, not just that it's another flavor of "new." `Add` is a fact the SDK has
checked; `UnverifiedAdd` is a guess it hasn't:

- `SetStateAsync`, for a key not yet locally tracked, stages it as `UnverifiedAdd` with **no round
  trip**.
- If nothing removes that key before `SaveStateAsync`, `UnverifiedAdd` is treated the same as
  `Update` at commit time - safe, since `Add` and `Update` produce the same `"upsert"` wire
  operation anyway.
- If a same-transaction `RemoveStateAsync` *does* target that key, **that's** the point where the
  real `ContainsStateAsync` check finally happens - only now do we need to know whether it
  genuinely pre-existed, to decide between dropping the local entry (never persisted) or staging
  an explicit delete (it was really there).

Keeping this separate from `Add` rather than reusing it matters: `Add` is also produced by
`AddStateAsync`/`TryAddStateAsync`, which has its own hard requirement to verify eagerly regardless
(it must know synchronously whether to throw `InvalidOperationException` on a duplicate).
`RemoveStateAsync`'s existing fast path for `Add` (drop the local entry, no check) relies on `Add`
always meaning "verified" - if `SetStateAsync` started producing unverified `Add`s too, that fast
path would no longer be safe for *either* caller. A distinctly-named `UnverifiedAdd` leaves `Add`'s
existing meaning and `RemoveStateAsync`'s existing fast path for it untouched, and adds one new
case that always resolves lazily instead of ever being trusted outright.

This preserves correctness (the existence question is still answered with a real check before any
decision that depends on the answer - never guessed and trusted blindly) while moving the round
trip from "every new key, always" to "only when a same-transaction remove of that exact key
actually happens." The common case (set once, never removed in the same transaction) goes from 1
round trip to 0; the rare case (set then removed) keeps its existing optimization, just paid for
lazily instead of eagerly.

We don't have strong data on how often the set-then-remove-same-transaction pattern actually
occurs across real Dapr actor usage, so we're filing this as a discussion of a plausible design
rather than a ready-to-merge proposal - there may be complexity in the tracker/transaction-building
code we're not accounting for from outside the codebase.
