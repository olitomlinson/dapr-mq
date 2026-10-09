# daprmq (Go)

A Go client for DaprMQ's HTTP/gRPC API: `Client` for direct queue and session operations, plus `SessionQueueConsumer`, a managed multi-session consume loop built on the `ConsumeSession` streaming RPC. Lives at `sdks/go/` (module `github.com/olitomlinson/dapr-mq/sdks/go`, package `daprmq`). Built on `net/http` (REST) and `grpc-go` (streaming and health). Requires Go 1.24+.

The API follows the conventions of the Azure SDK for Go (`azservicebus`): every method takes a `context.Context` first and, where it has options, a nil-able `*XxxOptions` struct last; failures are one error type, `*daprmq.Error`, with a `Code` you check through `errors.As`.

## Install / Reference

Not yet tagged for release. Reference it from a local checkout with a `replace` directive:

```bash
go mod edit -require=github.com/olitomlinson/dapr-mq/sdks/go@v0.0.0 \
            -replace=github.com/olitomlinson/dapr-mq/sdks/go=../path/to/dapr-mq/sdks/go
```

The gRPC stubs under `internal/pb/` are generated from the shared proto contract (`server/src/DaprMQ.ApiServer/Protos/daprmq.proto`, the same source the other SDKs use) and checked in. Run `go generate` in `sdks/go/` after the proto changes (needs `protoc`, `protoc-gen-go` and `protoc-gen-go-grpc`).

## Constructing a client

```go
import daprmq "github.com/olitomlinson/dapr-mq/sdks/go"

client, err := daprmq.NewClient("http://localhost:8002", "localhost:8003", nil)
if err != nil {
    return err
}
defer client.Close()
```

`NewClient` doesn't connect; the first call does. Pass `&daprmq.ClientOptions{...}` to change the defaults:

| Field | Default | Notes |
| --- | --- | --- |
| `HTTPClient` | an `*http.Client` with a 100 s timeout | Your own client keeps its own timeout and transport (useful for tests: a custom `RoundTripper`) |
| `GRPCDialOptions` | plaintext | Applied after the default, so `grpc.WithTransportCredentials(...)` switches to TLS |
| `Retry` | see below | `daprmq.RetryOptions` |

`Client` is REST-backed for every operation except `ConsumeSession` and `WaitForReady`, which use gRPC. It is safe for concurrent use; share one per process. `Close()` releases the gRPC connection (ending any open streams) and is safe to call more than once.

### Errors

Every DaprMQ failure is a `*daprmq.Error`:

```go
err := client.Acknowledge(ctx, queueID, lockID, nil)
var mqErr *daprmq.Error
if errors.As(err, &mqErr) && mqErr.Code == daprmq.CodeLockNotFound {
    // already settled, or the lock expired
}
```

Codes: `CodeLockNotFound`, `CodeLockExpired`, `CodeValidation`, `CodeNotFound`, `CodeSessionNotFound`, `CodeSessionLocked`, `CodeSessionLeaseExpired`, `CodeInvalidLeaseID`, `CodeSessionActorUnavailable`, `CodeNoSessionsAvailable`, `CodeSessionLost`, `CodeUnavailable`, `CodeDeliveryUnknown`. A code the server sends that isn't in that list comes through unchanged as a `Code` string. `StatusCode` holds the HTTP status when there was one.

Not errors: an empty queue (`DequeueLocked` returns a result with no `Items`), a locked queue (`Locked: true`), and no session available (`AcceptSession` returns `nil, nil`).

## Retries, failures and waiting for a server

Calls ride out a DaprMQ that briefly can't serve them, such as a worker restarting, under the shared contract in [RETRIES_AND_READINESS.md](../../testing/RETRIES_AND_READINESS.md). [docs/TIMEOUTS_AND_RETRIES.md](../../../docs/TIMEOUTS_AND_RETRIES.md) covers what to program for, across all SDKs:

```go
client, err := daprmq.NewClient(httpURL, grpcAddress, &daprmq.ClientOptions{
    Retry: daprmq.RetryOptions{
        Timeout:             30 * time.Second, // the default (0 also means 30 s); daprmq.NoRetries turns retries off
        AutoIdempotencyKeys: true,             // optional: makes every enqueue safe to retry
    },
})
```

