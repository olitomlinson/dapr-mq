# DaprMQ Client SDKs

Client SDKs for DaprMQ's HTTP/gRPC API, one per language, each living alongside the language it's written in rather than under this shared `docs/` folder:

- **[.NET](../sdks/dotnet/docs/CLIENT_SDK.md)** - `DaprMQ.Client` (`sdks/dotnet/`)
- **[TypeScript](../sdks/typescript/docs/CLIENT_SDK.md)** - `daprmq-client` (`sdks/typescript/`)
- **[Python](../sdks/python/docs/CLIENT_SDK.md)** - `daprmq-client` (`sdks/python/`)
- **[Java](../sdks/java/docs/CLIENT_SDK.md)** - `com.daprmq:daprmq-client` (`sdks/java/`)

Every SDK exposes the same shape: direct queue/session operations (`enqueue`, `dequeueLocked`/`dequeue_locked`, `acknowledge`, `extendLock`/`extend_lock`, `deadLetter`/`dead_letter`, `acceptSession`/`accept_session`, `renewSessionLease`/`renew_session_lease`, `releaseSession`/`release_session`) over REST, plus a managed `SessionQueueConsumer` built on the `ConsumeSession` gRPC streaming RPC - a multi-session consume loop that buffers messages and relays them to your handler push-based, so you don't hand-roll session leasing/heartbeating yourself. See each SDK's own doc for language-specific construction, error types, and examples.
