# Empirical comparison: evict-and-reload vs. refresh-in-place

**Status:** Empirical validation of the theory in `docs/REENTRANCY_FIX_ROUND_TRIP_IMPACT.md`. Answers
"does the refre◊sh-in-place alternative actually cause fewer Postgres reads, and by how much" with
measured numbers, not just code-level reasoning.

**What's being compared:**
- `1.18.4-reentrancyfix.1` - upstream `dapr/dotnet-sdk` PR [#1908](https://github.com/dapr/dotnet-sdk/pull/1908)
  as-is (evict-and-reload: drops `defaultTracker`'s clean copy of a key on a cross-tracker write,
  forcing the next read to reload from the state store).
- `1.18.4-refreshfix.1` - the alternative on branch `reentrancy-refresh-in-place` in
  [olitomlinson/dotnet-sdk](https://github.com/olitomlinson/dotnet-sdk/tree/reentrancy-refresh-in-place)
  (refresh-in-place: updates `defaultTracker`'s entry with the value `SaveStateAsync` just
  confirmed persisted, instead of dropping it).

Both are built from `server/nuget-local/`, wired into DaprMQ via `server/NuGet.config`.

---

## Method

### Instrumentation

Two complementary tools, both via a dedicated Postgres container config (added to
`DaprTestEnvironment`, `server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs`):

1. **`pg_stat_statements`** (`shared_preload_libraries=pg_stat_statements`, `CREATE EXTENSION`
   after startup) - gives an exact *count* of how many times a given normalized query ran.
   `GetActorStateSelectCallCountAsync()` sums `calls` for every `SELECT` against the actor state
   table; `GetActorStateSelectBreakdownAsync()` lists each distinct normalized query text.
   Limitation: Dapr's postgresql/v2 state store issues every single-key read (and the
   `ContainsStateAsync` existence check `SetStateAsync` does for a brand-new key) through the
   *exact same* parameterized query, `SELECT value, etag, expires_at FROM daprmq_state WHERE
   key = $1 ...` - Postgres normalizes away the bound key value, so `pg_stat_statements` collapses
   everything into one row. It answers "how many," not "which."

2. **Raw statement logging** (`log_statement=all`) - `GetPostgresLogsAsync()` shells out to
   `docker logs <container-id>` directly (not `IContainer.GetLogsAsync`, which was tried first and
   found to return only an early, truncated snapshot cutting off before any app traffic - a
   Testcontainers limitation, not a Postgres one). With `log_statement=all`, every query's
   `DETAIL: parameters: $1 = '...'` log line includes the actual bound key value. Dapr's
   postgresql/v2 keys are `{appId}||{actorType}||{actorId}||{stateName}`, so grouping DETAIL lines
   by the last `||`-delimited segment reveals exactly which state name each query touched - this
   is what "which," not just "how many."

### Build-arg-parametrized image

`server/Dockerfile` accepts `--build-arg DaprActorsSdkVersion=...`, threaded through to
`dotnet restore`/`publish -p:DaprActorsSdkVersion=...`. The three `.csproj` files
(`DaprMQ.csproj`, `DaprMQ.Interfaces.csproj`, `DaprMQ.ApiServer.csproj`) resolve
`Version="$(DaprActorsSdkVersion)"` with a property-group default, so a plain build still works
unchanged. This lets one Dockerfile produce both comparison images without hand-editing package
references:

```bash
docker build --build-arg DaprActorsSdkVersion=1.18.4-reentrancyfix.1 -t daprmq-api:reentrancyfix .
docker build --build-arg DaprActorsSdkVersion=1.18.4-refreshfix.1 -t daprmq-api:refreshfix .
```

### Scenario

`server/tests/DaprMQ.IntegrationTests/Tests/QueryCountComparisonTest.cs` - deliberately the
*realistic* case, not the SessionReentrancyTests cold-actor case (which is a single, one-shot
event): a `TopicActor` whose relay reminder self-reschedules every 1s while there's pending work
(`TopicActor.cs:658-667`), published to repeatedly so ordinary `Publish()` calls interleave with
reminder ticks on the same `MetadataKey`. Runs its own dedicated `DaprTestEnvironment` per image
(not the shared `"Dapr Collection"` fixture) since it needs to run the identical scenario against
two different app images and compare:

1. Subscribe 2 subscribers, publish once to start the relay ticking, let it settle (2s).
2. Reset query stats.
3. For `RelayCycles` iterations: publish, wait 1s (interleaving with the ~1s tick cadence).
4. Wait for the final tick to process, then read the counters/dump the log.

---

## Results

### Stability (RelayCycles = 20, 3 runs)

| Run | evict-and-reload (`reentrancyfix`) | refresh-in-place (`refreshfix`) | diff |
|---|---|---|---|
| 1 | 273 | 262 | 11 |
| 2 | 282 | 262 | 20 |
| 3 | 282 | 264 | 18 |

`refreshfix`'s count is consistently more stable (262, 262, 264) than `reentrancyfix`'s (273, 282,
282). This tracks with the mechanism: the evict build's extra cost depends on the exact timing
interleave between a tick and the write before it (jitter-sensitive), while the refresh build's
count doesn't depend on that timing at all - it never needs to ask "was this written since I last
read it."

### Full breakdown by state name (one representative run, RelayCycles = 20)

| State name | `reentrancyfix` | `refreshfix` | diff |
|---|---|---|---|
| **`metadata`** | **92** | **71** | **21** |
| `queue_1_seg_0` | 44 | 44 | 0 |
| `circuit_sub-a` | 44 | 44 | 0 |
| `circuit_sub-b` | 44 | 44 | 0 |
| `item_0` … `item_20` (21 keys) | 2 each | 2 each | 0 |
| `publish_<guid>` (20 keys) | 1 each | 1 each | 0 |
| `publish-seq_N` (21 keys) | 1 each | 1 each | 0 |
| generation / lock / migration bookkeeping | small, equal | small, equal | 0 |

**`metadata` is the only key that differs at all.** The 21-read diff on that one key accounts for
essentially the entire measured total diff (20-21, matching within the rounding of the smaller
counts). Every other key - 21 distinct item keys, 20 publish-record keys, 21 publish-sequence
keys, both subscribers' circuit-breaker state, the downstream subscriber queue's own segment - is
byte-for-byte identical between the two builds. The fix (and its alternative) changes exactly one
thing and nothing else; nothing is silently leaking into unrelated keys.

---

## Two side findings (apply equally to both builds - not part of the fix comparison)

Found while explaining why the *total* read count (~262-282) is much larger than the
fix-attributable diff (~9-21) - most of the total is build-independent baseline overhead:

1. **`SetStateAsync`'s existence check is a full read, and it's paying for an optimization that
   rarely applies.** For a key not yet touched by the current call's own tracker, `SetStateAsync`
   calls `ContainsStateAsync`, which internally just calls the same `GetStateAsync` used for reads
   (`DaprStateProvider.cs:73-77`) - a full round trip to classify the key as `Add` or `Update`.
   On the wire this classification is provably irrelevant: `DaprStateProvider.cs:170-172` maps
   *both* `Add` and `Update` to the same `"upsert"` operation - Dapr's actor state transaction API
   doesn't distinguish insert from update at all. The classification only matters for a different
   method, `AddStateAsync` (`ActorStateManager.cs:61-69`), which genuinely needs it to throw when
   a key already exists, and for one specific optimization: if a key is `Set` and then `Remove`d
   within the same uncommitted transaction, an `Add`-classified key's removal can just drop the
   local tracker entry with nothing sent to Dapr at all (`ActorStateManager.cs:279-282`), since
   nothing was ever persisted - whereas an `Update`-classified key's removal must still stage an
   explicit delete. `SetStateAsync` pays the round trip up front on the chance that pattern
   happens later in the same call. `Publish()` never does a matching remove, so all three of its
   existence checks (item, publish record, publish-seq) buy nothing - plus the metadata read, 4
   real queries per publish, regardless of SDK build. Over 20 publishes that's 80 queries, the
   largest single contributor to the total.

2. **Reads of a key that doesn't exist are never cached, in any tracker, regardless of
   reentrancy.** `circuit_sub-a`/`circuit_sub-b` were expected to be read once and stay cached
   (nothing writes them in this scenario - no failures ever happen, so `RecordFailureAsync` never
   runs) - but measured at 44 reads each, not ~2. Checked the raw per-statement log: `circuit_sub-a`
   is *only ever read* across the whole run - no `DELETE`, no write with a `$2` value anywhere -
   confirming the key genuinely never exists in the backend the entire time. The actual cause is
   `TryGetStateAsync` (`ActorStateManager.cs:178-185`): when the backend reports "not found," it
   returns `(false, default)` directly without ever adding anything to the tracker. There's no
   "confirmed absent" cache state - only positive results get cached. So `IsEligibleAsync`'s read
   and `ClearCircuitBreakerAsync`'s existence check (itself another `ContainsStateAsync`, per
   finding 1) both round-trip on *every single tick*, forever, since the key never starts
   existing. This is unrelated to reentrancy entirely - it happens identically with reentrancy on
   or off, on either tracker-sync strategy, and affects any code that periodically checks for
   something that's usually absent (circuit breakers, existence checks, idempotency lookups).
   Filed upstream as a separate issue (unrelated to the reentrancy PR):
   [dapr/dotnet-sdk#1909](https://github.com/dapr/dotnet-sdk/issues/1909).

3. **Cross-actor fan-out shows up in the same table.** `queue_1_seg_0` (44 reads, identical on both
   builds) is the *subscriber's own* `QueueActor` instance being read - each successful relay tick
   calls `EnqueueWithTimeoutAsync`, which invokes that subscriber's `Enqueue` method, a completely
   separate actor that happens to share the same physical `daprmq_state` table (Dapr's
   postgresql/v2 store keeps every actor's state in one table). It's identical on both builds
   because that call is itself an ordinary (non-nested) invocation, always given its own fresh
   scoped tracker regardless of which fix is in play - it was never eligible for the kind of
   cross-call caching either fix affects.

---

## Reproducing this

```bash
# From server/, build both comparison images:
docker build --build-arg DaprActorsSdkVersion=1.18.4-reentrancyfix.1 -t daprmq-api:reentrancyfix .
docker build --build-arg DaprActorsSdkVersion=1.18.4-refreshfix.1 -t daprmq-api:refreshfix .

# Run the comparison (prints the count summary; set PRINT_QUERY_BREAKDOWN=true for the
# pg_stat_statements-normalized breakdown inline in test output):
dotnet test tests/DaprMQ.IntegrationTests/DaprMQ.IntegrationTests.csproj \
  --filter "FullyQualifiedName~QueryCountComparisonTest"

# The test also writes full per-statement logs (with bound parameter values) to:
#   $TMPDIR/dapr-mq-postgres-log-reentrancyfix.txt
#   $TMPDIR/dapr-mq-postgres-log-refreshfix.txt
# To regroup them by state name:
grep "DETAIL:.*parameters:" "$TMPDIR/dapr-mq-postgres-log-reentrancyfix.txt" \
  | grep -v '\$2 =' \
  | sed -E "s/.*\\\$1 = '([^']*)'.*/\1/" \
  | awk -F'\\|\\|' '{print $NF}' \
  | sort | uniq -c | sort -rn
```

Adjust `RelayCycles` in `QueryCountComparisonTest.cs` for a longer/shorter measurement window - 20
was chosen to average out per-run timing jitter (see "Stability" above) while keeping the test
under ~1.5 minutes per build.

## Related docs

- `docs/DAPR_REENTRANCY_REMINDER_ISSUE.md` - the original reminder-staleness bug report.
- `docs/REENTRANCY_FIX_ROUND_TRIP_IMPACT.md` - the code-level (pre-measurement) impact analysis
  this document empirically validates.
