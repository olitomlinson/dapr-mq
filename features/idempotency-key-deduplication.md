# Feature: Idempotency Key Deduplication

## Overview

A producer can optionally attach a client-supplied **idempotency key** to a message when enqueuing directly to a queue or publishing to a Topic. If that key was already used within a configurable TTL window (default 24 hours), the message is silently **not** placed on the destination queue a second time — at-most-once delivery per key, enforced at the point of submission rather than left entirely to the consumer.

This complements, rather than replaces, the existing consumer-side idempotency guidance for HTTP Sink redelivery (see [API_REFERENCE.md](../docs/API_REFERENCE.md#delivery-behavior-by-response-status)): that's about tolerating redelivery of an item already on the queue; this is about stopping a duplicate submission from landing on the queue in the first place.

## Use Cases

- A payment or order service retries a request (network timeout, client-side retry logic) and must not enqueue the same charge/order twice.
- A producer batch-enqueues with a `\n`-delimited source file and wants replays of that file to be a no-op rather than double-processing.
- A Topic publisher wants at-most-once delivery per subscriber, but different subscribers have different tolerances for duplicates — some are naturally idempotent downstream and don't need it, others (billing-type consumers) must never double-process.

## Architecture

### Why one state entry per key, not a shared array

An earlier design considered bucket-sharding a "seen keys" list across N array-valued state keys, to avoid a single state key holding an ever-growing array. That approach was dropped: it just relocates the same "big array" problem to N smaller-but-still-growing arrays, and creates a painful shard-count-migration problem if N ever needs to change.

Instead, each idempotency key gets its **own** Dapr actor-state entry, using the Dapr.Actors SDK's native per-entry TTL (`IActorStateManager.SetStateAsync<T>(string, T, TimeSpan, CancellationToken)`). This directly mirrors the codebase's existing one-entry-per-lock convention (`{lockId}-lock` in `QueueActor.cs`) rather than inventing a new bucketing scheme. Point lookups replace bucket-array scans, and expiry is enforced natively by the Postgres v2 state store rather than by a reminder-based reaper — so it never interacts with the actor's turn-based concurrency.

### State Key Structure

```
idem_{idempotencyKey}
```

The **raw**, client-supplied key is used directly — not hashed. The Dapr Postgres v2 state store's `key` column is `text` (unbounded, not `varchar(N)`), every query is parameterized, and Dapr never re-parses a composed actor-state key apart (it constructs and compares the same opaque string on every write and read), so there's no routing or injection reason to hash it the way `HashBlobReference` hashes an `ActorId` (which *is* embedded in an HTTP path). Hashing would also make the common case worse: a typical 36-character UUID key becomes a fixed 64-character hex string once hashed — roughly 50% more memory per entry for no benefit.

The two real constraints are handled by validation instead of hashing:
- Postgres `text` columns reject embedded NUL bytes.
- A B-tree primary-key index has a practical ~2.7KB per-row size ceiling.

`QueueActor` validates every `IdempotencyKey` up front, alongside the existing `ItemJson`/`Priority` checks: max 128 characters (comfortably covers UUIDs, prefixed UUIDs, and composite keys), and no control characters (which also covers NUL). An invalid key fails the whole `Enqueue` batch the same way an empty `ItemJson` does — it's a client error, unlike a genuine duplicate, which is expected, valid behavior and only skips that one item.

The state value is a minimal marker:

```csharp
public record IdempotencyMarker
{
    public required double CreatedAt { get; init; } // debugging only; TTL enforcement is native
}
```

### Enqueue Flow

Inside `QueueActor.Enqueue`, for each item carrying a non-empty `IdempotencyKey` (and only if this queue's dedup is enabled — see [Per-Subscriber Dedup Toggle](#per-subscriber-dedup-toggle) below):

1. `TryGetStateAsync<IdempotencyMarker>("idem_{key}")`. If found, the item is skipped (`ItemsDeduplicated++`) and processing moves to the next item — `EnqueueInternal` is never called for it.
2. If not found, stage `SetStateAsync("idem_{key}", marker, ttl: TimeSpan.FromSeconds(TtlSeconds))`, then proceed with the normal enqueue.

Because Dapr's actor-state tracker reflects staged-but-uncommitted changes within the same turn, two occurrences of the *same* key within a single `Enqueue` call's item list are correctly deduped against each other — the first stages the marker, the second sees it and is skipped. The marker write is staged, not saved, alongside everything else in the batch, and commits atomically at the existing single `SaveStateAsync()` call at the end of `Enqueue`. If `Enqueue` throws before reaching that point, nothing — including the idempotency marker — persists, so a failed enqueue never burns a key.

`EnqueueInternal`'s other caller — the DLQ/lock-expiry-reminder requeue path inside `ReceiveReminderAsync` — is untouched. A requeue after lock expiry is not a new client submission and must not consult or write idempotency state.

### Bounding Actor Memory

Dapr's `IActorStateManager` tracks a `TTLExpireTime` per cached entry and re-validates it against wall-clock time on every read — so a stale, already-committed idempotency marker never incorrectly reports "still a duplicate" past its TTL, even for a continuously-active actor that never deactivates. However, the tracker never *removes* a committed entry on its own; it only marks it unmodified. For a busy, continuously-active queue actor handling many distinct keys, that dictionary would otherwise grow without bound for as long as the actor stays activated (bounded only by `ActorIdleTimeout`, which a hot queue may rarely hit).

`QueueActor` mitigates this the same way it already bounds memory for offloaded segments: right after `SaveStateAsync()` commits, it best-effort `UnloadStateAsync`s every `idem_*` key touched during the batch. This is gated by `IdempotencyConfig.UnloadAfterCommit` (default `true`) — a genuine memory-vs-latency trade-off:

- **`true` (default)** — actor memory stays bounded to roughly the keys touched by the in-flight batch. The cost: the next time an already-seen key is rechecked in a later `Enqueue` call, it's a real state-store round-trip instead of a free in-memory hit.
- **`false`** — every checked key stays resident for fast rechecks, but grows **unbounded** for as long as the actor stays continuously active — not "up to the TTL window," since nothing prunes an expired-but-cached entry until it's read again. Only appropriate for low-key-cardinality, latency-sensitive workloads.

### Per-Subscriber Dedup Toggle

A publisher always passes the idempotency key through to every subscriber (`TopicActor.Publish`/relay are unchanged — `EnqueueItem` carries the key by reference all the way to each subscriber's own `Enqueue` call). Whether a given destination queue *acts* on it is a separate, per-queue setting:

```csharp
public record MetadataConfig
{
    ...
    public bool DedupEnabled { get; init; } = true;
}
```

`QueueActor.ConfigureDedup(ConfigureDedupRequest { Enabled })` toggles it — a general-purpose actor method, not Topic-specific. `TopicActor.Subscribe` calls it best-effort (same failure-tolerant pattern as its existing HTTP sink initialization) when a subscriber opts out via `dedupEnabled: false` on the subscribe request. This lets different subscribers on the same topic make independent choices: one might need strict at-most-once delivery, another (an audit-log consumer, say) might want every delivery including duplicates.

## Configuration

| Env var | Default | Description |
|---|---|---|
| `IDEMPOTENCY_KEY_TTL_SECONDS` | `86400` (24h) | Global default TTL. Single process-wide value — no per-request override. |
| `IDEMPOTENCY_KEY_UNLOAD_AFTER_COMMIT` | `true` | See [Bounding Actor Memory](#bounding-actor-memory) above. |

The Postgres v2 state store component (`server/dapr/components/statestore-postgres.yaml` and its mirrors) also gained `cleanupIntervalInSeconds: "3600"` — without it, TTL-expired rows are filtered out at query time but never physically purged, so they'd accumulate on disk indefinitely.

## API Changes

- `EnqueueItem` / `ApiEnqueueItem` (HTTP `enqueue`, `enqueue-object`, gRPC `Enqueue`, Topic `publish` — all share this type): new optional `idempotencyKey` field. `enqueue-object` (no JSON body) carries it via an `idempotency-key` header instead.
- `EnqueueResponse` / `ApiEnqueueResponse`: new `itemsDeduplicated` field. `success` stays `true` when items are deduped — it's not an error.
- `SubscribeRequest` / `ApiSubscribeRequest`: new optional `dedupEnabled` field (nullable — omitting it leaves the provisioned queue's default, dedup enabled, untouched).

See [API_REFERENCE.md](../docs/API_REFERENCE.md) for full request/response shapes.
