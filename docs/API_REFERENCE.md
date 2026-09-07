# API Reference

Complete reference for the DaprMQ REST API, exposed by the ApiServer at `http://localhost:8002` (adjust host/port for your deployment).

All endpoints are scoped to a queue via `{queueId}` in the path — each distinct `queueId` maps to its own `QueueActor` instance.

## Table of Contents

- [Push](#push)
- [Push Large Object](#push-large-object)
- [Pop](#pop)
- [Acknowledge](#acknowledge)
- [Extend Lock](#extend-lock)
- [Dead Letter](#dead-letter)
- [HTTP Sink](#http-sink)
- [Error Codes](#error-codes)

---

## Push

Push one or more items onto the queue.

```
POST /queue/{queueId}/push
Content-Type: application/json
```

**Body**

```json
{
  "items": [
    { "item": { "task": "send_email", "to": "a@example.com" }, "priority": 0 }
  ]
}
```

- `item` — arbitrary JSON, stored and returned as-is.
- `priority` — `0` is the fast lane, `1+` is normal (lower value = higher priority). Default `1`.
- Up to 1000 items per request.

**Response — `200 OK`**

```json
{ "success": true, "message": "Pushed 1 items to queue my-queue", "itemsPushed": 1 }
```

**Example**

```bash
curl -X POST http://localhost:8002/queue/my-queue/push \
  -H "Content-Type: application/json" \
  -d '{"items": [{"item": {"task": "send_email"}, "priority": 0}]}'
```

---

## Push Large Object

Streams a large binary/object payload directly into an external object store (local volume, S3, Azure Blob — whichever `bindings.*` component is configured) instead of embedding it inline in queue state. The queue state only ever stores a small reference to the object, never the bytes themselves.

```
POST /queue/{queueId}/push-object
```

Unlike `push`, the request **body is the raw object content** (not JSON) — send whatever bytes you want stored (a file, an image, a large payload, etc).

**Headers**

| Header | Required | Default | Description |
|---|---|---|---|
| `priority` | no | `1` | Same semantics as `push` — `0` is the fast lane. |
| `content-type` | no | _(none)_ | Recorded alongside the object reference; returned as-is on pop. |
| `prefix` | no | `{queueId}` | User-controlled path segment the object is stored under, e.g. `store-a`, `store-b`. Combined with an operator-configured global prefix (set at deploy time, never client-controlled) to form the final key: `{globalPrefix}/{prefix}/{objectId}`. Restricted to `[A-Za-z0-9_-]` segments — no `..` or leading `/`. |

**Response — `200 OK`** — same shape as `push`.

```json
{ "success": true, "message": "Pushed 1 items to queue my-queue", "itemsPushed": 1 }
```

**Example — push a file**

```bash
curl -X POST http://localhost:8002/queue/my-queue/push-object \
  -H "content-type: application/pdf" \
  -H "priority: 0" \
  --data-binary @report.pdf
```

**Example — push with a custom storage prefix**

```bash
curl -X POST http://localhost:8002/queue/my-queue/push-object \
  -H "prefix: store-b" \
  --data-binary @large-image.png
```

### Popping large objects

Popping is always JSON — the queue never streams raw object bytes inline, regardless of how many items are popped or how large the object is. For an offloaded item, the response's `item` field is replaced with a small opaque **claim token**:

```json
{ "item": { "objectClaimToken": "eyJhbGciOiJIUzI1NiIs...", "contentType": "application/pdf" }, "priority": 0 }
```

Fetch the actual bytes with the claim token via:

```
GET /object/{token}
```

**Example — pop, then download the object**

```bash
RESPONSE=$(curl -s -X POST http://localhost:8002/queue/my-queue/pop -H "require-ack: false")
TOKEN=$(echo "$RESPONSE" | jq -r '.items[0].item.objectClaimToken')

curl -o downloaded-report.pdf "http://localhost:8002/object/$TOKEN"
```

**Response — `200 OK`** — raw object bytes, with `Content-Type` set from the value recorded at push time (falls back to `application/octet-stream` if none was given).

**Error responses**:

| HTTP Status | Meaning |
|---|---|
| `400 Bad Request` | Token is malformed, tampered, or otherwise invalid |
| `410 Gone` | Token has expired |
| `404 Not Found` | Token is valid but the underlying object has already been deleted |

The claim token is self-contained (it encodes the blob reference, content type, and its own expiry) and is validated standalone — no queue, lock, or actor lookup is involved in resolving it.

**Example — pop with acknowledgement, capturing the lock id**

`require-ack: true` still returns the lock id in the normal JSON item shape, just like inline items:

```bash
curl -X POST http://localhost:8002/queue/my-queue/pop \
  -H "require-ack: true" \
  -H "ttl-seconds: 60"

# {"items":[{"item":{"objectClaimToken":"...","contentType":"application/pdf"},"priority":0,"lockId":"aB3xQ9k2LmZ","lockExpiresAt":1780000123.45}]}

curl -X POST http://localhost:8002/queue/my-queue/acknowledge \
  -H "Content-Type: application/json" \
  -d '{"lockId": "aB3xQ9k2LmZ"}'
```

### Cleanup lifecycle

Cleanup uses two configurable TTLs (`blobStore.reap.backstopSeconds` / `blobStore.reap.postDownloadSeconds` in Helm — see [ARCHITECTURE.md](ARCHITECTURE.md)):

- **Backstop** — scheduled once the item is irrecoverably removed from the queue (shortly after plain `pop`, or after `acknowledge` for a `pop`-with-ack item). Covers the case where the claim token is never redeemed, so the object still gets cleaned up eventually. If the lock instead expires and the item is redelivered, no backstop is scheduled (the object is still referenced by the requeued item). `deadletter` also does not schedule cleanup - the dead-lettered item still references the same object.
- **Post-download extension** — every successful `GET /object/{token}` delays the deletion period futher, but only if that's later than whatever is currently scheduled. A repeated download never shortens the window.

Deletion is handled by a background process and retried a few times on transient failure — it is not synchronous with any triggering HTTP call.

---

## Pop

Pop one or more items from the front of the queue (lowest priority first, FIFO within a priority).

```
POST /queue/{queueId}/pop
```

**Headers**

| Header | Default | Description |
|---|---|---|
| `require-ack` | `false` | If `true`, creates a lock instead of permanently removing the item — see [Acknowledge](#acknowledge). |
| `count` | `1` | Number of items to pop (`0`–`100`). |
| `ttl-seconds` | `30` | Lock TTL in seconds, only used when `require-ack: true` (`1`–`300`). |
| `allow-competing-consumers` | `false` | Allow multiple parallel locks instead of the legacy single-lock behavior. |

**Response — `200 OK`**

```json
{ "items": [{ "item": { "task": "send_email" }, "priority": 0 }] }
```

**Response — `204 No Content`** — queue is empty.

**Response — `423 Locked`** — another lock is currently active and competing consumers aren't enabled.

**Example**

```bash
curl -X POST http://localhost:8002/queue/my-queue/pop \
  -H "require-ack: true" \
  -H "ttl-seconds: 60" \
  -H "count: 10"
```

---

## Acknowledge

Finalize a locked item (created via `pop` with `require-ack: true`), permanently removing it from the queue.

```
POST /queue/{queueId}/acknowledge
Content-Type: application/json
```

**Body**

```json
{ "lockId": "aB3xQ9k2LmZ" }
```

**Response — `200 OK`**

```json
{ "success": true, "message": "Successfully acknowledged 1 item", "itemsAcknowledged": 1 }
```

**Error responses** — `404` (`LOCK_NOT_FOUND`), `410` (`LOCK_EXPIRED`), `400` (`INVALID_LOCK_ID`).

**Example**

```bash
curl -X POST http://localhost:8002/queue/my-queue/acknowledge \
  -H "Content-Type: application/json" \
  -d '{"lockId": "aB3xQ9k2LmZ"}'
```

---

## Extend Lock

Extend the TTL of an existing lock.

```
POST /queue/{queueId}/extend-lock
Content-Type: application/json
```

**Body**

```json
{ "lockId": "aB3xQ9k2LmZ", "additionalTtlSeconds": 30 }
```

**Response — `200 OK`**

```json
{ "newExpiresAt": 1780000200, "lockId": "aB3xQ9k2LmZ" }
```

---

## Dead Letter

Move a locked item to its dead letter queue (`{queueId}-deadletter`) and void the lock.

```
POST /queue/{queueId}/deadletter
Content-Type: application/json
```

**Body**

```json
{ "lockId": "aB3xQ9k2LmZ" }
```

**Response — `200 OK`**

```json
{ "success": true, "message": "Item moved to dead letter queue", "dlqId": "my-queue-deadletter" }
```

The dead letter queue is itself a normal queue — pop from `{queueId}-deadletter` the same way you would any other queue.

---

## HTTP Sink

Register/unregister a pull-based push delivery sink that polls the queue and forwards items to an HTTP endpoint.

### Register

```
POST /queue/{queueId}/sink/http/register
Content-Type: application/json
```

```json
{ "url": "https://example.com/webhook", "maxConcurrency": 10, "lockTtlSeconds": 30 }
```

- `url` — must be a valid absolute URI.
- `maxConcurrency` — `1`–`100`. Caps how many locked items can be in flight at once; the sink calls `PopWithAck` with this as the concurrency limit.
- `lockTtlSeconds` — `1`–`300`. Lock TTL used for each `PopWithAck` call the sink makes.

Registering starts polling immediately (the sink actor calls `PopWithAck` on a reminder loop, starting at a 1s interval and backing off dynamically when the queue is empty — see [ARCHITECTURE.md](ARCHITECTURE.md)).

**Response — `200 OK`**

```json
{ "success": true, "message": "Sink registered successfully", "httpSinkActorId": "my-queue-sink" }
```

### Unregister

```
POST /queue/{queueId}/sink/http/unregister
```

Stops polling and clears sink state.

**Response — `200 OK`**

```json
{ "success": true, "message": "Sink unregistered successfully" }
```

### Delivery behavior by response status

Every poll, the sink pops a batch of items with `PopWithAck` and `POST`s them as a JSON array to the registered `url`. Your endpoint's response status code determines what the sink does next:

| Your endpoint responds with | Sink behavior |
|---|---|
| `200 OK` | **Delivery confirmed.** The sink immediately calls `Acknowledge` on every item's lock in the batch, permanently removing them from the queue — including items whose payload was offloaded to an object store (see [Delivering large objects](#delivering-large-objects)). The underlying blob isn't at risk: the backstop reap TTL gives you a window to fetch it via `GET /object/{token}` before the reaper deletes it. |
| `202 Accepted` | **Deferred acknowledgement.** The sink does *not* acknowledge anything — it assumes your endpoint will call `POST /queue/{queueId}/acknowledge` itself (e.g. after async processing completes) using the `lockId` from each delivered item. If you never acknowledge, the locks expire on their own TTL and the items are automatically re-queued for redelivery. |
| Any other status (`4xx`, `5xx`, timeout, connection refused, etc.) | **Treated as a failed delivery.** The sink does nothing further for that batch — no acknowledgement, no retry from the sink itself. The locks simply run out their `lockTtlSeconds` TTL and the items reappear at the front of the queue for the next poll (by this sink or a competing consumer) to pick up. |

Practical implications:

- **Idempotency matters.** Any non-`200`/non-`202` response (including a slow response that outlives the lock TTL) results in redelivery — design your endpoint to handle receiving the same item more than once.
- **`202` puts you in control of the ack, but also the risk.** If your endpoint accepts the delivery (`202`) but then crashes before acknowledging, the item redelivers once the lock expires — same as a hard failure. Use `202` when your processing is genuinely async and outlives the lock TTL; use `200` when processing completes synchronously within the request.
- **No sink-side retries or backoff on failure.** A failing endpoint doesn't get hammered with retries — the next attempt only happens on the item's normal lock-expiry/redelivery cycle, which naturally throttles retry pressure.
- **Delivered payload shape** — the POST body is a JSON array, one entry per popped item: `{"item": ..., "priority": 0, "lockId": "...", "lockExpiresAt": 1780000123.45}`. That's everything your endpoint needs to call `acknowledge`/`extend-lock`/`deadletter` on a `202` response.

### Delivering large objects

There is no difference in behaviour when pushing large objects via the HTTP sink, the consumer must manually download using the `objectClaimToken` -- same as when simply using the `/Pop` endpoint

```json
{
  "item": { "objectClaimToken": "eyJhbGciOiJIUzI1NiIs...", "contentType": "application/pdf" },
  "priority": 0,
  "lockId": "aB3xQ9k2LmZ",
  "lockExpiresAt": 1780000123.45
}
```

Your endpoint fetches the actual bytes itself, while the lock is still active, via:

```
GET /object/{token}
```

**Example**

```bash
curl -o report.pdf http://localhost:8002/object/eyJhbGciOiJIUzI1NiIs...
```

**Response — `200 OK`** — raw object bytes, with `Content-Type` set from the value recorded at push time (falls back to `application/octet-stream` if none was given).

**Error responses**:

| HTTP Status | Meaning |
|---|---|
| `400 Bad Request` | Token is malformed, tampered, or otherwise invalid |
| `410 Gone` | Token has expired |
| `404 Not Found` | Token is valid but the underlying object has already been deleted |

### Fetch the object before it's reaped

A `200 OK` response acknowledges blob-reference items the same as inline ones — the sink doesn't wait for you to download the object first. That means the underlying blob's cleanup clock (the backstop reap TTL, scheduled at `Acknowledge`) starts ticking as soon as the sink acknowledges, not when you fetch it. Download promptly via `GET /object/{token}`; a successful download extends the deletion window further (see [cleanup lifecycle](#cleanup-lifecycle)) but a very slow or never-attempted fetch risks the object having already been reaped.

If you need more processing time before committing to acknowledgement, use `202 Accepted` instead of `200 OK` — that defers the ack entirely (see the table above), giving you the full `lockTtlSeconds` window to fetch the object and call `acknowledge` yourself. `extend-lock`/`deadletter` also work against the same `lockId` in that case.

---

## Error Codes

| HTTP Status | Meaning |
|---|---|
| `204 No Content` | Queue is empty |
| `400 Bad Request` | Validation error (bad priority, count, lock id, etc.) |
| `404 Not Found` | Lock not found / actor not found |
| `410 Gone` | Lock has expired |
| `423 Locked` | Queue is locked by another in-flight operation |
| `500 Internal Server Error` | Unexpected server-side error |

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full list of internal `ErrorCode` values (`QueueEmpty`, `Locked`, `LockNotFound`, `LockExpired`, `ValidationError`, `ActorNotFound`).
