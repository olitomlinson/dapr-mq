# SDK Integration Test Matrix

Source of truth for the integration scenarios every DaprMQ client SDK must implement against a real server (ApiServer + Dapr sidecar). Unit tests with fakes/mocks do **not** count here.

**Rules**
- Every scenario has a stable ID (`Q-01`, `S-03`…). Name the SDK test after it, e.g. `Q01_Enqueue_Dequeue_Ack_RoundTrips`, so IDs are greppable.
- Each test uses a fresh, unique queue ID. Tests must not depend on each other or on ordering.
- Tick a box only when the test exists **and** passes in CI/locally. Adding a scenario here means adding an unticked row for every SDK.
- Scenarios test the **SDK surface** (typed results, exception mapping, consumer behaviour), not raw HTTP contracts — those are covered by [server/tests/DaprMQ.IntegrationTests](../../server/tests/DaprMQ.IntegrationTests/).
- Error names below are language-neutral; map to each SDK's exception/error type (`LockNotFound`, `LockExpired`, `Validation`, `SessionLocked`, …).

**Test environment strategy (all SDKs): Testcontainers, never docker compose.** Each SDK's integration suite starts its own throwaway stack on a private Docker network with dynamic host ports, so it runs identically on a laptop and on a GitHub Actions runner with no pre-existing server. The stack mirrors the .NET fixture ([DaprTestEnvironment.cs](../../server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs)):

| Container | Image | Network alias | Notes |
|---|---|---|---|
| Postgres | `postgres:16.2-alpine` | `postgres-db` | db `actor_state`, user `postgres`, password `test_password` |
| Dapr placement | `daprio/dapr:1.18.4` | `dapr-placement` | `./placement -port 50005` |
| Dapr scheduler | `daprio/dapr:1.18.4` | `dapr-scheduler` | `./scheduler --port 50006 --etcd-data-dir <mounted dir>` |
| API server | `daprmq-api:test` (override `DAPRMQ_API_IMAGE`) | `api-server` | expose 5000 (REST) + 5001 (gRPC); `REGISTER_ACTORS=true`, `DAPR_HTTP_ENDPOINT=http://dapr-sidecar:3500`, `DAPR_GRPC_ENDPOINT=http://dapr-sidecar:50001` |
| daprd sidecar | `daprio/daprd:1.18.4` | `dapr-sidecar` | mounts [dapr-components](../../server/tests/DaprMQ.IntegrationTests/dapr-components) at `/tmp/dapr-components` and a temp dir at `/tmp/blobstore` |

