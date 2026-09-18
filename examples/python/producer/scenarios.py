"""Producer-side implementation of the 4 demo scenarios.

See examples/shared/SCENARIOS.md for the exact business logic each of these
functions must follow. Each function enqueues items to `queue_id` using
`client` (a `daprmq_client.DaprMQClient`), logs every step via `log(level,
message)`, and returns the list of `{"action", "detail"}` step dicts for the
`POST /scenarios/{name}/run` response body.
"""
from __future__ import annotations

import time
from collections.abc import Callable

from daprmq_client import DaprMQClient, EnqueueItem

LogFn = Callable[[str, str], None]


async def run_basic(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    items = [
        EnqueueItem(item={"n": 1, "message": "hello from producer"}),
        EnqueueItem(item={"n": 2, "message": "hello from producer"}),
        EnqueueItem(item={"n": 3, "message": "hello from producer"}),
    ]
    await client.enqueue(queue_id, items)

    steps = []
    for i, pi in enumerate(items, start=1):
        log("INFO", f"Enqueued item {i}/{len(items)} to queue {queue_id} (priority={pi.priority})")
        steps.append({"action": "enqueue", "detail": f"enqueued item n={pi.item['n']} to {queue_id} (priority={pi.priority})"})
    return steps


async def run_ack_deadletter(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    items = [
        EnqueueItem(item={"outcome": "ack", "n": 1}),
        EnqueueItem(item={"outcome": "deadletter", "n": 2}),
        EnqueueItem(item={"outcome": "expire", "n": 3}),
    ]
    await client.enqueue(queue_id, items)

    steps = []
    for i, pi in enumerate(items, start=1):
        outcome = pi.item["outcome"]
        log("INFO", f"Enqueued item {i}/{len(items)} to queue {queue_id}: outcome={outcome} n={pi.item['n']}")
        steps.append({"action": "enqueue", "detail": f"enqueued item outcome={outcome} n={pi.item['n']} to {queue_id}"})
    return steps


async def run_priority(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    normal_items = [EnqueueItem(item={"priority": 1, "n": n}, priority=1) for n in (1, 2, 3)]
    fast_items = [EnqueueItem(item={"priority": 0, "n": n}, priority=0) for n in (4, 5, 6)]

    steps: list[dict] = []

    await client.enqueue(queue_id, normal_items)
    for pi in normal_items:
        log("INFO", f"Enqueued item n={pi.item['n']} to {queue_id} (priority={pi.priority})")
        steps.append({"action": "enqueue", "detail": f"enqueued item n={pi.item['n']} to {queue_id} (priority={pi.priority})"})

    await client.enqueue(queue_id, fast_items)
    for pi in fast_items:
        log("INFO", f"Enqueued item n={pi.item['n']} to {queue_id} (priority={pi.priority})")
        steps.append({"action": "enqueue", "detail": f"enqueued item n={pi.item['n']} to {queue_id} (priority={pi.priority})"})

    return steps


async def run_sessions(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    items = [
        EnqueueItem(item={"sessionId": "session-a", "seq": 1}, session_id="session-a"),
        EnqueueItem(item={"sessionId": "session-a", "seq": 2}, session_id="session-a"),
        EnqueueItem(item={"sessionId": "session-b", "seq": 1}, session_id="session-b"),
        EnqueueItem(item={"sessionId": "session-b", "seq": 2}, session_id="session-b"),
    ]
    await client.enqueue(queue_id, items)

    steps = []
    for pi in items:
        log("INFO", f"Enqueued item sessionId={pi.session_id} seq={pi.item['seq']} to {queue_id}")
        steps.append({"action": "enqueue", "detail": f"enqueued item sessionId={pi.session_id} seq={pi.item['seq']} to {queue_id}"})
    return steps


async def run_idempotency(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    """Enqueues an item, a duplicate reusing its idempotency key (expected to be
    deduplicated), then a distinct item under a different key. The key is
    suffixed with the current time so repeated runs of this scenario don't
    collide with a previous run's key still inside the dedup TTL window.
    """
    key = f"idempotency-demo-{int(time.time() * 1000)}"
    steps: list[dict] = []

    first = await client.enqueue(queue_id, [EnqueueItem(item={"n": 1}, idempotency_key=key)])
    log(
        "INFO",
        f"Enqueued item n=1 with idempotencyKey={key} "
        f"(itemsEnqueued={first.items_enqueued}, itemsDeduplicated={first.items_deduplicated})",
    )
    steps.append(
        {
            "action": "enqueue",
            "detail": f"enqueued item n=1 with idempotencyKey={key} "
            f"(itemsEnqueued={first.items_enqueued}, itemsDeduplicated={first.items_deduplicated})",
        }
    )

    duplicate = await client.enqueue(queue_id, [EnqueueItem(item={"n": 2}, idempotency_key=key)])
    log(
        "INFO",
        f"Enqueued item n=2 reusing idempotencyKey={key} "
        f"(itemsEnqueued={duplicate.items_enqueued}, itemsDeduplicated={duplicate.items_deduplicated}) "
        "- expected to be deduplicated",
    )
    steps.append(
        {
            "action": "enqueue",
            "detail": f"enqueued item n=2 reusing idempotencyKey={key} "
            f"(itemsEnqueued={duplicate.items_enqueued}, itemsDeduplicated={duplicate.items_deduplicated})",
        }
    )

    distinct_key = f"{key}-b"
    distinct = await client.enqueue(queue_id, [EnqueueItem(item={"n": 3}, idempotency_key=distinct_key)])
    log(
        "INFO",
        f"Enqueued item n=3 with a different idempotencyKey={distinct_key} "
        f"(itemsEnqueued={distinct.items_enqueued}, itemsDeduplicated={distinct.items_deduplicated})",
    )
    steps.append(
        {
            "action": "enqueue",
            "detail": f"enqueued item n=3 with a different idempotencyKey={distinct_key} "
            f"(itemsEnqueued={distinct.items_enqueued}, itemsDeduplicated={distinct.items_deduplicated})",
        }
    )

    return steps


RUNNERS: dict[str, Callable] = {
    "basic": run_basic,
    "ack-deadletter": run_ack_deadletter,
    "priority": run_priority,
    "sessions": run_sessions,
    "idempotency": run_idempotency,
}
