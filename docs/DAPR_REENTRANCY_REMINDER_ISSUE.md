# Dapr actor reminders silently stop firing once actor reentrancy is enabled

**Status:** Unconfirmed root cause. Written to support filing a GitHub issue against `dapr/dapr`.

**Dapr versions tested:** 1.18.2 and 1.18.4 (daprd + placement + scheduler, all matching versions). Same result on both — this is not something patched between those two releases.

**.NET SDK versions tested:** `Dapr.Actors` / `Dapr.Actors.AspNetCore` / `Dapr.Client` / `Dapr.AspNetCore` 1.18.4.

---

## Phase 1 — Abstract hypothesis

*(Written for a Dapr maintainer with no knowledge of the application this was found in. No project-specific names below.)*

### Summary

When [Actor Reentrancy](https://docs.dapr.io/developing-applications/building-blocks/actors/actor-reentrancy/) is enabled for an actor type, a self-registered actor **reminder** on an instance of that type can silently stop being delivered — no error, no warning, no log trace, in either the application or the Dapr runtime. The actor method that registered the reminder returns successfully; the reminder registration call itself succeeds; the reminder simply never fires. This is reproducible on demand (100% of runs, not intermittent) and was observed on both Dapr 1.18.2 and 1.18.4.

### Preconditions to reproduce

1. An actor type is registered with `ReentrancyConfig.Enabled = true` (or the language-SDK equivalent — this is a general actor-runtime setting exposed via the app's `GET /dapr/config`, not specific to .NET).
2. A method on an instance of that actor type does two things, in the same call: (a) some ordinary state work, and (b) registers a **periodic reminder on itself** via `RegisterReminderAsync`/equivalent — the reminder is not pre-existing, it is created fresh by this call.
3. The reminder is delivered via the **Scheduler service** (i.e., daprd was started with `--scheduler-host-address` pointing at a running scheduler; this is the default reminder-delivery path in recent Dapr versions when a scheduler is configured).
4. The actor method call that registers the reminder is **not itself part of any reentrant call chain** — it's a plain, single top-level invocation (e.g., triggered by an external HTTP/gRPC request), not a call nested inside another actor call.

Under these conditions, the reminder that was just registered with a short due-time (e.g., 1 second) never invokes the actor's reminder callback. Waiting arbitrarily long (tested up to tens of seconds, well past the reminder's period) does not help — it does not fire late, it does not fire at all.

### What is *not* the cause (ruled out)

- **Reminder registration itself failing.** The registering call completes successfully and returns a success response to the caller; the application's own debug logs confirm the registering method ran to completion.
- **A slow/overloaded environment.** The behavior is a hard, complete stop, not degraded throughput — re-running the same scenario with reentrancy disabled (everything else identical, same machine, same load) completes in about 1–2 seconds every time; with reentrancy enabled it never completes.
- **A stale/cached Dapr image.** Verified with two different Dapr patch releases (1.18.2 and 1.18.4), pulled fresh, confirmed present locally with distinct image digests.
- **The reminder never being registered in the first place.** No error surfaces anywhere (app logs, Dapr sidecar logs at `info` level) around the reminder-registration call.

### Working hypothesis

Reentrancy locking is implemented as a per-actor-instance lock that every inbound invocation into that instance — ordinary method calls, reminder callbacks, and timer callbacks alike — must acquire before running, and release when done (see Phase 3 for the exact code this is based on). The hypothesis is that there is some path by which:

- the *outer* call (the one that both does its own work and registers the reminder) does not cleanly release this per-instance lock once reentrancy is enabled, **or**
- the reminder invocation attempt, once delivered by the Scheduler service, is not correctly recognized as a **new**, independent top-level call and instead queues behind a lock slot that will never be freed (e.g., a reentrancy-chain-id mismatch, or a queue-position bookkeeping error specific to the Scheduler-driven reminder-delivery path, as opposed to the older placement-driven reminder path).

The result is consistent with a lock that is acquired and never released, or a queued caller that is waiting on a wake-up signal that never arrives — not a crash, not a timeout with backoff/retry visible in the logs, just permanent silence for that one reminder.

This appears to be specific to the *combination* of reentrancy + Scheduler-delivered reminders + a plain (non-nested) call registering the reminder. It was not tested with the older placement-based reminder delivery path, and it was not tested with a reminder registered from within an already-reentrant call chain (both worth a maintainer checking, since they narrow the search space further — see the open questions at the end of Phase 3).

---

## Phase 2 — Concrete reproduction (exact, no abstraction)

This section documents the actual reproduction as found, in a real ASP.NET Core / C# application (project internally called "DaprMQ"), so it can be rebuilt exactly. All paths below are relative to that project's `server/` directory.

### 1. Actor runtime registration (where reentrancy is toggled)

`src/DaprMQ.ApiServer/Program.cs`, inside the `builder.Services.AddActors(options => { ... })` block, alongside the existing `options.ActorIdleTimeout = TimeSpan.FromSeconds(60);`:

```csharp
options.ReentrancyConfig = new Dapr.Actors.ActorReentrancyConfig { Enabled = true };
```

This is the *only* change that toggles the bug on/off. Every other line below is unchanged between the passing and failing runs — confirmed by bisection (see "Bisection evidence" below).

Actor types registered in the same app/process (all hosted by one `daprd` sidecar, reentrancy applies runtime-wide once set — there is no per-actor-type opt-out available through this API surface):

```csharp
options.Actors.RegisterActor<DaprMQ.QueueActor>(actorConfig.QueueActorTypeName);
options.Actors.RegisterActor<DaprMQ.HttpSinkActor>(actorConfig.HttpSinkActorTypeName);
options.Actors.RegisterActor<DaprMQ.BlobReaperActor>(actorConfig.BlobReaperActorTypeName);
options.Actors.RegisterActor<DaprMQ.TopicActor>(actorConfig.TopicActorTypeName);
options.Actors.RegisterActor<DaprMQ.SessionCoordinatorActor>(actorConfig.SessionCoordinatorActorTypeName);
```

The actor type that actually exhibits the bug in this repro is `TopicActor`.

### 2. The actor method that registers the reminder

`src/DaprMQ/TopicActor.cs`. Constant:

```csharp
private const string RelayReminderName = "relay";
```

Reminder-registration helper (best-effort, logs and swallows failure — confirmed by logs that this does *not* throw in the failing case):

```csharp
private async Task TryRegisterReminderAsync(string name, TimeSpan dueTime, TimeSpan period)
{
    try
    {
        await RegisterReminderAsync(name, null, dueTime, period);
    }
    catch (Exception ex)
    {
        Logger.LogDebug(ex, "TopicActor {ActorId} failed to register reminder {ReminderName} (scheduler unavailable)", Id.GetId(), name);
    }
}
```

The `Publish` method (public actor method, invoked once via a plain external HTTP call, not nested inside any other actor call) — the relevant tail end:

```csharp
public async Task<PublishResponse> Publish(PublishRequest request)
{
    // ... validates request, writes several state entries via StateManager.SetStateAsync,
    //     updates its own metadata, calls StateManager.SaveStateAsync() ...

    await TryRegisterReminderAsync(RelayReminderName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    Logger.LogDebug("TopicActor {ActorId} published {Count} item(s) as PublishId {PublishId}, sequence {First}-{Last}", Id.GetId(), request.Items.Count, publishId, firstSequence, lastSequence);

    return new PublishResponse { Accepted = true, PublishId = publishId, Sequence = firstSequence };
}
```

Reminder dispatch:

```csharp
public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
{
    try
    {
        if (reminderName == RelayReminderName)
        {
            await RelayTickAsync();   // <-- never reached once reentrancy is enabled
        }
        // ... other reminder names, not relevant here ...
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "TopicActor {ActorId} error in ReceiveReminderAsync for reminder {ReminderName}", Id.GetId(), reminderName);
    }
}
```

`RelayTickAsync()` is the method that actually delivers the published item(s) to subscriber queues. It is never invoked; no exception is ever logged from the `catch` block above either — `ReceiveReminderAsync` itself never runs at all.

### 3. The failing test (xUnit + Testcontainers)

`tests/DaprMQ.IntegrationTests/Tests/TopicTests.cs` — three tests all fail the same way; the simplest:

```csharp
[Fact]
public async Task Publish_TwoSubscribers_BothReceiveItemInTheirOwnQueue()
{
    var topicId = NewTopicId();                 // $"test-topic-{Guid.NewGuid():N}"
    await SubscribeAsync(topicId, "sub-a");
    await SubscribeAsync(topicId, "sub-b");

    var publishResponse = await PublishAsync(topicId, new { id = 1 });

    var completed = await WaitForPublishCompleteAsync(topicId, publishResponse.PublishId, TimeSpan.FromSeconds(20));
    Assert.True(completed, "Publish did not complete relay within timeout");   // <-- fails here, every time

    // ... (never reached) ...
}
```

`PublishAsync` does `POST /topic/{topicId}/publish` (which invokes the `Publish` actor method above). `WaitForPublishCompleteAsync` polls `GET /topic/{topicId}/publish/{publishId}` every 500ms for up to the given timeout, checking a `Complete` flag that only becomes true once `RelayTickAsync` has processed the item. A companion test in a different file, exercising the same `Publish` → relay pattern with an extra pub/sub-dedup wrinkle, fails identically:

`tests/DaprMQ.IntegrationTests/Tests/IdempotencyKeyTests.cs::Publish_SameIdempotencyKeyTwice_DefaultSubscriberDedupesButOptedOutSubscriberDoesNot`.

### 4. Test infrastructure (Testcontainers-based, standalone mode)

`tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs`. Three Dapr-related containers, all Docker images pulled fresh, standalone mode (no Kubernetes), explicit placement + scheduler:

```csharp
// Placement
new ContainerBuilder()
    .WithImage("daprio/dapr:1.18.4")
    .WithCommand("./placement", "-port", "50005")
    ...

// Scheduler
new ContainerBuilder()
    .WithImage("daprio/dapr:1.18.4")
    .WithCommand("./scheduler", "--port", "50006", "--log-level", "info", "--etcd-data-dir", schedulerContainerDataDir)
    ...

// Sidecar
new ContainerBuilder()
    .WithImage("daprio/daprd:1.18.4")
    .WithCommand("./daprd",
        "--app-id", "daprmq-api",
        "--app-channel-address", "api-server",
        "--app-port", "5000",
        "--dapr-http-port", "3500",
        "--dapr-grpc-port", "50001",
        "--placement-host-address", "dapr-placement:50005",
        "--scheduler-host-address", "dapr-scheduler:50006",
        "--resources-path", "/tmp/dapr-components",
        "--config", "/tmp/dapr-components/config.yml",
        "--log-level", "info")
    ...
```

`--scheduler-host-address` being set is what puts reminders on the Scheduler-service delivery path (confirmed in the sidecar's own startup log — see below). `config.yml` only enables the `ActorStateTTL` preview feature; nothing reentrancy- or reminder-related is set there. Actor state store is `state.postgresql/v2` (Postgres 16.2, via Testcontainers). None of this differs between the passing and failing runs.

### 5. Commands to reproduce

From the `server/` directory, with Docker running:

```bash
# Build the app image once (contains the ReentrancyConfig = Enabled line above)
cd ..
./build-and-test.sh --skip-tests

# Run just the failing tests
cd server
DAPRMQ_TEST_QUEUE_ID="repro-$(uuidgen | tr '[:upper:]' '[:lower:]' | tr -d '-')" \
  dotnet test tests/DaprMQ.IntegrationTests/DaprMQ.IntegrationTests.csproj \
  --configuration Release \
  --logger "console;verbosity=normal" \
  --filter "FullyQualifiedName~TopicTests.Publish_TwoSubscribers_BothReceiveItemInTheirOwnQueue"
```

To capture full Dapr sidecar + app container logs during the run (used to produce the log excerpt below):

```bash
ENABLE_CONTAINER_LOGS=true dotnet test tests/DaprMQ.IntegrationTests/DaprMQ.IntegrationTests.csproj \
  --configuration Release --logger "console;verbosity=normal" \
  --filter "FullyQualifiedName~TopicTests.Publish_TwoSubscribers_BothReceiveItemInTheirOwnQueue"
```

### 6. Bisection evidence

Same build, same test, same Docker images, only `options.ReentrancyConfig` toggled:

| Reentrancy | Dapr version | Result |
|---|---|---|
| off (line absent) | 1.18.2 | `Publish_TwoSubscribers...` etc. pass, ~1–2s each |
| **on** | 1.18.2 | same tests fail, 20s timeout each, every run |
| **on** | 1.18.4 | same tests fail, 20s timeout each, every run |
| off (line removed again) | 1.18.4 | tests pass again, ~1–2s each |

Confirmed via `docker images` that both `daprio/dapr:1.18.4` and `daprio/daprd:1.18.4` were freshly pulled with distinct image IDs from the 1.18.2 versions already present locally — the 1.18.4 run was not silently reusing a cached 1.18.2 layer.

### 7. Captured log evidence (reentrancy ON, Dapr 1.18.2, `ENABLE_CONTAINER_LOGS=true`)

Sidecar startup (relevant lines only):

```
level=info msg="Enabled features: ActorStateTTL HotReload WorkflowsRemoteActivityReminder"
level=info msg="Using Scheduler service for reminders." scope=dapr.runtime.actor.reminders.scheduler
level=info msg="Registering hosted actors: [QueueActor HttpSinkActor BlobReaperActor TopicActor SessionCoordinatorActor]"
level=info msg="Actor runtime started" scope=dapr.runtime.actor
```

Application log, full timeline of the test run (nothing omitted between these two blocks — this is the entire output for the ~37-second run):

```
dbug: DaprMQ.TopicActor[0]
      TopicActor test-topic-6eb9a55c40314744a9165b10125d9254 activated and metadata initialized
dbug: DaprMQ.TopicActor[0]
      Activated
info: DaprMQ.TopicActor[0]
      TopicActor test-topic-6eb9a55c40314744a9165b10125d9254 subscribed sub-a at generation 1
info: DaprMQ.TopicActor[0]
      TopicActor test-topic-6eb9a55c40314744a9165b10125d9254 subscribed sub-b at generation 2
dbug: DaprMQ.TopicActor[0]
      TopicActor test-topic-6eb9a55c40314744a9165b10125d9254 published 1 item(s) as PublishId 4dc9e7a1bb7e4ed89059f9163a84d8c9, sequence 0-0
[xUnit.net 00:00:37.17]     DaprMQ.IntegrationTests.Tests.TopicTests.Publish_TwoSubscribers_BothReceiveItemInTheirOwnQueue [FAIL]
[xUnit.net 00:00:37.17]       Publish did not complete relay within timeout
```

No error, no warning, no reentrancy-related message, no lock/deadlock message, nothing about the `relay` reminder at all after the `published 1 item(s)` line — in either the application's own logging (configured at `Debug` level for the app's own namespaces) or the Dapr sidecar's `info`-level log. `RelayTickAsync` (and hence `ReceiveReminderAsync`) is never entered; its own `catch` block's error log — which *would* have appeared if the callback ran and threw — never appears either. The callback simply never starts.

---

## Phase 3 — Source-level investigation (validating the hypothesis against `dapr/dapr`)

All references below are against `dapr/dapr@master` at the time of writing, cross-checked to be consistent with behavior in 1.18.2 and 1.18.4 (both exhibit the bug; the lock implementation referenced below carries a `2025` copyright header, i.e. it postdates 1.18's initial release line and is plausibly the current implementation in both tested versions).

### The lock itself: `pkg/actors/targets/app/lock/lock.go`

One `*lock.Lock` is created **per actor instance** (per actor type *and* ID — not shared across instances of the same type), in `pkg/actors/targets/app/factory.go`:

```go
func (f *factory) initApp(actorID string) *app {
    app := &app{
        actorID: actorID,
        factory: f,
        clock:   f.clock,
        lock: lock.New(lock.Options{
            ActorType:   f.actorType,
            ConfigStore: f.reentrancy,
        }),
    }
    ...
}
```

This rules out one obvious hypothesis: the lock is *not* accidentally shared across unrelated actor instances of the same type. Good — that would have been a much bigger bug.

Every inbound call into an actor instance — plain method invocation, reminder, and timer alike — funnels through the *same* lock, in `pkg/actors/targets/app/app.go`:

```go
func (a *app) InvokeMethod(ctx context.Context, req *internalv1pb.InternalInvokeRequest) (*internalv1pb.InternalInvokeResponse, error) {
    ctx, cancel, err := a.lock.LockRequest(ctx, req)
    if err != nil {
        return nil, err
    }
    defer cancel()
    a.touchIdle()
    return a.transport.Invoke(ctx, req)
}

func (a *app) InvokeReminder(ctx context.Context, reminder *api.Reminder) error {
    lockReq := internalv1pb.NewInternalInvokeRequest("remind/"+reminder.Name).
        WithActor(reminder.ActorType, reminder.ActorID)

    ctx, cancel, err := a.lock.LockRequest(ctx, lockReq)
    if err != nil {
        return err
    }
    defer cancel()
    ...
    return a.transport.InvokeReminder(ctx, reminder)
}
```

Note: the `cancel` returned by `LockRequest` is in fact the lock's `release` closure (see below) — `defer cancel()` should reliably release the lock when `InvokeMethod`/`InvokeReminder` returns, success or error, since Go's `defer` runs regardless. **On a straightforward reading, this does not look like it leaks the lock for the simple, non-nested case** — see the full trace below.

### The locking/queueing mechanism itself

`Lock.LockRequest` → `handleLock` decides, per call, whether the request is a brand-new top-level call (gets queued at the back of an internal ring buffer, `l.inflights`) or a reentrant continuation of an already-in-flight chain (matched by a `Dapr-Reentrancy-Id` request-metadata value; same chain's `inflight.depth` is incremented instead of queueing a new entry):

```go
func (l *Lock) handleLock(ctx context.Context, msg *internalv1pb.InternalInvokeRequest) (*inflight, error) {
    id, ok := l.idFromRequest(msg)

    if !ok || !l.reentrancyEnabled || l.inflights.Len() == 0 {
        flight := newInflight(id)
        if l.inflights.Front() == nil {
            close(flight.startCh)
        }
        l.inflights.AppendBack(flight)
        return flight, nil
    }

    var flight *inflight
    var err error
    l.inflights.Range(func(v *inflight) bool {
        if v.id != id { return true }
        flight = v
        v.depth++
        if v.depth > l.maxStackDepth { err = messages.ErrActorMaxStackDepthExceeded }
        return false
    })
    if err != nil { return nil, err }

    if flight == nil {
        flight = newInflight(id)
        l.inflights.AppendBack(flight)
    }
    return flight, nil
}
```

`idFromRequest` (relevant because it explains why a *plain, non-reentrant* call still gets `ok=true` once reentrancy is enabled for the actor type):

```go
func (l *Lock) idFromRequest(req *internalv1pb.InternalInvokeRequest) (string, bool) {
    if !l.reentrancyEnabled || req == nil {
        return uuid.New().String(), false
    }
    if md := req.GetMetadata()[headerReentrancyID]; md != nil && len(md.GetValues()) > 0 {
        return md.GetValues()[0], true
    }
    id := uuid.New().String()
    if req.Metadata == nil { req.Metadata = make(map[string]*internalv1pb.ListStringValue) }
    req.Metadata[headerReentrancyID] = &internalv1pb.ListStringValue{Values: []string{id}}
    return id, true
}
```

Release:

```go
release := func() {
    close(doneCh)
    l.lock <- struct{}{}
    defer func() { <-l.lock }()

    flight.depth--
    if flight.depth == 0 {
        if v := l.inflights.RemoveFront(); v != nil {
            close(v.startCh)
        }
    }
}
```

And `RemoveFront` (from `github.com/dapr/kit/ring`, `ring/buffered.go`) — this matters, because its semantics are easy to misread:

```go
// RemoveFront removes the first value from the buffer and returns the next
// front value (or nil if the buffer is now empty). Amortized O(1).
func (b *Buffered[T]) RemoveFront() *T {
    ...
}
```

`RemoveFront` returns the **new** front (the next waiter), not the element just removed. So `close(v.startCh)` in `release` wakes up whichever call is now at the head of the queue — not the call that just finished. Combined with `newInflight` creating each entry's `startCh` **open** (unclosed) except when the ring was empty at creation time (in which case it's closed immediately so the new entry can proceed without waiting on anyone), this reading is internally consistent and does **not** produce an obvious double-close panic or an obviously-stuck queue for the simple single-caller case.

### Manual trace of the exact reproduction scenario

1. Ring empty. `Publish` arrives as a plain HTTP-triggered `InvokeMethod` call. `idFromRequest` generates a fresh reentrancy ID (no header present yet), `ok=true`. `handleLock`: ring length is 0 → new-entry branch; `Front()==nil` so `startCh` is closed immediately; entry appended. Caller proceeds without waiting.
2. Inside `Publish`, `RegisterReminderAsync` is called. This is **not** an actor invocation against this lock at all — it's an outbound call to the Scheduler service via a separate Dapr HTTP/gRPC endpoint (`PUT .../reminders/{name}`), which does not touch `a.lock`.
3. `Publish` returns; `a.transport.Invoke` returns to `InvokeMethod`; `defer cancel()` fires → `release()` → `flight.depth` 1→0 → `RemoveFront()` pops this entry, ring is now empty, returns `nil` → no-op. **Ring correctly ends up empty.**
4. ~1 second later, the Scheduler service triggers the `relay` reminder. This flows through `pkg/actors/router/router.go`'s `CallReminder` → `callReminder` → `target.InvokeReminder(ctx, req)`, where `req` is a **freshly constructed** `internalv1pb.NewInternalInvokeRequest("remind/relay").WithActor(...)` with no pre-existing `Dapr-Reentrancy-Id` metadata. `idFromRequest` generates a new fresh ID for it. `handleLock`: ring is empty (per step 3) → new-entry branch, `Front()==nil` → `startCh` closed immediately → should proceed without waiting.

**On this reading, the reminder invocation should not block at all** — the ring should be empty by the time it arrives, and even if it weren't, a genuinely new UUID would never match an existing entry via the `Range` loop and would simply queue normally. This is the point at which static reading of `lock.go`/`app.go`/`ring/buffered.go` in isolation stops explaining the observed behavior, and the search needs to move to code this investigation did not reach (see below).

### The most relevant lead found: PR [#8449](https://github.com/dapr/dapr/pull/8449) — "Actors: Move reentrancy lock to top level"

Merged 2025-02-02. Description, in full:

> Moves the reentrnacy actor lock to the top level of the API call stack in the engine. This is done so that in the event of a placement dissemination during 2 calls of the same actor ID, after unlocking the order of calls is preserved and there is not a non-deterministic race to when either call is processed.
>
> Introduces a `Locker` which is responsible for indexing actor ID locks. **Workflow activity actor reminders will continue to skip locks for reminders when Scheduler push based reminders are used.**
>
> Fixes actor lock e2e test.

Two things stand out:

1. **This PR is specifically about the interaction between reentrancy locking, actor-call ordering, and placement/dissemination** — i.e., exactly the kind of cross-cutting timing concern that could plausibly leave a lock in a state a purely-static read of the current `lock.go` wouldn't reveal (e.g., a race between lock state and a concurrent placement-table change, or between lock state and the Scheduler's own job-firing path, which this investigation did not trace — the router/lock code above only covers "how daprd dispatches an invocation it has already decided to make," not "how the Scheduler service decides to make one and delivers it into daprd's process in the first place").
2. **The explicit carve-out is only for *Workflow activity actor reminders*.** The reproduction here uses an ordinary (non-workflow) actor with an ordinary self-registered reminder. If the carve-out exists because *ordinary* reminders under Scheduler-push delivery need it too but don't get it — or because the underlying race the carve-out works around also affects ordinary reminders in some circumstance the workflow-specific fix didn't anticipate — that would line up closely with what's reproduced here.

Searching for the `Locker` type this PR introduced did not turn up a current match in `dapr/dapr` by that exact name; `pkg/actors/targets/app/lock.Lock` (dated 2025, described above) is the closest current candidate and is plausibly its descendant/rename, but this was not confirmed by tracing git history/blame on the file, which would be straightforward for someone with local clone access and is a good first step for whoever picks this up.

### Open questions for a maintainer (in priority order)

1. **Does `pkg/actors/targets/app/lock/lock.go` fully supersede what PR #8449 called "top level" locking, or does a separate/earlier lock still sit above it in the call stack** (e.g., in `pkg/actors/router/router.go` or `pkg/actors/actors.go`) that this investigation didn't locate? If there's a second lock layer, the "ring returns to empty" conclusion in the manual trace above may not hold — that second layer could be where a chain fails to unlock.
2. **How does the Scheduler service's job-firing callback actually reach `router.CallReminder`?** This investigation traced *from* `router.CallReminder` downward (into the app-level lock), but not the Scheduler → daprd delivery path itself. Given the bug is specific to Scheduler-delivered reminders (untested against the older placement-based reminder delivery, which would be a good differential test), the fault may be upstream of everything traced here — e.g., in how a Scheduler-delivered job's context or metadata is constructed before it ever reaches the router.
3. **Is the `Dapr-Reentrancy-Id` request-metadata mutation in `idFromRequest` safe against request-object reuse?** It mutates `req.Metadata` in place. If any caller (Scheduler delivery included) reuses or pools `*internalv1pb.InternalInvokeRequest` objects across separate logical calls, a previously-injected reentrancy ID could resurface on an unrelated later call and cause an incorrect chain match in the `Range` loop in `handleLock` — this would need confirming against how the Scheduler client constructs/reuses these request objects, which this investigation did not check.
4. **Does the "Workflow activity actor reminders... skip locks" carve-out from PR #8449 exist because the *general* case (what's reproduced here) is known to be still-affected by whatever prompted that carve-out?** Worth asking directly, since it may already be a known, partially-fixed issue rather than a novel one.

### What would most efficiently confirm/deny the hypothesis

Not attempted here (would require a local `dapr/dapr` build and debug instrumentation, beyond what this investigation had access to):

- Add trace/debug logging (or a debugger) around `Lock.LockRequest`/`handleLock`/`release` in a locally-built `daprd`, reproduce the scenario in Phase 2 against it, and observe directly whether the `relay` reminder's `LockRequest` call is entered at all, and if so, what it's blocked on.
- Re-run the same reproduction with placement-based (non-Scheduler) reminder delivery, to test whether the bug is specific to the Scheduler delivery path (per the PR #8449 lead) or general to reentrancy + reminders regardless of delivery mechanism.
- Re-run with the reminder registered from *within* an already-reentrant nested call (rather than a plain top-level call, as in the current reproduction), to test whether that changes the outcome — this would narrow whether the bug is about "any actor with reentrancy + a self-registered reminder" versus something more specific to the non-nested case this reproduction happens to use.
