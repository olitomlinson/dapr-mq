# daprmq-client

Async Python client SDK for [DaprMQ](https://github.com/olitomlinson/dapr-mq), a FIFO queue built on Dapr actors.

```sh
pip install daprmq-client
```

```python
from daprmq_client import DaprMQClient, EnqueueItem

async with DaprMQClient(http_base_url="http://localhost:8002", grpc_address="localhost:8003") as client:
    await client.enqueue("orders", [EnqueueItem(item={"id": 1})])
```

DaprMQ is pre-release: versions are `0.0.0aN`. Don't add `--pre`: it lets pip pick pre-releases of every
dependency too.

See the [SDK guide](https://github.com/olitomlinson/dapr-mq/blob/main/sdks/python/docs/CLIENT_SDK.md) for
consumers, sessions, retries and the full API. Licensed under Apache-2.0.