- **Slow is not failed.** A call that reached a busy queue waits its turn for as long as it takes, up to your context and the 100 s per-call limit. The retry timeout never cuts it short.
- **Certainly not performed** (the server reports it couldn't serve the call, or the connection was refused): retried until `Timeout`, then `CodeUnavailable`. Always safe to repeat later.
- **Outcome unknown** (the connection broke after sending, or no response arrived in time): an `Enqueue` whose items all have an `IdempotencyKey`, and `AcknowledgeBatch`, are retried; anything else returns `CodeDeliveryUnknown` straight away. The error carries `Operation`, `QueueID` and, for `Enqueue`, `IdempotencyKeys`. What to do next:
  - **Enqueue without keys:** re-send and accept a possible duplicate.
  - **`DequeueLocked`:** don't re-send. If it ran, the items come back when their locks expire.
  - **Acknowledge / ExtendLock / DeadLetter / Nack:** re-sending is safe in effect. `CodeLockNotFound` then means the first attempt worked.
- **Cancelling the context** stops retrying and returns `context.Canceled` or `context.DeadlineExceeded`, never a `*daprmq.Error`.

`client.WaitForReady(ctx, nil)` waits until queue operations can be served (gRPC health service `daprmq.DaprMQ.operations`), without writing anything. It has no limit of its own, so bound it with `context.WithTimeout`. Pass `&daprmq.WaitForReadyOptions{Service: "daprmq.DaprMQ"}` to wait only for the server instance itself. It returns an error wrapping `errors.ErrUnsupported` if the server has no health service.

## Basic queue operations

```go
_, err := client.Enqueue(ctx, "my-queue", []daprmq.EnqueueItem{
    {Item: map[string]string{"task": "send_email"}},
    {Item: urgent, Priority: daprmq.Ptr(daprmq.PriorityFastLane)},
}, nil)

result, err := client.DequeueLocked(ctx, "my-queue", &daprmq.DequeueLockedOptions{TTL: 60 * time.Second})
if err != nil {
    return err
}
for _, item := range result.Items {
    var task Task
    _ = json.Unmarshal(item.Item, &task) // Item is json.RawMessage
    // ... process ...
    if err := client.Acknowledge(ctx, "my-queue", item.LockID, nil); err != nil {
        return err
    }
}
```

`EnqueueItem.Item` is marshalled with `encoding/json`. `Priority` is a `*int` because 0 is a real lane (the fast lane): leave it nil for the normal lane (1).

**Competing consumers.** By default a queue serves one lock at a time: while any item is locked, further locked dequeues come back `Locked`. When several consumers share a queue (for example replicas scaled out by KEDA), set `AllowCompetingConsumers: true` so each can hold its own locks.

**Settling.** Each lock call takes `*daprmq.LockOptions` (`LeaseID` on a session queue, else nil):

- `Acknowledge(ctx, queueID, lockID, opts)` removes the item.
- `AcknowledgeBatch(ctx, queueID, lockIDs, opts)` settles up to 1,000 locks in one call (one actor turn, one state save), typically everything a bulk `DequeueLocked` (`Count: n`) returned. It returns `AcknowledgeBatchResult{ItemsAcknowledged, Results}` with one `LockAcknowledgeResult{LockID, Outcome}` per lock in request order; `Outcome` is one of `AcknowledgeOutcomeAcknowledged`, `AcknowledgeOutcomeLockNotFound`, `AcknowledgeOutcomeLockExpired`, `AcknowledgeOutcomeInvalidLockID`. A lock that can't be settled never fails the others; only a whole-call problem is an error (`CodeSessionLeaseExpired`, `CodeInvalidLeaseID`, `CodeValidation` for an empty list, more than 1,000 ids or duplicates). An unknown outcome is retried automatically, so after a retry `LOCK_NOT_FOUND` can mean the first attempt already settled that lock.
- `Nack(ctx, queueID, lockID, opts)` returns the item to its original position and returns `NackResult{DeadLettered, DeliveryCount, DLQID}`.
- `ExtendLock(ctx, queueID, lockID, additional, opts)` adds time to the lock.
- `DeadLetter(ctx, queueID, lockID, opts)` moves the item to `{queueID}-deadletter`.

Durations sent to the server (`TTL`, `LeaseDuration`, `ExtendLock`'s `additional`) are whole seconds, rounded up.

## Consuming a queue over a stream (`Consume`)

`Consume` opens a gRPC stream on a plain queue. The server keeps up to `PrefetchCount` locked items delivered, refills as they are settled, and renews the lock of every delivered item until it is settled, so there's no polling and no `ExtendLock`:

```go
stream, err := client.Consume(ctx, "my-queue", &daprmq.ConsumeOptions{
    PrefetchCount: 50, AllowCompetingConsumers: true,
    OnSettleFailed: func(lockID string, err error) { log.Printf("settle %s: %v", lockID, err) },
})
if err != nil {
    return err
}
defer stream.Close()
for {
    delivery, err := stream.Receive()
    if err != nil {
        return err // io.EOF once the stream ends after Close
    }
    // ... process delivery.Item; delivery.DeliveryCount is 1 on a first delivery ...
    _ = delivery.Ack() // or Nack() / DeadLetter()
}
```

`Receive` is for one goroutine; settle from any. A rejected settle doesn't end the stream: it is reported to `OnSettleFailed` (for example `CodeLockNotFound` after a lost lock). `Close()` half-closes: the server applies the settles already sent, returns every unsettled item to its position straight away, then ends the stream. Settling after `Close` returns `ErrStreamClosed`. With `PrefetchCount` 1 and `AllowCompetingConsumers` false, items arrive strictly in queue order.

## Topics

A topic fans each published item out to every subscriber's own queue, which is consumed with the queue calls above.

```go
sub, err := client.Subscribe(ctx, "orders", "billing", nil)
var mqErr *daprmq.Error
if errors.As(err, &mqErr) && mqErr.Code == daprmq.CodeSubscriberExists {
    sub.QueueID, err = daprmq.TopicSubscriberQueueID("orders", "billing"), nil
}

_, err = client.Publish(ctx, "orders", []daprmq.EnqueueItem{{Item: order}}, nil)

result, err := client.DequeueLocked(ctx, sub.QueueID, nil)
```

- `Subscribe(ctx, topicID, subscriberID, opts)` provisions the subscriber's queue and returns `SubscribeResult{QueueID}`. A subscriber receives only items published after it subscribed. `SubscribeOptions.DedupEnabled` turns de-duplication on for its queue.
- `Publish(ctx, topicID, items, nil)` returns `PublishResult{PublishID, Sequence}` once the topic has accepted the items; relay to the subscriber queues happens afterwards, in publish order per subscriber. A publish whose outcome is unknown isn't retried, since the topic doesn't de-duplicate (`SessionID` is ignored).

## Sessions: manual API

For sticky routing, admin tooling, or callers who don't want a managed consume loop:

```go
lease, err := client.AcceptSession(ctx, "my-queue", &daprmq.AcceptSessionOptions{SessionID: "order-42"})
if err != nil || lease == nil {
    return err // lease == nil: no session available
}
sessionQueue := "my-queue-session-" + lease.SessionID
result, err := client.DequeueLocked(ctx, sessionQueue, &daprmq.DequeueLockedOptions{LeaseID: lease.LeaseID})
// ... Acknowledge/ExtendLock/DeadLetter against sessionQueue with &daprmq.LockOptions{LeaseID: lease.LeaseID} ...
_ = client.ReleaseSession(ctx, "my-queue", lease.SessionID, lease.LeaseID)
```

`RenewSessionLease(ctx, queueID, sessionID, leaseID, opts)` extends the lease. See [API_REFERENCE.md](../../../docs/API_REFERENCE.md#sessions) and [design/sessions.md](../../../docs/design/sessions.md) for the actor-id convention and lease semantics.

## Sessions: one stream (`ConsumeSession`)

`ConsumeSession` claims one session and streams its items; the server renews the lease for as long as the stream stays open, and no lease id is exposed:

```go
stream, err := client.ConsumeSession(ctx, "my-queue", &daprmq.ConsumeSessionOptions{SessionIdleTimeout: 10 * time.Second})
if err != nil {
    return err
}
defer stream.Close()
for {
    delivery, err := stream.Receive()
    if errors.Is(err, io.EOF) {
        break // the session drained
    }
    if err != nil {
        return err // e.g. CodeNoSessionsAvailable, CodeSessionLocked, CodeSessionLost
    }
    // ... process delivery.Item ...
    _ = delivery.Ack() // or DeadLetter() / Nack()
}
```

Call `Receive` from one goroutine; `Ack`/`DeadLetter`/`Nack` may be called from any. `Close()` half-closes the stream: the server applies every settlement already sent and then releases the session (keep calling `Receive` until it returns an error to wait for that). If the server hasn't ended the stream 5 s after `Close`, the client cancels it. Settling after `Close` returns `ErrSessionStreamClosed`. Cancelling `ctx` ends the stream immediately, dropping settlements the server hasn't read yet.

## Sessions: managed consume loop (`SessionQueueConsumer`)

`SessionQueueConsumer` is the recommended way to consume sessions. It runs `MaxConcurrentSessions` independent slots, each looping over its own `ConsumeSession` stream: claim a session, hand each message to your handler, settle it, repeat until the session drains, then claim another.

```go
handler := func(ctx context.Context, msg daprmq.SessionMessage) error {
    // msg.SessionID, msg.LockID, msg.Item (json.RawMessage), msg.Priority
    return process(ctx, msg.Item) // nil acks; an error applies OnHandlerError
}

consumer, err := daprmq.NewSessionQueueConsumer(client, "my-queue", handler, &daprmq.SessionQueueConsumerOptions{
    MaxConcurrentSessions: 8,
    OnHandlerError:        daprmq.DeadLetterMessage,
})
if err != nil {
    return err
}
consumer.Start(ctx)
// ... run your application ...
err = consumer.Stop(context.Background())
```

| Option | Default | Notes |
| --- | --- | --- |
| `MaxConcurrentSessions` | 4 | One stream per slot |
| `TargetSessionID` | any session | Sticky routing; requires `MaxConcurrentSessions: 1` (`NewSessionQueueConsumer` returns an error otherwise) |
| `LeaseDuration` | 30 s | |
| `PrefetchCount` | 1 | Session order holds only at 1 when messages are nacked |
| `SessionIdleTimeout` | the lease duration | How long a session may sit empty before the slot moves on |
| `MinBackoff` / `MaxBackoff` | 1 s / 60 s | After a failed claim; doubles per miss, resets on a successful claim |
| `OnHandlerError` | `DeadLetterMessage` | Or `NackMessage`, `AbandonSession`, `DeadLetterAndAbandonSession` (the other SDKs' `Both`) |
| `DrainTimeout` | 30 s | How long `Stop` waits for in-flight handlers |

`Stop(ctx)` stops claiming sessions, lets in-flight handlers finish and settle, then closes the streams, which releases their sessions; prefetched messages not yet handled return with their session. If handlers are still running after `DrainTimeout`, their context is cancelled and `Stop` returns `context.DeadlineExceeded`. Cancelling the context passed to `Start` stops the consumer the same way, and `Done()` is closed once it has fully stopped. The handler's context keeps `Start`'s context values but is cancelled only by that drain timeout.

### Why one shared client

All of a consumer's slots share the single `Client` (and its one gRPC connection) passed to `NewSessionQueueConsumer`. A gRPC connection multiplexes every RPC, including long-lived streams, over HTTP/2, which is what makes tens or hundreds of concurrent `ConsumeSession` streams cheap. Don't construct a `Client` per slot. See [design/sessions.md](../../../docs/design/sessions.md) for the reasoning.

## Testing

- Unit tests: `go test -race ./...` in `sdks/go`. REST behaviour is tested through a fake `http.RoundTripper` (`ClientOptions.HTTPClient`); gRPC streams and health through an in-process server on `bufconn` (`ClientOptions.GRPCDialOptions` with `grpc.WithContextDialer`). The same two seams work for testing your own code against this SDK.
- Integration tests: `go test ./...` in `sdks/go/integration` (a separate module, so the SDK doesn't depend on Testcontainers). It starts its own stack with Testcontainers for Go; build the API image first with `./build-and-test.sh --skip-tests` from the repo root. See [INTEGRATION_TESTS.md](../../testing/INTEGRATION_TESTS.md).
