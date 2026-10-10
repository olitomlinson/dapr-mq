# @daprmq/client

TypeScript client SDK for [DaprMQ](https://github.com/olitomlinson/dapr-mq), a FIFO queue built on Dapr actors.

```sh
npm install @daprmq/client
```

```ts
import { DaprMQClient } from "@daprmq/client";

const client = new DaprMQClient({ httpBaseUrl: "http://localhost:8002", grpcAddress: "localhost:8003" });
await client.enqueue("orders", [{ item: { id: 1 } }]);
```

DaprMQ is pre-release: versions are `0.0.0-alpha.N`, and `latest` is the newest of them until the first full release.

See the [SDK guide](https://github.com/olitomlinson/dapr-mq/blob/main/sdks/typescript/docs/CLIENT_SDK.md) for
consumers, sessions, retries and the full API. Licensed under Apache-2.0.
