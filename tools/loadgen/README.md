# Load generator

Steady, varied traffic against a DaprMQ gateway, so the [admin console](../../docs/CONSOLE.md) has something to show.

- **Producers:** each queue is fed at a rate that swings on a slow wave, so depth rises and falls. About 10% of items go
  to the priority 0 fast lane.
- **Consumers:** competing consumers lock-dequeue in batches, "work", then acknowledge.
- **Poison and expiry:** about 1% of items are poison. Consumers always nack those, so they reach the queue's DLQ after
  `MaxDeliveryCount`. Another 1% of locks are abandoned, to expire and be redelivered.
- **Topic:** a publisher feeds a topic with two subscribers.

It prints per-second rates every 10 seconds.

## In docker compose

The `loadgen` service is opt-in (profile `load`):

```bash
docker compose --profile load up -d --build loadgen   # start
docker compose logs -f loadgen                         # rates every 10 s
docker compose stop loadgen                            # stop
```

`LOADGEN_QUEUES` and `LOADGEN_PRODUCE_RATE` on the host override the queues and rate. For anything else, edit the
service's `environment`.

## Standalone

```bash
GATEWAY_URL=http://localhost:8002 python3 tools/loadgen/loadgen.py
```

## Settings

| Variable | Default | |
|---|---|---|
| `GATEWAY_URL` | `http://localhost:8002` | DaprMQ gateway (REST) |
| `QUEUES` | `orders,emails,payments` | Queues to produce to and consume from |
| `PRODUCE_RATE` | `20` | Average items/s per queue |
| `PRODUCE_WAVE` / `WAVE_PERIOD_SECONDS` | `0.6` / `300` | The rate swings ±60% over 5 minutes |
| `CONSUMERS` | `2` | Consumer threads per queue |
| `CONSUME_BATCH` / `WORK_MS` | `10` / `400` | Items per locked dequeue / simulated work per batch |
| `LOCK_TTL_SECONDS` | `30` | Lock TTL; abandoned items come back after it |
| `PRIORITY0_PERCENT` | `10` | Items sent to the fast lane |
| `POISON_PERCENT` | `1` | Items always nacked, so dead-lettered |
| `ABANDON_PERCENT` | `1` | Locks left to expire |
| `TOPIC` / `TOPIC_SUBSCRIBERS` | `news` / `audit,search` | Topic to publish to (`""` skips it) |
| `PUBLISH_RATE` | `2` | Topic items/s |
| `PAYLOAD_BYTES` | `200` | Approximate size of each item |
| `DURATION_SECONDS` | `0` | Stop after this long; `0` runs until stopped |
