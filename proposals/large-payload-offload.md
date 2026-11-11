# Large payload offload and streaming enqueue/dequeue

**Status:** proposed. Not yet implemented. Builds on [binary-store-migration.md](binary-store-migration.md) (implemented).

## Context

Today a payload goes to the Binary Store only when the producer chooses `POST /queue/{id}/enqueue-object`
([QueueController.cs:190](../server/src/DaprMQ.ApiServer/Controllers/QueueController.cs#L190)). Plain
enqueue has no size threshold, so whatever the producer sends is stored inline in the segment.

That costs every queue, not only deep ones:

- **Every enqueue rewrites the whole tail segment**
  ([SegmentedQueue.cs:47](../server/src/DaprMQ/SegmentedQueue.cs#L47)). One 5 MB item makes each of
  the next 99 enqueues to that segment write at least 5 MB to the state store.
- **The only cap is the transport.** gRPC allows 16 MB per message
  ([Program.cs:75](../server/src/DaprMQ.ApiServer/Program.cs#L75)) and HTTP uses Kestrel's ~30 MB
  default. A 100-item segment can therefore reach hundreds of MB, past state store item limits such
  as Cosmos DB's 2 MB.
- **Large items go through the actor.** The full payload is serialized into the actor call, held in
  actor memory with the segment, and serialized again on dequeue.

There are also two gaps in the existing object path:

- **gRPC drops the claim token.** gRPC `Dequeue`, `DequeueLocked`, `Consume` and `ConsumeSession` copy
  only `ItemJson` ([DaprMQGrpcService.cs:199](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L199),
  [:298](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L298),
  [:1026](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L1026),
  [:1387](../server/src/DaprMQ.ApiServer/Services/DaprMQGrpcService.cs#L1387)). For an object item
  that is the internal `{"__daprmq_blob_ref__": ...}` marker
  ([BlobReferenceEnvelope.cs](../server/src/DaprMQ.Interfaces/BlobReferenceEnvelope.cs)). The
  consumer can't fetch the payload, and the raw blob reference leaks, which
  [Models.cs:270](../server/src/DaprMQ.Interfaces/Models.cs#L270) says must never happen.
- **A failed enqueue leaks its blob.** `enqueue-object` uploads first, then calls the actor. Reaping
  is only scheduled on dequeue or acknowledge
  ([QueueActor.cs:388](../server/src/DaprMQ/QueueActor.cs#L388)). If the actor call fails, nothing
  ever deletes the blob, and the Binary Store has no list operation to find it later.

## Proposal

Two features that share one storage path:

1. **Transparent offload.** The API server sends any enqueued payload over a configured size to the
   Binary Store and enqueues a small envelope in its place. On every delivery path, the server reads
   the payload back and returns it inline. Producers, consumers and SDKs see no difference.
2. **Streaming enqueue and dequeue.** New opt-in calls, over HTTP and gRPC, that move a payload in
   chunks between the client and the Binary Store. DaprMQ never holds the whole message in memory.

`QueueActor` stays almost unchanged. It stores and moves envelopes, as it already does for
`enqueue-object` items. All upload, download and rehydration happens in the API server (and in
`HttpSinkActor` for HTTP sinks).

### Payload kinds

The envelope gains a `kind` string so the server knows how to deliver an offloaded payload:

```json
{"__daprmq_blob_ref__": "orders/9f2c...", "size": 5242880, "contentType": "application/json", "kind": "item"}
```

- **`kind` says how the payload is delivered, which follows from how it was enqueued.** `"item"`
  means it was a queue item offloaded by transparent offload, and is returned inline. `"object"`
  means it was streamed in, and is returned as a claim token or a stream.
- **`contentType` says what format it's in**, and takes any value (`application/json`,
  `application/x-ndjson`, `image/png`, ...). A new format needs no envelope change. A new `kind` is
  only needed for a new way of delivering.

| Payload | Created by | Buffered dequeue returns | Streaming dequeue returns |
|---|---|---|---|
| Inline item | `enqueue`, under the threshold | the JSON | the JSON as the body |
| Offloaded item (`kind: "item"`) | `enqueue`, over the threshold | the JSON, read back from the blob | the blob as the body |
| Object (`kind: "object"`) | `enqueue-stream`, any content type | `{objectClaimToken, contentType}`, as today | the blob as the body |

Only transparent offload creates `"item"`. Its payload arrived as a JSON item that was already
parsed, so it is known to be valid JSON and safe to return inline. A streamed body is never
validated, may not be a single JSON value (JSON Lines isn't), and may be far larger than a caller
expects inline, so it is always an object, whatever its content type. Buffered dequeue keeps returning
a claim token for objects. Streaming dequeue returns every payload as raw bytes, so a streaming
consumer never needs claim tokens.

Pre-v1, existing envelopes without `kind` are read as `"object"`. No migration.

### Transparent offload

**Threshold.** `DAPRMQ_INLINE_MAX_BYTES`, deployment-wide, default 64 KiB; `0` turns the feature
off. It's measured on the UTF-8 length of the item's JSON. Helm: `blobStore.inlineMaxBytes`.

**Enqueue** (HTTP `enqueue`, gRPC `Enqueue`, both of which already hold the item in memory):

1. Validate the request as today.
2. For each item over the threshold, upload its JSON with content type `application/json` and
   replace `ItemJson` with a `kind: "item"` envelope. A batch uploads in parallel, at most 8 at once,
   and every upload finishes before the actor call.
3. Make the one actor call, unchanged.

The blobs are durable before the actor commits, so a 200 still means every item is stored.

If an upload fails, the server deletes the blobs it already uploaded for that request and returns the
upload's error: 413 for an object that's too large, 507 when the store is full, and 503 for an
unavailable store. Nothing is enqueued.

**Dequeue.** Every delivery path puts the payload back inline before replying:

- HTTP `dequeue` (plain and locked): in `ResolveItemElement`
  ([QueueController.cs:454](../server/src/DaprMQ.ApiServer/Controllers/QueueController.cs#L454)).
- gRPC `Dequeue`, `DequeueLocked`, `Consume`, `ConsumeSession`.
- `HttpSinkActor`: it already has `IObjectStore` in the actor host. It sends JSON payloads inline and
  keeps claim tokens for objects ([HttpSinkActor.cs:172](../server/src/DaprMQ/HttpSinkActor.cs#L172)).
- `AdminReadService` peek: shows size and an "offloaded" badge, and reads the payload only when it's
  opened.

A batch downloads in parallel, at most 8 at once. Response sizes are the same as when these payloads
were inline, so transport limits apply exactly as they do today.

**If a blob is missing on delivery:** the server logs an error and fails the call. A locked item
comes back when its lock expires, and goes to the dead-letter queue after `MaxDeliveryCount`. A plain
dequeue has already removed the item, so it's lost. That is the same at-most-once delivery as any
plain dequeue whose response fails. A missing blob for a live item means a reaping bug, not a normal
state.

**Reaping.** The actor schedules the reap on plain dequeue and on acknowledge, as today. Offloaded
items (`kind: "item"`) use a shorter delay, `DAPRMQ_BLOB_REAP_ITEM_SECONDS` (default 300), because
the server delivers them inline and nothing redeems them later; see [Open questions](#open-questions) 3. Objects
keep the 24-hour backstop. Dead-letter, nack, lock expiry, session restore and scheduled delivery all move the
envelope as-is, so the blob lives exactly as long as the item.

**Not offloaded in phase 1: topic publish.** `TopicActor` fans one publish out to N subscriber queues.
A shared blob would be reaped on the first subscriber's acknowledge while the others still need it.
Topic publishes stay inline until DaprMQ has message expiry; see [Open questions](#open-questions) 2.

### Streaming enqueue and dequeue

These are for payloads too large to buffer, or for producers and consumers that already work with
streams. Server memory per call is bounded by the chunk size and pipe buffer, not by the payload
size. The size cap is `DAPRMQ_OBJECT_MAX_BYTES` (default 100 MiB), not the 16 MB gRPC message limit,
because each frame is a separate message.

#### HTTP

**`POST /queue/{id}/enqueue-stream`** replaces `enqueue-object` (pre-v1, no alias).

- The body is the payload. Metadata uses the existing headers: `content-type`, `priority`,
  `idempotency-key`, `session-id`, `prefix`.
- The body streams into `IObjectStore.UploadAsync`, as `enqueue-object` does today.
- Every body becomes an object (`kind: "object"`), whatever its content type, and always uploads to
  the Binary Store. See [Payload kinds](#payload-kinds) for why a streamed body is never returned
  inline.
- `size` in the envelope is the number of bytes actually uploaded, counted while streaming. Today it
  comes from `Content-Length` and is empty for a chunked upload.

**`POST /queue/{id}/dequeue-stream`** returns one item, with the payload as the response body.

- Request: the same headers as `dequeue` (`require-ack`, `ttl-seconds`, `allow-competing-consumers`,
  `lease-id`). `count` is not accepted.
- 200 response:
  - The body is the payload: the inline JSON, or the blob streamed chunk by chunk.
  - `Content-Type` is `application/json` for items, or the object's stored content type.
  - `Content-Length` comes from the envelope's `size` when it's known.
  - Headers carry the metadata: `priority`, `delivery-count`, and, for locked mode, `lock-id` and
    `lock-expires-at`.
- Other responses match `dequeue`: 204 empty, 423 locked, 410 lease expired, 503 and 504.

**Failure part-way through the body.** `DownloadAsync` reads the first chunk before returning
([DaprBinaryStoreObjectStore.cs:61](../server/src/DaprMQ.ApiServer/Services/DaprBinaryStoreObjectStore.cs#L61)),
so a missing blob fails before the status line goes out. A later failure aborts the connection, and
`Content-Length` lets the client see the body was cut short.

- In locked mode the client doesn't acknowledge, so the item comes back on lock expiry. The docs
  should recommend locked mode for `dequeue-stream`.
- In plain mode the item was removed before the body started, so a broken stream loses it. This is
  the same at-most-once delivery as plain `dequeue`, but the window lasts as long as the transfer.

#### gRPC

```protobuf
// Client-streaming: the first frame is `start`, every later frame is `chunk`.
rpc EnqueueStream(stream EnqueueStreamRequest) returns (EnqueueResponse);

// Server-streaming: the first frame is `header` (or a terminal empty/locked/error), then `chunk`s.
rpc DequeueStream(DequeueStreamRequest) returns (stream DequeueStreamResponse);

// Server-streaming download of a claimed object, the gRPC equivalent of GET /object/{token}.
rpc GetObject(GetObjectRequest) returns (stream ObjectChunk);

message EnqueueStreamRequest {
  oneof payload {
    EnqueueStreamStart start = 1;   // queue_id, priority, idempotency_key, session_id, content_type
    bytes chunk = 2;
  }
}

message DequeueStreamResponse {
  oneof payload {
    DequeueStreamHeader header = 1; // content_type, size, priority, delivery_count, lock_id, lock_expires_at
    bytes chunk = 2;
    DequeueEmpty empty = 3;
    DequeueBlocked locked = 4;
  }
}
```

- **Server side.** gRPC frames are written into a `System.IO.Pipelines` `Pipe` whose reader is passed
  to `UploadAsync`. The pipe's pause threshold (1 MiB) provides backpressure, and HTTP/2 flow control
  carries it back to the client. Downloads write each Binary Store chunk as one `chunk` frame, at
  most 64 KiB.
- **Enqueue rules** match HTTP `enqueue-stream`: every payload becomes an
  object, and the size is counted while uploading.
- **Mid-stream failure on dequeue:** the call ends with a non-OK status after some chunks. The SDK
  discards the partial payload and reports the error.

#### Consume streams

`Consume` and `ConsumeSession` deliver several items in turn over one stream, so they don't send
payloads in chunks. Splitting payloads into chunk frames would block every later delivery behind one
large item.

- Items, inline or offloaded, are delivered inline (transparent offload).
- Objects are delivered with a claim token: new fields `object_claim_token = 6` and `content_type = 7`
  on `ConsumeDelivered` and `SessionDelivered`. The SDK fetches the bytes with `GetObject` on the same
  channel, so no HTTP address is needed.

#### Fixing gRPC claim tokens (prerequisite)

gRPC dequeue must stop sending the raw marker:

- Add `repeated string object_claim_token` and `repeated string content_type` to `DequeueSuccess` and
  `DequeueLockedSuccess`, parallel to `item_json`. An empty string means the item is inline.
- Fill them the way `ResolveItemElement` does: an offloaded item is rehydrated, an object gets its
  token, and the marker is never sent.

### Orphaned blobs

A blob is orphaned when it's uploaded but never committed in an item.

| Cause | Handling |
|---|---|
| Actor returns a validation error, or `UNAVAILABLE` (not performed) | Server deletes the request's blobs. |
| Item skipped as a duplicate by idempotency key | Server deletes that item's blob. Needs per-item results: `EnqueueResponse.DeduplicatedIndexes`. |
| Upload part-way through a batch fails | Server deletes the blobs already uploaded. |
| `DELIVERY_UNKNOWN`, or the API server crashes between upload and commit | **Leaked.** The item may have been committed, so deleting is unsafe. The Binary Store can't list, so no sweep can find it. |

Deletes go through `BlobReaperActor.ScheduleDeletion` with no delay, so a failed delete is retried.
The leak in the last row already exists for `enqueue-object`. It's rare, since it needs a crash or a
lost actor response.

### Not changed

- `QueueActor` segment, lock, sweep and session logic. The actor's only change is per-item
  deduplication results.
- Segment storage (state store, 100 items per segment). Offloading whole segments was considered and
  is not part of this proposal.
- `GET /object/{token}`, claim tokens, `BlobReapConfig` TTLs.
- `ScheduleActor` (proposed on `feature/scheduled-delivery`): it forwards envelopes unchanged.

## Limits and costs

- **Latency.** Each offloaded item adds one Binary Store upload on enqueue and one download on
  delivery. Inline items, the common case under 64 KiB, cost nothing extra.
- **Memory.** Transparent offload still buffers each payload in the API server, as inline payloads
  are today. Only the streaming calls avoid that.
- **Transport mismatch.** An item enqueued over HTTP can be up to ~30 MB, but gRPC delivery is capped
  at 16 MB per message. That's true today, too. Streaming dequeue or `GetObject` has no such cap.
- **Ordering.** Offload happens before the actor call, so it doesn't affect FIFO order or `Sequence`.

## Tests (TDD order, per CLAUDE.md)

1. **Integration** (HTTP and gRPC contract):
   - Enqueue a JSON item over the threshold, dequeue it (plain, locked, consume): the same JSON comes
     back.
   - `enqueue-stream` then `dequeue-stream`: the same bytes and content type come back, for JSON and
     binary bodies, including chunked uploads with no `Content-Length`. A JSON body sent to
     `enqueue-stream` comes back from plain `dequeue` as a claim token, not inline.
   - gRPC `EnqueueStream`/`DequeueStream` round trip a payload larger than 16 MB.
   - An object dequeued over gRPC returns a claim token, never `__daprmq_blob_ref__`; `GetObject`
     redeems it.
2. **Controller / gRPC service** (mocked actor and `IObjectStore`):
   - Items at the threshold stay inline; items one byte over are uploaded.
   - A failed upload in a batch deletes the earlier blobs and doesn't call the actor.
   - An actor failure or `UNAVAILABLE` deletes the blobs; `DELIVERY_UNKNOWN` does not.
   - Deduplicated items have their blobs deleted.
   - A missing blob on locked delivery fails the call without acknowledging.
   - `dequeue-stream` sets the content type, length, and lock headers; returns 204 and 423 as
     `dequeue` does.
3. **Actor** (mocked state):
   - `EnqueueResponse.DeduplicatedIndexes` lists the skipped items.
   - A `kind: "item"` envelope is reaped with the short delay on plain dequeue and acknowledge; an object
     keeps the backstop.
4. **`DaprBinaryStoreObjectStore`**: `UploadAsync` reports the counted size.

## Phases

0. **Fix gRPC claim tokens.** Proto fields and filling them, plus SDKs reading them. This stands on
   its own and is a bug fix.
1. **Transparent offload.** Threshold, rehydration on all delivery paths, orphan handling,
   `DeduplicatedIndexes`. No SDK changes.
2. **HTTP streaming.** `enqueue-stream` (replacing `enqueue-object`) and `dequeue-stream`.
3. **gRPC streaming.** `EnqueueStream`, `DequeueStream`, `GetObject`, claim tokens on consume streams.
4. **SDKs.** All five get `enqueueStream`/`dequeueStream` and object payloads as a lazy stream in
   consume handlers.
5. **Docs.** [API_REFERENCE.md](../docs/API_REFERENCE.md), [ARCHITECTURE.md](../docs/ARCHITECTURE.md),
   Helm values, and a line in `CLAUDE.md` Domain Knowledge.

## Open questions

Each question notes what Azure Service Bus (ASB) does. ASB has no automatic offload. Premium keeps
payloads up to 100 MB in the broker, and anything larger is left to client-side claim-check libraries:
[ServiceBus.AttachmentPlugin](https://github.com/SeanFeldman/ServiceBus.AttachmentPlugin) and
[NServiceBus claim check](https://docs.particular.net/nservicebus/messaging/claimcheck/azure-blob-storage).

1. **Per-queue threshold.** Phase 1 has one deployment-wide setting. A per-queue override would need
   somewhere to store queue config that the API server reads before calling the actor.
   - *ASB:* the broker's size cap is set per queue or topic
     ([`MaxMessageSizeInKilobytes`](https://learn.microsoft.com/en-us/dotnet/api/azure.messaging.servicebus.administration.createqueueoptions.maxmessagesizeinkilobytes?view=azure-dotnet),
     1 MB by default), but it only rejects; nothing is offloaded. The libraries set the offload
     threshold on the sender, as a rule given when the client is created (for example "body > 200 KB").
   - *Recommendation:* one deployment-wide threshold. If a per-queue setting is needed later, copy the
     shape of the per-queue cap.
2. **Topic publish.** A shared blob would be reaped on the first subscriber's acknowledge.
   - *ASB:* the broker copies a published message to each subscription itself, so one subscriber's
     settlement never affects another's. The libraries don't solve this: the attachment plugin
     deletes no blobs, and NServiceBus deprecated its built-in cleanup in favour of time-based external
     cleanup. Neither reference-counts.
   - *Recommendation:* topic publishes stay inline until DaprMQ has **message expiry**, proposed in
     `proposals/message-expiry.md` (on `feature/message-expiry`), which defines the details. Then the
     API server uploads the payload once and every subscriber queue references the same blob. The blob
     is reaped after the message expires, not when any subscriber settles it. No subscriber can be
     delivered the message after that, because an expired message is never delivered.
     - **One upload per publish,** and no reference counting.
     - **The envelope records when the blob is reaped:** a new `reapAt` field, the message's expiry
       plus a 24-hour grace period. `ScheduleBlobReapingIfNeeded` skips any envelope with `reapAt`, so
       a subscriber's acknowledge or dequeue never schedules a deletion.
     - **Deletion is scheduled before the upload.** The API server calls
       `BlobReaperActor.ScheduleDeletion` for `reapAt` first, then uploads, then publishes. A crash
       at any point leaves at most a scheduled deletion of a blob that doesn't exist, which succeeds
       harmlessly. Nothing leaks, which also closes open question 4 for these blobs.
     - **Only publishes with a time to live are offloaded.** A topic publish over the threshold with no
       time to live stays inline, so every shared blob has a deletion time.
     - **Dead-lettering copies the blob.** Dead-lettered items expire 28 days after being
       dead-lettered, so a dead-lettered item gets its own copy of the blob with its own `reapAt`. The
       original is left to its own schedule.
     - **The grace period covers late work.** It covers clock skew, a consumer still holding a locked
       item after it expires, and a claim token redeemed late. Locks aren't capped at `reapAt`; an
       item held more than 24 hours past expiry may lose its blob, which is accepted as rare.
     - **Storage cost:** one copy is kept for the message's full lifetime plus the grace period, even
       after every subscriber has finished. That's still less than today's one inline copy per
       subscriber.

     A blob copy per subscriber (what the broker does internally) was considered and rejected. It
     costs N uploads inside `TopicActor`'s publish turn. A reference count in `BlobReaperActor` was
     rejected as more machinery than anyone in the ecosystem uses.
3. **Shorter reap for offloaded items.** Offloaded items (`kind: "item"`) never hand out claim tokens, so the
   24-hour backstop is longer than they need.
   - *ASB:* inside the broker, a payload is freed when its message completes, with no grace period.
     The libraries never delete per message, because a consumer that deletes the blob breaks
     redelivery.
   - *Recommendation:* a short reap (for example 5 minutes) for `kind: "item"` envelopes. The server
     delivers them inline, so after acknowledge or plain dequeue nothing needs the blob. Locked items
     are reaped only on acknowledge, so redelivery isn't affected. Objects keep the 24-hour backstop,
     because their claim tokens are redeemed after delivery.
4. **Leaked blobs.** The crash and `DELIVERY_UNKNOWN` leak can't be found without a list operation.
   - *ASB:* both libraries clean up with Azure Storage lifecycle rules that delete by age (once a
     day). NServiceBus ties payload lifetime to the message's `TimeToBeReceived`. That's safe because
     every ASB message has a time to live, so retention is bounded.
     [Microsoft's claim-check guidance](https://learn.microsoft.com/azure/architecture/patterns/claim-check)
     accepts orphans as unavoidable, since the payload write and the publish aren't atomic.
   - *Recommendation:* document the leak. DaprMQ items have no expiry, so an age-based rule could
     delete a blob a queued item still needs. Once message expiry exists, a message with an expiry
     can use the design in question 2 (deletion scheduled before upload), which can't leak. Items
     without an expiry still can.
