# daprmq-client

Async Python client SDK for [DaprMQ](https://github.com/olitomlinson/dapr-mq), a FIFO queue built on Dapr actors.

```sh
pip install daprmq-client --pre
```

```python
from daprmq_client import DaprMQClient, EnqueueItem

async with DaprMQClient(http_base_url="http://localhost:8002", grpc_address="localhost:8003") as client:
    await client.enqueue("orders", [EnqueueItem(item={"id": 1})])
```

DaprMQ is pre-release: versions are `0.0.0aN`, so `pip` needs `--pre` (or an explicit version) to install one.

See the [SDK guide](https://github.com/olitomlinson/dapr-mq/blob/main/sdks/python/docs/CLIENT_SDK.md) for
consumers, sessions, retries and the full API. Licensed under Apache-2.0.
