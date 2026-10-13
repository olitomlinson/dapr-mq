# Load generator

Steady, varied traffic against a DaprMQ gateway, so the [admin console](../../docs/CONSOLE.md) has something to show.

- **Producers:** each queue is fed at a rate that swings on a slow wave, so depth rises and falls. About 10% of items go
  to the priority 0 fast lane.
- **Consumers:** competing consumers take items, "work", then acknowledge. They are either unary (REST locked
  dequeues in batches, polling when empty) or streaming (the Python SDK's `QueueConsumer` over the gRPC `Consume`
  stream, which keeps a window of messages in flight and renews their locks). Set it with `CONSUMER_MODE`.
- **Poison and expiry:** about 1% of items are poison. Consumers always nack those, so they reach the queue's DLQ after
  `MaxDeliveryCount`. In unary mode, another 1% of locks are abandoned, to expire and be redelivered.
- **Topic:** a publisher feeds a topic with two subscribers.

It prints per-second rates every 10 seconds.

## In docker compose

The `loadgen` service is opt-in (profile `load`):

```bash
docker compose --profile load up -d --build loadgen   # start
docker compose logs -f loadgen                         # rates every 10 s
docker compose stop loadgen                            # stop
```

`LOADGEN_QUEUES`, `LOADGEN_PRODUCE_RATE` and `LOADGEN_CONSUMER_MODE` on the host override the queues, rate and
consumer mode, for example `LOADGEN_CONSUMER_MODE=stream docker compose --profile load up -d --build loadgen`. For
anything else, edit the service's `environment`.

The image builds from the repo root (`docker build -f tools/loadgen/Dockerfile .`), since it installs the Python SDK
from `sdks/python` for stream mode.

## Standalone

```bash
GATEWAY_URL=http://localhost:8002 python3 tools/loadgen/loadgen.py
```

Unary mode needs only the standard library. Stream mode needs the SDK: `pip install ./sdks/python`.

## Settings

| Variable | Default | |
|---|---|---|
| `GATEWAY_URL` | `http://localhost:8002` | DaprMQ gateway (REST) |
| `GRPC_ADDRESS` | `localhost:8102` | Gateway gRPC (`host:port`), for stream mode |
| `CONSUMER_MODE` | `unary` | `unary` (REST locked dequeues) or `stream` (`QueueConsumer` over the `Consume` stream) |
| `QUEUES` | `orders,emails,payments` | Queues to produce to and consume from |
| `PRODUCE_RATE` | `20` | Average items/s per queue |
| `PRODUCE_WAVE` / `WAVE_PERIOD_SECONDS` | `0.6` / `300` | The rate swings ±60% over 5 minutes |
| `CONSUMERS` | `2` | Consumers per queue (threads, or `QueueConsumer`s in stream mode) |
| `CONSUME_BATCH` / `WORK_MS` | `10` / `400` | Unary: items per locked dequeue / work per batch. Stream: messages in flight / work per message |
| `LOCK_TTL_SECONDS` | `30` | Lock TTL; abandoned items come back after it |
| `PRIORITY0_PERCENT` | `10` | Items sent to the fast lane |
| `POISON_PERCENT` | `1` | Items always nacked, so dead-lettered |
| `ABANDON_PERCENT` | `1` | Unary mode: locks left to expire |
| `TOPIC` / `TOPIC_SUBSCRIBERS` | `news` / `audit,search` | Topic to publish to (`""` skips it) |
| `PUBLISH_RATE` | `2` | Topic items/s |
| `PAYLOAD_BYTES` | `200` | Approximate size of each item |
| `DURATION_SECONDS` | `0` | Stop after this long; `0` runs until stopped |
