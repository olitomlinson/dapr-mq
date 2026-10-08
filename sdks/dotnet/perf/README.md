# .NET SDK performance harness

The .NET implementation of [sdks/testing/PERFORMANCE_TESTS.md](../../testing/PERFORMANCE_TESTS.md):
closed-loop load (`enqueue`, `enqueue-batch`, `dequeue-ack`, and their `extreme` ramps) and the
`SessionQueueConsumer` session drain, against a real server it starts with Testcontainers. Results use the
shared schema, so [perf-results/report.html](../../testing/PERFORMANCE_TESTS.md#result-format) puts
them next to every other SDK's.

```bash
./build-and-test.sh --skip-tests             # builds daprmq-api:test (once per server change)
./run-perf-test.sh --suite pr                # every pr profile, ~6 min
./run-perf-test.sh --profile enqueue         # one profile
./run-perf-test.sh --suite extreme           # ramps + big drains, 3 API replicas behind nginx, ~2h
./run-perf-test.sh --profile enqueue-ramp --api-replicas 5
open perf-results/report.html
```

The stack is the perf topology of the spec: a 3-node scheduler HA cluster, and with `--api-replicas N`
(default 1, `--suite extreme` 3) N API servers, each with its own sidecar, behind an nginx load balancer
([DaprTopology.cs](../../../server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTopology.cs)).

Load profiles are fixed by the spec. The rest of this page covers the session drain (`P-04`) in detail.

## Session drain

Publishes **N sessions × M messages**, drains them with one consumer whose handler takes a fixed
**settle time** per message, and records how long that took and where the consumer's slots sat idle.

### Profiles

The pr profiles use short settle times so server and SDK overhead dominates the numbers instead of
hiding behind handler time. `--suite pr` runs them, after the load profiles, against one stack.

| Profile | Sessions × msgs | Settle | Slots | Other | Catches regressions in | Watch |
|---|---|---|---|---|---|---|
| `steady-drain` | 200 × 20 | 100 ms | 20 | idle 1 s | Overall throughput: delivery → ack → next delivery, moving between sessions | efficiency, msg gap p95 |
| `session-churn` | 300 × 2 | 50 ms | 20 | idle 1 s | The claim path (`AcceptSession`, lease, least-recently-claimed ordering) | claim p95, wall clock |
| `deep-session` | 4 × 1000 | 10 ms | 4 | idle 1 s | Long sessions: segment rollover, prefetch keeping up, lock/ack cost deep in a session | msg gap p95, wall clock |
| `live-publish` | 20 × 50 | 100 ms | 20 | idle 1 s, concurrent, 200 ms ± 100 ms | Producer and consumer together: how fast a new message reaches a slot holding its session (sessions = slots, so no backlog) | delivery p95, in-session wait |
| `sdk-defaults` | 40 × 5 | 1 s | 20 | SDK defaults | The out-of-the-box consumer, so changes to defaults show up | wall clock, drain wait |
| `quick` | 100 × 20 | 1 s | 20 | SDK defaults | — | — |
| `full` (default, extreme) | 1000 × 100 | 1 s | 20 | SDK defaults | — | — |
| `wide-drain` (extreme) | 2000 × 10 | 50 ms | 200 | idle 1 s | Many slots over short sessions: claim path and stream fan-out under load | claim p95, wall clock |

`session-churn` reads as inefficient by design: the server counts the idle timeout in whole seconds, so
every session costs ~1–2 s to close. Track its claim latency and wall clock, not efficiency.

Outside a suite, any profile value can be overridden:
`--sessions --messages --settle-ms --max-sessions --prefetch --lease --idle-timeout`.

- `--publish-mode concurrent` starts the consumer at the same moment publishing starts, instead of after
  the whole backlog is published (`before`, the default). The wall clock then includes publishing, and
  early claims can find no sessions (and back off).
- `--publish-interval-ms N [--publish-jitter-ms J]` rate-limits publishing: every session publishes in
  parallel, one message at a time, waiting N + random(0..J) ms between messages (and a random 0..J ms
  before the first). Without them each session is published in one enqueue call. Combined with
  `concurrent`, a publisher slower than the settle time shows up as in-session wait, and sessions that
  empty before their next message arrives show up as drain wait and re-claims.

Consumer options default to prefetch 10, lease 30 s, idle timeout = lease. Prefetch stays at 10 (the SDK default is now 1) so results remain comparable with earlier baselines.
`--http URL --grpc URL` targets an existing server instead of starting Testcontainers. `--report` only
regenerates the report.

### What is measured

Every `ConsumeSession` stream is timestamped by a decorating `IDaprMQClient`
([RecordingDaprMQClient.cs](DaprMQ.Client.Perf/RecordingDaprMQClient.cs)) and every handler call is
timed, so each slot-second is accounted for without changing the SDK:

```
slots × window = handling + claim + in-session wait + drain wait + between streams
```

| Metric | Meaning |
|---|---|
| Wall clock | Consume start → last message handled. `before`: seeding is timed separately; `concurrent`: consume start = publish start |
| Ideal / efficiency | ⌈sessions ÷ slots⌉ × msgs × settle (or a session's publish time + settle, if a rate-limited concurrent publish is slower); efficiency = ideal ÷ wall clock |
| Peak window | First delivery → the last session's first delivery. After it there is no unclaimed work, so tail idleness is excluded |
| Utilisation | Handler time ÷ slot capacity in the peak window |
| Claim | Stream opened → first delivery (`AcceptSession` + first dequeue), incl. failed claims |
| In-session wait | Gaps between handlers on the same stream (ack + redelivery latency, prefetch underrun, waiting for the publisher) |
| Drain wait | Last handler finished → server ended the stream (the session idle timeout) |
| Between streams | Slot time outside any stream (client loop, backoff after failed claims) |
| Delivery latency | Enqueue → handler start, from a publish timestamp in each payload. With `before` it's dominated by backlog wait; with `concurrent` it's the end-to-end latency |

A run fails (exit 1) if any message is missing or a session's messages were handled out of order
(load profiles: on any failed operation, or a `dequeue-ack` queue draining early).

## Regression check

After every run, each profile is compared with the **median of the last ≤10 passing runs** of the same
SDK, scenario key, environment and API replica count on the baseline branch (`--baseline-branch`,
default `main`; needs ≥3). The rules live in the shared
[RegressionCheck.cs](../../testing/perf/DaprMQ.PerfReport/RegressionCheck.cs), so every SDK is judged
the same way. A metric only counts as regressed or improved once it clears both a relative tolerance
and an absolute floor, since shared runners are noisy:

| Metric | Worse when | Tolerance | Floor |
|---|---|---|---|
| Wall clock | higher | 25% | 2 s |
| Efficiency, peak utilisation | lower | 15% | 0.03 |
| Claim p95, msg gap p95 | higher | 50% | 50 ms |
| Delivery p95 | higher | 50% | 100 ms |
| Load: messages/s | lower | 20% | 20 msg/s |
| Load: latency p95 | higher | 50% | 5 ms |
| Load: latency p99 | higher | 100% | 10 ms |

Peak utilisation is skipped when the peak window is under 5 s (e.g. `live-publish`, where every session
is claimed at once), since a window that short is noise. The table is printed, and appended to the GitHub job summary in CI. It's report-only unless `--gate` is
passed, which exits 3 on any regression.

## Results

Written under the results root (`--out`, default `<repo>/perf-results`, git-ignored), in the shared
layout of [result.schema.json](../../testing/perf/result.schema.json):

- `sdk-dotnet/runs/<runId>.json`: one run in full, including its per-second timeline
- `sdk-dotnet/history.jsonl`: one line per run without the timeline. Trends and regression checks read this.
- `report.html`: regenerated after every run from every `sdk-*/` directory present

Runs are tagged with `--env-label` (default `local-<hostname>`), git sha/branch/dirty flag, OS, CPU
count and topology. Runs from different machines or replica counts aren't comparable, so the report
filters by environment and replica count. Results from before the shared schema convert in place with
`--migrate-v1 <dir>`. CI does this to the `perf-results` branch automatically.

CI ([perf.yml](../../../.github/workflows/perf.yml)) runs `--suite pr` as `ci-ubuntu-latest` on every push
to `main` and PR that touches `server/`, `sdks/dotnet/` or `sdks/testing/`, plus nightly.
[perf-extreme.yml](../../../.github/workflows/perf-extreme.yml) runs the extreme suite, only by hand.
Results live on the `perf-results` branch: `main`, nightly, manual and extreme runs are merged into it,
and PR runs compare against it. Every run uploads `report.html` as the `perf-report` artifact.

## State reads benchmark

```bash
./run-perf-test.sh --benchmark state-reads   # ~2 min
```

Counts the actor-state statements each operation sends to Postgres: reads (`SELECT`) and writes
(`INSERT`/`UPDATE`/`DELETE`) against `daprmq_state`. They come from Postgres' own statement log
(`log_statement=all`), so they count what actually reached the store, after any caching in the Dapr
actor SDK. It always starts its own stack, since it needs that log; `--http`/`--grpc` aren't accepted.

Each step runs its setup unmeasured, then counts only the statements its measured operations produce:

| Step | Measured | Per |
|---|---|---|
| `enqueue-new-queue` | first enqueue to 20 new queues (activation + first write) | queue |
| `enqueue` | 50 single-item enqueues to a warm queue | enqueue |
| `enqueue-batch-10` | 10 enqueues of 10 items | batch |
| `dequeue-ack` | 50 × `DequeueLocked` + `Acknowledge` on a 150-item queue | message |
| `dequeue-ack-batch-10` | 5 × `DequeueLocked(count: 10)` + 10 acks | message |
| `session-cycle` | 10 × accept session, dequeue + ack 3 messages, release | session |
| `consume-session` | 10 sessions of 3 messages drained over the gRPC session stream | session |
| `lock-expiry-sweep` | the one dequeue that reclaims 20 expired locks | reclaimed lock |
| `topic-relay` | 10 publishes, 1 s apart, relayed to 2 subscribers | publish |

Every step but `topic-relay` and `consume-session` runs synchronously on a warm actor, so its counts are
deterministic: the same code gives the same numbers on every run and machine. `topic-relay` depends on how
the relay reminder's ticks interleave with the publishes, and `consume-session` on how many times the
server's 200 ms poll runs before the stream is closed.

**Regression check.** Reads/op and writes/op per step are compared with the median of the last ≤10
runs on the baseline branch, like the session drain metrics. Because the counts don't drift with runner
noise, the tolerance is 5% (15% for `topic-relay` and `consume-session`), with a floor of 0.5 statements per operation. The
table goes to the console and the job summary; `--gate` exits 3 on a regression.

**Results.** `perf-results/sdk-dotnet/state-reads/history.jsonl` has one line per run;
`perf-results/sdk-dotnet/state-reads/runs/<runId>.json` adds a per-step breakdown by actor type and state name, with ids
and sequence numbers collapsed (`queue_*_seg_*`, `*-lock`, `session-lock_*`), to show which state a
change came from. CI runs it after the session drain and stores it on the same `perf-results` branch.

To compare two server builds directly, run it once per image with `DAPRMQ_API_IMAGE=<image>` and diff
the two run files.

[docs/performance/state-reads-breakdown.md](../../../docs/performance/state-reads-breakdown.md) traces every read and write in each
step back to the actor code.
