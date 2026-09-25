# Dapr.Actors 1.18.9 retest

**Date:** 2026-09-24. **Update 2026-09-25:** the regression found below is fixed in the official
`Dapr.Actors` 1.18.10 release ([dapr/dotnet-sdk#1916](https://github.com/dapr/dotnet-sdk/pull/1916),
one commit on top of 1.18.9). DaprMQ now uses 1.18.10 directly; the local
`1.18.9-notfoundfix.1` build is no longer needed and has been removed from
`server/nuget-local/`. Retested: unit 334/334, integration 82/82, including the regression test
added for this issue.

The official `Dapr.Actors` 1.18.9 release includes all three changes from
`docs/REENTRANCY_FIX_EMPIRICAL_COMPARISON.md`. This page records the retest of DaprMQ against
1.18.9.

| Upstream PR | Change | Addresses |
|---|---|---|
| [#1912](https://github.com/dapr/dotnet-sdk/pull/1912) | Refreshes the default tracker in place after a reentrant save | Reentrancy / reminder staleness (`DAPR_REENTRANCY_REMINDER_ISSUE.md`) |
| [#1913](https://github.com/dapr/dotnet-sdk/pull/1913) | Caches "not found" (`StateChangeKind.NotFound`) | [#1909](https://github.com/dapr/dotnet-sdk/issues/1909), `github-issue-draft-negative-caching.md` |
| [#1914](https://github.com/dapr/dotnet-sdk/pull/1914) | `SetStateAsync` no longer calls `ContainsStateAsync`; it always stages `Update` (upsert) | #1910, `github-issue-draft-setstate-existence-check.md` |

**Result:** all three are confirmed fixed. The retest also found **one new regression** caused by
#1912 and #1913 together (see [below](#new-regression-notfound-entries-are-not-synced-after-a-reentrant-save)).

---

## Test results on 1.18.9

- **Unit tests** (`server/tests/DaprMQ.Tests`): 334/334 pass.
- **Integration suite** (`./build-and-test.sh`): 81/81 pass. This includes
  `SessionReentrancyTests` (the cold-actor A → B → A deadlock repro) and the `TopicTests`
  reminder-relay tests.

## Postgres reads: refreshfix.1 vs 1.18.9

Same scenario as `QueryCountComparisonTest`: 20 publish/relay-tick cycles. Both images ran in the
same test run.

| State key | `1.18.4-refreshfix.1` | `1.18.9` | Fix responsible |
|---|---|---|---|
| **Total** | **262** | **120** | 54% fewer reads; 1.18.9 read 120 in all 3 runs |
| `metadata` | 71 | 68 | Refresh-in-place is still in effect (#1912) |
| `circuit_sub-a` / `circuit_sub-b` | 44 each | 1 each | "Not found" is now cached (#1913) |
| `publish_<guid>` / `publish-seq_N` | 21 each | 0 each | Existence check removed (#1914) |
| `item_N` | 42 | 21 | Existence check removed (#1914); the remaining 21 are the relay's genuine reads |
| `queue_1_seg_N` | 44 | 42 | Subscriber `QueueActor`; roughly unchanged |

---

## New regression: `NotFound` entries are not synced after a reentrant save

**Status:** fixed. Filed as [dapr/dotnet-sdk#1915](https://github.com/dapr/dotnet-sdk/issues/1915),
fixed by PR [#1916](https://github.com/dapr/dotnet-sdk/pull/1916), released in the official
**1.18.10** package. DaprMQ uses 1.18.10 directly (no local build needed), covered by
`SessionTests.SessionLease_ExpiresAfterDirectorySweepSawItAbsent_ExpiryReminderStillReapsLockRecord`,
which now passes.

### What happens

After a reentrant save, `SyncDefaultTracker` (from #1912) only refreshes default-tracker entries
whose `ChangeKind == StateChangeKind.None`. #1913 adds a second kind of clean entry to the default
tracker, `StateChangeKind.NotFound`, and `SyncDefaultTracker` skips it.

The failing sequence:

1. A reminder or timer, running on the default tracker, calls `TryGetStateAsync("key")`. The key
   is absent, so `NotFound` is cached.
2. A reentrant method call creates `key` through its own scoped tracker and saves.
   `SyncDefaultTracker` skips the entry because it is `NotFound`, not `None`.
3. Every later reminder or timer read of `key` returns "absent" until the actor deactivates.

This is the same stale-state bug that #1912 fixed, now happening when a key goes from absent to
present.

### Proof

The following regression test was added to `test/Dapr.Actors.Test/ActorStateManagerTest.cs` at
tag `v1.18.9`. It **fails** on the released 1.18.9 code:

```csharp
[Fact]
public async Task ReentrantSaveUpdatesDefaultTrackerNotFoundEntry()
{
    var interactor = new Mock<TestDaprInteractor>();
    var host = ActorHost.CreateForTest<TestActor>();
    host.StateProvider = new DaprStateProvider(interactor.Object, new JsonSerializerOptions());
    var mngr = new ActorStateManager(new TestActor(host));
    var token = new CancellationToken();

    interactor
        .Setup(d => d.GetStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .Returns(Task.FromResult(new ActorStateResponse<string>("", null, 204)));
    interactor
        .Setup(d => d.SaveStateTransactionallyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .Returns(Task.CompletedTask);

    // Reminder/timer (default tracker) checks an absent key - "not found" is now cached.
    Assert.False((await mngr.TryGetStateAsync<string>("key1", token)).HasValue);

    // A reentrancy-scoped call creates the key through its own tracker.
    await mngr.SetStateContext("ctx1");
    await mngr.SetStateAsync("key1", "value2", token);
    await mngr.SaveStateAsync(token);
    await mngr.SetStateContext(null);

    // The store now holds the key; the default tracker must not keep serving "not found".
    interactor
        .Setup(d => d.GetStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .Returns(Task.FromResult(new ActorStateResponse<string>("\"value2\"", null)));
    var result = await mngr.TryGetStateAsync<string>("key1", token);
    Assert.True(result.HasValue);
    Assert.Equal("value2", result.Value);
}
```

### Proposed fix

A one-line change in `src/Dapr.Actors/Runtime/ActorStateManager.cs`. With it, the new test passes
and the full SDK suite passes (318 passed, 2 skipped on `net10.0`):

```diff
         foreach (var stateChange in stateChanges)
         {
             if (this.defaultTracker.TryGetValue(stateChange.StateName, out var stateMetadata) &&
-                stateMetadata.ChangeKind == StateChangeKind.None)
+                stateMetadata.ChangeKind is StateChangeKind.None or StateChangeKind.NotFound)
             {
                 if (stateChange.ChangeKind == StateChangeKind.Remove)
                 {
```

With this change:

- A reentrant write replaces a `NotFound` entry in place with the value just persisted.
- A reentrant remove evicts the entry. It was already absent, so this is harmless.

### Impact on DaprMQ

- **`SessionCoordinatorActor.SweepDirectoryAsync` is exposed.**
  - The sweep reminder reads `session-lock_X` for every candidate session. For a session nobody
    has claimed, that caches `NotFound` on the default tracker.
  - A consumer then claims X through `AcceptSession`, a reentrant method call. Later sweeps in the
    same activation still see no lease.
  - If X's queue happens to be empty at that moment, the sweep can evict an actively leased
    session from the directory, after its two consecutive confirmations.
  - The session-expiry reminder (`session-{id}`) for X reads the same stale `NotFound` and skips
    cleanup.
  - This is fairly rare, because the sweep runs hourly and actors deactivate when idle.
- **`TopicActor` is not affected.** Its relay tick only reads keys whose sequence numbers are below
  the current count in its metadata (`NextSequence`, `NextPublishSequence`), so it never looks up a
  key before it exists. Circuit-breaker keys are written only from the relay tick itself (default
  tracker), and they are removed rather than created by method calls.

---

## Side fix found during the retest

A plain `docker build` of `server/Dockerfile` had been failing since commit 044d1d5. When no SDK
version was given, the Dockerfile passed an empty `-p:DaprActorsSdkVersion=`, and that empty
global MSBuild property overrides the default version in the `.csproj` files (`NU1015`: no version
specified). The Dockerfile now only passes the property when it is set:

```dockerfile
RUN dotnet restore ${DaprActorsSdkVersion:+-p:DaprActorsSdkVersion=$DaprActorsSdkVersion}
RUN dotnet publish -c Release -o /app/publish ${DaprActorsSdkVersion:+-p:DaprActorsSdkVersion=$DaprActorsSdkVersion}
```

## Reproducing

```bash
# From server/: build the 1.18.9 image (now the csproj default)
docker build -t daprmq-api:official1189 .

# Point QueryCountComparisonTest at daprmq-api:refreshfix and daprmq-api:official1189, then:
dotnet test tests/DaprMQ.IntegrationTests/DaprMQ.IntegrationTests.csproj \
  --filter "FullyQualifiedName~QueryCountComparisonTest"
# Group the per-statement logs by state name as described in REENTRANCY_FIX_EMPIRICAL_COMPARISON.md.
```
