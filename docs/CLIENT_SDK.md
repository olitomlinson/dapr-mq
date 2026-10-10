# DaprMQ Client SDKs

Client SDKs for DaprMQ's HTTP/gRPC API, one per language, each living alongside the language it's written in rather than under this shared `docs/` folder:

- **[.NET](../sdks/dotnet/docs/CLIENT_SDK.md)** - `DaprMQ.Client` (`sdks/dotnet/`)
- **[TypeScript](../sdks/typescript/docs/CLIENT_SDK.md)** - `@daprmq/client` (`sdks/typescript/`)
- **[Python](../sdks/python/docs/CLIENT_SDK.md)** - `daprmq-client` (`sdks/python/`)
- **[Java](../sdks/java/docs/CLIENT_SDK.md)** - `io.github.olitomlinson:daprmq-client` (`sdks/java/`)
- **[Go](../sdks/go/docs/CLIENT_SDK.md)** - `github.com/olitomlinson/dapr-mq/sdks/go` (`sdks/go/`)

Every SDK exposes the same shape: direct queue/session operations (`enqueue`, `dequeueLocked`/`dequeue_locked`, `acknowledge`, `acknowledgeBatch`/`acknowledge_batch`, `extendLock`/`extend_lock`, `nack`, `deadLetter`/`dead_letter`, `acceptSession`/`accept_session`, `renewSessionLease`/`renew_session_lease`, `releaseSession`/`release_session`) over REST, plus two gRPC streams and a managed consumer over each:

- `consume` opens a `Consume` stream on a plain queue. The server keeps up to `prefetchCount` locked messages delivered, refills as they are settled, renews their locks, and returns the unsettled ones as soon as the stream closes. `QueueConsumer` runs your handler over it: success acks, an error nacks (paced) or dead-letters, and a broken stream is reopened with backoff.
- `consumeSession`/`consume_session` opens a `ConsumeSession` stream on one session. `SessionQueueConsumer` runs a pool of them, so you don't hand-roll session leasing yourself.

See each SDK's own doc for language-specific construction, error types, and examples, and [TIMEOUTS_AND_RETRIES.md](TIMEOUTS_AND_RETRIES.md) for how calls wait, retry and fail, and what your code should do about it.

## Which to use

`QueueConsumer` (or `consume`) is the way to run a long-lived consumer of a plain queue. It is several times faster than a loop around `dequeueLocked`, because messages are pushed as handlers free up rather than polled for, and the server renews locks instead of the client.

The REST calls stay the right tool for:

- **Run-to-completion work**: KEDA ScaledJobs, cron jobs and serverless functions that take a batch and exit. A stream's prefetch would lock messages the job never handles.
- **Admin and inspection tools**, and one-off operations.
- **Clients without HTTP/2 or gRPC**: browsers, some proxies, `curl`.
- **Explicit control of individual locks**: choosing each lock's TTL, extending one, or settling in a batch with `acknowledgeBatch`.
