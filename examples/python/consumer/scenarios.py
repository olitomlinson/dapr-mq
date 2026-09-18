"""Consumer-side implementation of the 4 demo scenarios.

See examples/shared/SCENARIOS.md for the exact business logic each of these
functions must follow. Each function dequeues/acks/dead-letters items on
`queue_id` using `client` (a `daprmq_client.DaprMQClient`), logs every step
via `log(level, message)`, and returns the list of `{"action", "detail"}`
step dicts for the `POST /scenarios/{name}/run` response body.

**Empty-queue tolerance**: per API_CONTRACT.md, a consumer scenario finding
nothing to dequeue (e.g. the producer's scenario hasn't run yet) is not an
error — it returns 200 with a step noting the empty queue.
"""
from __future__ import annotations

import asyncio
from collections.abc import Callable

from daprmq_client import DaprMQClient

LogFn = Callable[[str, str], None]

EMPTY_QUEUE_DETAIL = "queue empty — run the producer's scenario first"


async def run_basic(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    result = await client.dequeue_locked(queue_id, count=5)
    if result is None or not result.items:
        log("INFO", f"Queue {queue_id} empty — run the producer's scenario first")
        return [{"action": "dequeue", "detail": EMPTY_QUEUE_DETAIL}]

    log("INFO", f"Dequeued {len(result.items)} items from {queue_id}")
    steps = [{"action": "dequeue", "detail": f"dequeued {len(result.items)} items"}]

    for it in result.items:
        await client.acknowledge(queue_id, it.lock_id)
        log("INFO", f"Acknowledged item {it.item} (lockId={it.lock_id})")
        steps.append({"action": "acknowledge", "detail": f"acked item {it.item}"})

    return steps


async def run_ack_deadletter(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    dlq_id = f"{queue_id}-deadletter"

    result = await client.dequeue_locked(queue_id, count=3, ttl_seconds=10)
    if result is None or not result.items:
        log("INFO", f"Queue {queue_id} empty — run the producer's scenario first")
        return [{"action": "dequeue", "detail": EMPTY_QUEUE_DETAIL}]

    lock_ids = [it.lock_id for it in result.items]
    log("INFO", f"Dequeued {len(result.items)} items from {queue_id}, lockIds={lock_ids}")
    steps = [{"action": "dequeue", "detail": f"dequeued {len(result.items)} items, lockIds={lock_ids}"}]

    expire_pending = False
    for it in result.items:
        outcome = (it.item or {}).get("outcome", "ack") if isinstance(it.item, dict) else "ack"

        if outcome == "deadletter":
            await client.dead_letter(queue_id, it.lock_id)
            log("INFO", f"Dead-lettered item outcome=deadletter -> dlqId={dlq_id}")
            steps.append({"action": "deadletter", "detail": f"dead-lettered item outcome=deadletter -> dlqId={dlq_id}"})
        elif outcome == "expire":
            expire_pending = True
            log("INFO", "Leaving item outcome=expire locked so its lock can expire")
        else:
            await client.acknowledge(queue_id, it.lock_id)
            log("INFO", f"Acknowledged item outcome={outcome}")
            steps.append({"action": "acknowledge", "detail": f"acked item outcome={outcome}"})

    if expire_pending:
        log("INFO", "Waiting 11s for outcome=expire item's lock to expire")
        steps.append({"action": "wait", "detail": "waiting 11s for outcome=expire item's lock to expire"})
        await asyncio.sleep(11)

        redelivered = await client.dequeue_locked(queue_id, count=3, ttl_seconds=10)
        if redelivered is not None and redelivered.items:
            log("INFO", f"Dequeued {len(redelivered.items)} redelivered item(s) from {queue_id}")
            steps.append({"action": "dequeue", "detail": "dequeued expired item again via redelivery"})
            for it in redelivered.items:
                await client.acknowledge(queue_id, it.lock_id)
                log("INFO", f"Acknowledged redelivered item outcome=expire ({it.item})")
                steps.append({"action": "acknowledge", "detail": "acked redelivered item outcome=expire (drained)"})
        else:
            log("WARN", f"Expected a redelivered item from {queue_id} but none arrived")
            steps.append({"action": "dequeue", "detail": "no redelivered item found on retry (unexpected)"})

    dlq_result = await client.dequeue_locked(dlq_id, count=1, ttl_seconds=10)
    if dlq_result is not None and dlq_result.items:
        for it in dlq_result.items:
            log("INFO", f"Dequeued dead-lettered item from {dlq_id}: {it.item}")
            steps.append({"action": "dequeue", "detail": f"dequeued dead-lettered item from {dlq_id}: {it.item}"})
            await client.acknowledge(dlq_id, it.lock_id)
            log("INFO", f"Acknowledged item from {dlq_id} (drained)")
            steps.append({"action": "acknowledge", "detail": f"acked item from {dlq_id} (drained)"})
    else:
        log("INFO", f"DLQ {dlq_id} empty — nothing to drain")
        steps.append({"action": "dequeue", "detail": f"DLQ {dlq_id} empty — nothing to drain"})

    return steps


async def run_priority(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    result = await client.dequeue_locked(queue_id, count=10)
    if result is None or not result.items:
        log("INFO", f"Queue {queue_id} empty — run the producer's scenario first")
        return [{"action": "dequeue", "detail": EMPTY_QUEUE_DETAIL}]

    order = [it.item.get("n") if isinstance(it.item, dict) else None for it in result.items]
    log("INFO", f"Dequeued {len(result.items)} items from {queue_id} in order n={order}")
    steps = [{"action": "dequeue", "detail": f"dequeued {len(result.items)} items in order n={order}"}]

    for it in result.items:
        await client.acknowledge(queue_id, it.lock_id)
        n = it.item.get("n") if isinstance(it.item, dict) else None
        log("INFO", f"Acknowledged item n={n} (priority={it.priority})")
        steps.append({"action": "acknowledge", "detail": f"acked item n={n} (priority={it.priority})"})

    return steps


KNOWN_SESSION_IDS = ("session-a", "session-b")


async def _drain_session(client: DaprMQClient, queue_id: str, session_id: str, lease_id: str, log: LogFn) -> list[dict]:
    steps: list[dict] = []
    session_queue_id = f"{queue_id}-session-{session_id}"
    try:
        dequeued = await client.dequeue_locked(session_queue_id, count=10, ttl_seconds=30, lease_id=lease_id)
        if dequeued is None or not dequeued.items:
            log("WARN", f"No items dequeued for session {session_id} from {session_queue_id}")
            steps.append(
                {"action": "dequeue", "detail": f"queue empty for session {session_id} — run the producer's scenario first"}
            )
        else:
            seqs = [it.item.get("seq") if isinstance(it.item, dict) else None for it in dequeued.items]
            log("INFO", f"Dequeued {len(dequeued.items)} items for session {session_id} in order seq={seqs}")
            steps.append(
                {
                    "action": "dequeue",
                    "detail": f"dequeued {len(dequeued.items)} items for session {session_id} in order seq={seqs}",
                }
            )
            for it in dequeued.items:
                await client.acknowledge(session_queue_id, it.lock_id, lease_id=lease_id)
                seq = it.item.get("seq") if isinstance(it.item, dict) else None
                log("INFO", f"Acknowledged item session={session_id} seq={seq}")
                steps.append({"action": "acknowledge", "detail": f"acked item session={session_id} seq={seq}"})
    finally:
        await client.release_session(queue_id, session_id, lease_id)
        log("INFO", f"Released session {session_id}")
        steps.append({"action": "release_session", "detail": f"released session {session_id}"})

    return steps


async def run_sessions(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    steps: list[dict] = []

    # Round 1: "any available" mode (session_id=None) - the server picks whichever
    # of the known sessions is currently unclaimed.
    claimed_first: str | None = None
    any_lease = await client.accept_session(queue_id, session_id=None, lease_seconds=30)
    if any_lease is None:
        log("WARN", f"No session currently available (any-available mode) on {queue_id}")
        steps.append({"action": "accept_session", "detail": "no session currently available (any-available mode)"})
    else:
        claimed_first = any_lease.session_id
        log("INFO", f"Accepted session {claimed_first} via any-available mode leaseId={any_lease.lease_id}")
        steps.append(
            {"action": "accept_session", "detail": f"accepted session {claimed_first} via any-available mode leaseId={any_lease.lease_id}"}
        )
        steps.extend(await _drain_session(client, queue_id, claimed_first, any_lease.lease_id, log))

    # Round 2: targeted mode - accept whichever known session round 1 didn't return
    # (both, if round 1 found nothing available).
    for session_id in KNOWN_SESSION_IDS:
        if session_id == claimed_first:
            continue

        lease = await client.accept_session(queue_id, session_id=session_id, lease_seconds=30)
        if lease is None:
            log("INFO", f"Session {session_id} not available (targeted mode) on {queue_id}")
            steps.append(
                {"action": "accept_session", "detail": f"session {session_id} not available (targeted mode)"}
            )
            continue

        log("INFO", f"Accepted session {session_id} via targeted mode leaseId={lease.lease_id}")
        steps.append(
            {"action": "accept_session", "detail": f"accepted session {session_id} via targeted mode leaseId={lease.lease_id}"}
        )
        steps.extend(await _drain_session(client, queue_id, session_id, lease.lease_id, log))

    return steps


async def run_idempotency(client: DaprMQClient, queue_id: str, log: LogFn) -> list[dict]:
    """Dequeues from the queue the producer's idempotency scenario just filled.
    Expects exactly 2 items (n=1 and n=3) since n=2, the duplicate, should never
    have been enqueued at all.
    """
    result = await client.dequeue_locked(queue_id, count=5)
    if result is None or not result.items:
        log("INFO", f"Queue {queue_id} empty — run the producer's scenario first")
        return [{"action": "dequeue", "detail": EMPTY_QUEUE_DETAIL}]

    order = [it.item.get("n") if isinstance(it.item, dict) else None for it in result.items]
    log(
        "INFO",
        f"Dequeued {len(result.items)} item(s) n={order} from {queue_id} - expecting 2 (n=1, n=3); "
        "the duplicate n=2 should have been silently dropped by idempotency dedup",
    )
    steps = [
        {
            "action": "dequeue",
            "detail": f"dequeued {len(result.items)} item(s) n={order} - expecting 2 (n=1, n=3)",
        }
    ]

    for it in result.items:
        await client.acknowledge(queue_id, it.lock_id)
        n = it.item.get("n") if isinstance(it.item, dict) else None
        log("INFO", f"Acknowledged item n={n}")
        steps.append({"action": "acknowledge", "detail": f"acked item n={n}"})

    return steps


RUNNERS: dict[str, Callable] = {
    "basic": run_basic,
    "ack-deadletter": run_ack_deadletter,
    "priority": run_priority,
    "sessions": run_sessions,
    "idempotency": run_idempotency,
}
