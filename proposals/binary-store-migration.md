# Proposal: Binary Store for large-message offload (Dapr 1.19)

> **Status: proposal only.** Build this once Dapr 1.19.0 and a GA `Dapr.BinaryStore` package ship.
> On 2026-10-17 the latest releases are runtime `v1.19.0-rc.4` and dotnet-sdk `v1.19.0-rc.2`.

## Context

`POST /queue/{id}/enqueue-object` puts large payloads in object storage, through
`IObjectStore` → [DaprBindingObjectStore.cs](server/src/DaprMQ.ApiServer/Services/DaprBindingObjectStore.cs)
(Dapr output bindings `create`/`get`/`delete`). Problems with this today:
- **Fully buffered.** Upload copies `Request.Body` into a `MemoryStream`, and download does `response.Data.ToArray()`. daprd buffers too, so the payload size is capped by `-max-body-size 16Mi` ([docker-compose.yml:115](docker-compose.yml#L115)).
- **Per-binding hacks.** The same key is set under three metadata names (`fileName`/`blobName`/`key`), and each binding reports errors in its own shape.
- **localstorage needs a shared volume.** That forces an RWX PVC in k8s and a bind-mounted directory in every compose, test and SDK stack.

The README already lists this as a pending item ([README.md:162](README.md#L162)).

**What the Binary Store offers (alpha, `v1.0-alpha1`):**
- gRPC calls: client-streaming `SetBinaryFileAlpha1` (with an `overwrite` flag), server-streaming `GetBinaryFileAlpha1`, and unary `DeleteBinaryFileAlpha1`.
- Components: `binarystore.azure.blobstorage`, `aws.s3`, `gcp.bucket`, `azure-datalake`, `oci-objectstorage`, `in-memory`. Every one of them supports a `prefix` metadata field.
- Not supported: content-type or other metadata, TTL, list.
- Errors: NotFound, AlreadyExists, 413, 507.
- .NET SDK (`Dapr.BinaryStore`): register with `services.AddDaprBinaryStoreClient()`, then use `DaprBinaryStoreClient`:
  - `SetBinaryFileAsync(store, name, Stream, overwrite, chunkSize)`
  - `GetBinaryFileChunksAsync(store, name)` → `IAsyncEnumerable<ReadOnlyMemory<byte>>`
  - `DeleteBinaryFileAsync(store, name)`
  - Failures arrive as `DaprException` wrapping an `RpcException`.

**Decisions made:**
- Start fresh: delete the bindings entirely, with no data migration and no dual-backend switch.
- Use Azurite + `binarystore.azure.blobstorage` for local and dev environments.
- Wait for GA before building.

## Approach

`IObjectStore` ([IObjectStore.cs](server/src/DaprMQ.Interfaces/IObjectStore.cs)) is the only place that touches storage. `QueueController`, `BlobReaperActor`, `AdminReadService`, claim tokens, the reaper TTLs and every client SDK stay as they are. Replace the single implementation.

### 1. Packages and versions
- Set `DaprActorsSdkVersion` to `1.19.x` in the three csproj files, plus the hard-coded `Dapr.AspNetCore` and `Dapr.Testcontainers` pins.
- Add `Dapr.BinaryStore` to [DaprMQ.ApiServer.csproj](server/src/DaprMQ.ApiServer/DaprMQ.ApiServer.csproj).
- Runtime 1.19.x everywhere:
  - `.env` (daprd, placement, scheduler)
  - [DaprTestEnvironment.cs](server/tests/DaprMQ.IntegrationTests/Infrastructure/DaprTestEnvironment.cs) (4 pins)
  - SDK integration stacks: go `main_test.go`, python `stack.py`, ts `daprmqServer.ts`, java `DaprMQServer.java`, `sdks/testing/INTEGRATION_TESTS.md`
  - `site/index.html`

### 2. New `DaprBinaryStoreObjectStore : IObjectStore` (TDD; replaces `DaprBindingObjectStore`)
To make it testable, put a thin internal seam over `DaprBinaryStoreClient`, or mock it directly if its members are virtual. Check this against the GA package.
- **Upload:** keep the prefix regex check. Key = `{userPrefix}/{objectId}`. Call `SetBinaryFileAsync(store, key, content, overwrite: false)`, which streams with no `MemoryStream`. The objectId is a GUID, so AlreadyExists can't happen.
- **Download:** read the first chunk eagerly, so NotFound is raised *before* response headers go out, then return a read-only `Stream` that yields the remaining chunks. The controller and the admin console keep consuming a `Stream`.
- **Delete:** treat NotFound as success. The reaper becomes idempotent instead of retrying up to `MaxAttempts` on a blob that is already gone.
- Map `DaprException { InnerException: RpcException NotFound }` → a new `ObjectNotFoundException` in DaprMQ.Interfaces.

Delete `DaprBindingObjectStore.cs` and `DaprBindingObjectStoreTests.cs`.

### 3. Config and wiring ([Program.cs:195-202](server/src/DaprMQ.ApiServer/Program.cs#L195-L202))
- `builder.Services.AddDaprBinaryStoreClient()`, then register `IObjectStore` → `DaprBinaryStoreObjectStore`.
- `ObjectStoreConfig`: replace `BindingName` with `StoreName`, read from `DAPRMQ_BLOB_STORE_NAME` (default `daprmq-blobstore`).
- **Drop `DAPRMQ_BLOB_GLOBAL_PREFIX` / `GlobalPrefix`.** The component's `prefix` metadata does the same job on every backend.
- **Size cap:** once the 16Mi daprd cap is gone, Kestrel's default 30 MB becomes the effective limit. Add `DAPRMQ_OBJECT_MAX_BYTES`, applied per request on `EnqueueObject` through `IHttpMaxRequestBodySizeFeature`.

### 4. Controller (small, test first in `QueueControllerLargeObjectTests.cs`)
- `GET /object/{token}` and the admin payload read: `ObjectNotFoundException` → **404** (today a missing blob gives a 500).
- Map Binary Store 413 → 413 and 507 → 507 on `enqueue-object` (today any failure gives a 500).

### 5. Components and infra (delete the bindings, add Binary Store)
- **Delete** `objectstore.yaml` and `objectstore-azure.yaml` from `server/dapr/components`, `server/dapr/helm-components`, the server integration tests, and the dotnet SDK tests and perf stacks.
- **Add** `server/dapr/components/blobstore.yaml`:
  ```yaml
  apiVersion: dapr.io/v1alpha1
  kind: Component
  metadata: { name: daprmq-blobstore }
  spec:
    type: binarystore.azure.blobstorage
    version: v1
    metadata:
    - { name: accountName, value: devstoreaccount1 }
    - { name: accountKey,  value: "<azurite well-known key>" }
    - { name: containerName, value: daprmq-blobstore }
    - { name: endpoint, value: "http://azurite:10000/devstoreaccount1" }
  scopes: [daprmq-service, daprmq-gateway]
  ```
  Check the exact Azurite endpoint form (with or without the account path segment) against the GA component.
- **docker-compose:** Azurite already exists at [docker-compose.yml:497](docker-compose.yml#L497). Remove the `blobstore-data` mounts (4 sidecars) and the `DAPRMQ_BLOB_BINDING_NAME` override in `.env`.
- **Server integration tests:** start an Azurite Testcontainer on the test network in `DaprTestEnvironment`, swap the component, and drop `BlobStoreDirectory` and the `/tmp/blobstore` mounts.
- **SDK integration stacks** (go, py, ts, java, dotnet tests and perf): none of these SDKs touch large objects. Delete the component and the `/tmp/blobstore` mount; no Azurite needed. Without the component daprd still starts, and only `enqueue-object` would fail.
- **Helm** ([values.yaml:161-194](helm/values.yaml#L161-L194)):
  - Remove `localStorage.*`, `globalPrefix`, and the `dapr.io/volume-mounts` blocks in `deployment-{gateway,worker}.yaml`.
  - Rename `bindingName` → `storeName` and update the env mapping.
  - Rewrite `helm-components/objectstore-k8s.yaml` as a `binarystore.azure.blobstorage` example that reads the connection string from a secret, with an s3 variant in comments.

### 6. Tests to change
- Unit: new `DaprBinaryStoreObjectStoreTests` covering:
  - upload streams the original stream with no buffering, using the correct key and `overwrite:false`
  - prefix rejection
  - download yields the chunks in order
  - download NotFound → `ObjectNotFoundException` before any bytes are returned
  - delete NotFound is swallowed
  - other errors propagate
- Controller: 404 on a missing blob; 413/507 passthrough; `DAPRMQ_OBJECT_MAX_BYTES` enforced.
- `BlobReaperActorTests`: delete of a missing blob → stop reaping, no retry. This may already pass with the store swallowing NotFound; add it as a regression test.
- Integration [LargeObjectTests.cs](server/tests/DaprMQ.IntegrationTests/Tests/LargeObjectTests.cs):
  - Replace the filesystem assertions (`Directory.GetFiles`) with Azurite checks. Either use `Azure.Storage.Blobs` as a test-only package, or call the sidecar's `GET /v1.0-alpha1/binarystore/daprmq-blobstore/{key}` and expect 404.
  - Add a round trip **larger than 16 MiB** to prove streaming works.

### 7. Docs
- [API_REFERENCE.md:70-167](docs/API_REFERENCE.md#L70-L167): new 404/413/507 behaviour, size cap env var, and the component type.
- README: mark "Optimised large message support" done.
- ARCHITECTURE: the object store section.
- Helm README.
- `CLAUDE.md`: no change.

## Risks
- **Alpha API.** The proto names (`*Alpha1`) and SDK surface may change at GA. Re-check the SDK signatures and the Azurite `endpoint` form before building.
- **No content-type in the store.** Nothing changes, because content-type already lives in `BlobReferenceEnvelope` and the claim token.
- **Name rules** forbid `\ * ? [ ] #`, `.`/`..` segments, and leading or trailing `/`. The existing prefix regex plus GUID object ids already meet these.
- **Breaking for operators.** The env vars are renamed or removed, and binding CRs must be replaced. Call this out in release notes.

## Verification (at build time)
1. `dotnet test` in `server/tests/DaprMQ.Tests/`: all green.
2. `./build-and-test.sh`: LargeObjectTests pass against Azurite, including the >16 MiB case.
3. `docker compose up`, then `curl --data-binary @50MB.bin .../enqueue-object`, then dequeue, then `GET /object/{token}`. Compare checksums and watch the gateway's memory stay flat.
4. SDK integration suites (go, py, ts, java, dotnet) pass with no blob component.
5. `helm template` renders without the volume-mounts or localStorage blocks.