Readiness: poll a probe enqueue on the API until it returns 200 (actors registered) rather than fixed sleeps. Start the stack once per test session and use a unique queue ID per test. The API image is a prerequisite, built by `./build-and-test.sh --skip-tests` from the repo root (CI must run that step first). Skip (don't fail) when the Docker daemon is unavailable; fail when the image is missing.

**CI:** [.github/workflows/sdk-integration-tests.yml](../../.github/workflows/sdk-integration-tests.yml) builds the API image once and each SDK job loads it via the shared composite action [actions/load-api-image](actions/load-api-image/action.yml). Add a job there when an SDK gains integration tests.

**Running**
- .NET: `dotnet test` in `sdks/dotnet/tests/DaprMQ.Client.IntegrationTests` (Testcontainers for .NET).
- Python: `pytest tests/integration` in `sdks/python` (`testcontainers` package; fixture in `tests/integration/conftest.py`).
- TypeScript: `npm run test:integration` in `sdks/typescript` (`testcontainers` package; fixture in `tests/integration/daprmqServer.ts`, needs Node ≥ 22 with testcontainers 12 / ≥ 18 with the pinned 10.x).
- Java: Testcontainers for Java with the same stack.

## Coverage matrix

Legend: ✅ implemented and passing · ⬜ not yet

| ID | Capability | .NET | Python | TypeScript | Java |
|---|---|:-:|:-:|:-:|:-:|
| **Queue basics** | | | | | |
| Q-01 | Enqueue → DequeueLocked → Ack round trip | ⬜ | ⬜ | ⬜ | ⬜ |
| Q-02 | FIFO order preserved within a priority | ⬜ | ⬜ | ⬜ | ⬜ |
| Q-03 | Priority ordering (0 before 1+, lower first) | ⬜ | ⬜ | ⬜ | ⬜ |
| Q-04 | Bulk enqueue (many items, one call) | ⬜ | ⬜ | ⬜ | ⬜ |
| Q-05 | Bulk dequeue (`count` > 1) returns up to N items, in order | ⬜ | ⬜ | ⬜ | ⬜ |
| Q-06 | Dequeue on empty queue returns null/none (no error) | ⬜ | ⬜ | ⬜ | ⬜ |
| Q-07 | Arbitrary JSON payloads round-trip intact (nested, unicode, null fields) | ⬜ | ⬜ | ⬜ | ⬜ |
| Q-08 | Invalid enqueue input surfaces `Validation` error (bad priority, empty batch, >1000 items) | ⬜ | ⬜ | ⬜ | ⬜ |
| **Idempotency** | | | | | |
| I-01 | Duplicate `idempotencyKey` is skipped; `ItemsDeduplicated` reported | ⬜ | ⬜ | ⬜ | ⬜ |
| I-02 | Same key on different queues does not dedupe | ⬜ | ⬜ | ⬜ | ⬜ |
| I-03 | Over-length / control-character key surfaces `Validation` error | ⬜ | ⬜ | ⬜ | ⬜ |
| **Locks** | | | | | |
| L-01 | Locked items are not redelivered to a second dequeue | ⬜ | ⬜ | ⬜ | ⬜ |
| L-02 | Lock expiry makes item available again | ⬜ | ⬜ | ⬜ | ⬜ |
| L-03 | Ack after lock expiry raises `LockExpired` | ⬜ | ⬜ | ⬜ | ⬜ |
| L-04 | Ack with unknown lock ID raises `LockNotFound` | ⬜ | ⬜ | ⬜ | ⬜ |
| L-05 | Double ack raises `LockNotFound` | ⬜ | ⬜ | ⬜ | ⬜ |
| L-06 | `ExtendLock` prolongs the lock past its original TTL | ⬜ | ⬜ | ⬜ | ⬜ |
| L-07 | `ExtendLock` on expired/unknown lock raises the matching error | ⬜ | ⬜ | ⬜ | ⬜ |
| L-08 | Redelivery preserves original FIFO position | ⬜ | ⬜ | ⬜ | ⬜ |
| **Dead-letter** | | | | | |
| D-01 | `DeadLetter` removes item from source queue and it appears on `{queueId}-deadletter` | ⬜ | ⬜ | ⬜ | ⬜ |
| D-02 | `DeadLetter` with unknown/expired lock raises the matching error | ⬜ | ⬜ | ⬜ | ⬜ |
| **Sessions (manual API)** | | | | | |
| S-01 | Enqueue with `SessionId` → `AcceptSession` (targeted) → dequeue/ack yields that session's items in FIFO order | ⬜ | ⬜ | ⬜ | ⬜ |
| S-02 | `AcceptSession` with no ID claims any available session and returns its ID | ⬜ | ⬜ | ⬜ | ⬜ |
| S-03 | `AcceptSession` with no sessions available returns null/none | ⬜ | ⬜ | ⬜ | ⬜ |
| S-04 | `AcceptSession` on already-leased session raises `SessionLocked` | ⬜ | ⬜ | ⬜ | ⬜ |
| S-05 | `AcceptSession` on unknown session raises `SessionNotFound` | ⬜ | ⬜ | ⬜ | ⬜ |
| S-06 | Dequeue on leased session without / with wrong `leaseId` raises `Validation`/`InvalidLeaseId` | ⬜ | ⬜ | ⬜ | ⬜ |
| S-07 | `RenewSessionLease` extends expiry; session stays usable past original expiry | ⬜ | ⬜ | ⬜ | ⬜ |
| S-08 | Expired lease makes session reclaimable by another `AcceptSession`; old lease raises `SessionLeaseExpired` | ⬜ | ⬜ | ⬜ | ⬜ |
| S-09 | `ReleaseSession` frees immediately (no wait for expiry) | ⬜ | ⬜ | ⬜ | ⬜ |
| S-10 | `ReleaseSession` twice with same lease is idempotent; wrong lease raises error | ⬜ | ⬜ | ⬜ | ⬜ |
| S-11 | Ack / ExtendLock / DeadLetter honour `leaseId` on a session queue | ⬜ | ⬜ | ⬜ | ⬜ |
| S-12 | Two sessions keep independent FIFO order | ⬜ | ⬜ | ⬜ | ⬜ |
| **Session streaming (`ConsumeSession`)** | | | | | |
| C-01 | Stream assigns a session, delivers items in order, Ack removes them | ⬜ | ⬜ | ⬜ | ⬜ |
| C-02 | Targeted `sessionId` stream only receives that session | ⬜ | ⬜ | ⬜ | ⬜ |
| C-03 | Delivery `DeadLetter` routes to `{queueId}-deadletter` | ⬜ | ⬜ | ⬜ | ⬜ |
| C-04 | `prefetchCount` bounds unacked in-flight deliveries | ⬜ | ⬜ | ⬜ | ⬜ |
| C-05 | Client disconnect/cancel releases the session immediately | ⬜ | ⬜ | ⬜ | ⬜ |
| C-06 | `sessionIdleTimeoutSeconds` ends stream (session drained) and releases session | ⬜ | ⬜ | ⬜ | ⬜ |
| C-07 | Second stream on a leased session surfaces `SessionLocked` | ⬜ | ⬜ | ⬜ | ⬜ |
| C-08 | Unacked delivery at disconnect is redelivered to the next consumer | ⬜ | ⬜ | ⬜ | ⬜ |
| C-09 | Lease lost mid-stream surfaces `SessionLost` | ⬜ | ⬜ | ⬜ | ⬜ |
| **SessionQueueConsumer (high-level)** | | | | | |
| K-01 | Handler success auto-acks; queue ends empty | ⬜ | ⬜ | ⬜ | ⬜ |
| K-02 | Multi-session: per-session FIFO preserved and slow session doesn't stall fast one (`MaxConcurrentSessions` ≥ 2) | ✅ | ✅ | ✅ | ⬜ |
| K-03 | Handler throws + `DeadLetterMessage` → item dead-lettered, session continues | ⬜ | ⬜ | ⬜ | ⬜ |
| K-04 | Handler throws + `AbandonSession` → session released, item redelivered | ⬜ | ⬜ | ⬜ | ⬜ |
| K-05 | Handler throws + `Both` → item dead-lettered and session abandoned | ⬜ | ⬜ | ⬜ | ⬜ |
| K-06 | `TargetSessionId` (requires `MaxConcurrentSessions == 1`) only consumes that session | ⬜ | ⬜ | ⬜ | ⬜ |
| K-07 | `MaxConcurrentSessions` is never exceeded | ⬜ | ⬜ | ⬜ | ⬜ |
| K-08 | Empty queue backs off (min→max) and picks up sessions enqueued later | ⬜ | ⬜ | ⬜ | ⬜ |
| K-09 | `Stop` drains in-flight handlers within `DrainTimeout` and releases sessions | ⬜ | ⬜ | ⬜ | ⬜ |
| K-10 | External cancellation token stops the consumer | ⬜ | ⬜ | ⬜ | ⬜ |
| K-11 | `SessionIdleTimeoutSeconds` lets the consumer move on to another session | ⬜ | ⬜ | ⬜ | ⬜ |
| **Client lifecycle** | | | | | |
| X-01 | Construct from options (HTTP + gRPC addresses) and perform a round trip | ⬜ | ⬜ | ⬜ | ⬜ |
| X-02 | Dispose/close is idempotent and cancels in-flight streams | ⬜ | ⬜ | ⬜ | ⬜ |
| X-03 | Server unreachable surfaces a transport error (not a hang, not a domain error) | ⬜ | ⬜ | ⬜ | ⬜ |

## Out of scope (not exposed by any SDK today)

Topics/pub-sub, HTTP sink, and large-object offload are server features with no SDK surface yet. When an SDK adds them, add a section and rows here first.

## Notes

- Java currently has unit tests only; their columns start empty.
- Scenarios relying on TTL expiry (L-02, L-03, S-08, C-09) should use the shortest TTL the server accepts and poll with a timeout rather than fixed sleeps.
