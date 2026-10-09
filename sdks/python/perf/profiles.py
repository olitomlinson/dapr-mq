"""The profiles of sdks/testing/PERFORMANCE_TESTS.md, exactly as tabled there and in the .NET
reference (sdks/dotnet/perf/DaprMQ.Client.Perf/PerfOptions.cs). The `key` strings group runs across
SDKs on the report, so they must match the .NET ones character for character."""

from __future__ import annotations

import math
from dataclasses import dataclass

ENQUEUE = "enqueue"
ENQUEUE_BATCH = "enqueue-batch"
DEQUEUE_ACK = "dequeue-ack"

PUBLISH_BEFORE = "before"
PUBLISH_CONCURRENT = "concurrent"


@dataclass(frozen=True)
class LoadParams:
    """P-01..P-03 closed-loop load. More than one concurrency value makes it a ramp; queues None =
    one queue per worker."""

    scenario: str
    concurrency: tuple[int, ...]
    queues: int | None
    batch_size: int
    dequeue_count: int
    payload_bytes: int
    seed_per_queue: int
    warmup_seconds: int
    duration_seconds: int

    @property
    def id(self) -> str:
        return {ENQUEUE: "P-01", ENQUEUE_BATCH: "P-02", DEQUEUE_ACK: "P-03"}[self.scenario]

    def queues_at(self, concurrency: int) -> int:
        return self.queues if self.queues is not None else concurrency

    @property
    def key(self) -> str:
        queues = str(self.queues) if self.queues is not None else "=c"
        shape = f"/k{self.dequeue_count}/seed{self.seed_per_queue}" if self.scenario == DEQUEUE_ACK else f"/b{self.batch_size}"
        return (
            f"{self.scenario}:c{','.join(map(str, self.concurrency))}/q{queues}{shape}"
            f"/{self.payload_bytes}B/{self.warmup_seconds}+{self.duration_seconds}s"
        )

    def params(self) -> dict:
        return {
            "concurrency": list(self.concurrency),
            "queues": self.queues,
            "batchSize": self.batch_size,
            "dequeueCount": self.dequeue_count,
            "payloadBytes": self.payload_bytes,
            "seedPerQueue": self.seed_per_queue,
            "warmupSeconds": self.warmup_seconds,
            "durationSeconds": self.duration_seconds,
        }


@dataclass(frozen=True)
class SessionDrainParams:
    """P-04: sessions x messages drained by one SessionQueueConsumer."""

    sessions: int = 1000
    messages_per_session: int = 100
    settle_ms: int = 1000
    max_concurrent_sessions: int = 20
    prefetch_count: int = 10
    lease_seconds: int = 30
    session_idle_timeout_seconds: int = 0
    publish_mode: str = PUBLISH_BEFORE
    publish_interval_ms: int = 0
    publish_jitter_ms: int = 0

    @property
    def rate_limited(self) -> bool:
        return self.publish_interval_ms > 0 or self.publish_jitter_ms > 0

    @property
    def key(self) -> str:
        key = f"{self.sessions}x{self.messages_per_session}@{self.settle_ms}ms/{self.max_concurrent_sessions}slots"
        if self.publish_mode != PUBLISH_BEFORE:
            key += f"+{self.publish_mode}"
        if self.rate_limited:
            key += f"+pub{self.publish_interval_ms}ms~{self.publish_jitter_ms}ms"
        if self.session_idle_timeout_seconds != 0:
            key += f"+idle{self.session_idle_timeout_seconds}s"
        if self.prefetch_count != 10:
            key += f"+prefetch{self.prefetch_count}"
        if self.lease_seconds != 30:
            key += f"+lease{self.lease_seconds}s"
        return key

    @property
    def ideal_seconds(self) -> float:
        """Consumer-bound: ceil(sessions / slots) rounds of M x settle. Concurrent and rate limited, no
        session can finish before its last message is published (~M x mean delay) and settled."""
        consume_bound = math.ceil(self.sessions / self.max_concurrent_sessions) * self.messages_per_session * self.settle_ms / 1000.0
        if self.publish_mode != PUBLISH_CONCURRENT or not self.rate_limited:
            return consume_bound
        publish_bound = (self.messages_per_session * (self.publish_interval_ms + self.publish_jitter_ms / 2.0) + self.settle_ms) / 1000.0
        return max(consume_bound, publish_bound)

    def params(self) -> dict:
        return {
            "sessions": self.sessions,
            "messagesPerSession": self.messages_per_session,
            "settleMs": self.settle_ms,
            "maxConcurrentSessions": self.max_concurrent_sessions,
            "prefetchCount": self.prefetch_count,
            "leaseSeconds": self.lease_seconds,
            "sessionIdleTimeoutSeconds": self.session_idle_timeout_seconds,
            "publishMode": self.publish_mode,
            "publishIntervalMs": self.publish_interval_ms,
            "publishJitterMs": self.publish_jitter_ms,
        }


@dataclass(frozen=True)
class QueueDrainParams:
    """P-05: messages drained by one QueueConsumer; a publish interval publishes alongside it."""

    messages: int
    settle_ms: int
    max_active_messages: int
    max_concurrent_handlers: int
    strict_order: bool = False
    publish_interval_ms: int = 0
    publish_jitter_ms: int = 0
    tail_every: int = 0
    tail_ms: int = 0

    @property
    def live_publish(self) -> bool:
        return self.publish_interval_ms > 0 or self.publish_jitter_ms > 0

    @property
    def key(self) -> str:
        key = f"queue:{self.messages}@{self.settle_ms}ms/active{self.max_active_messages}"
        if self.max_concurrent_handlers > 0:
            key += f"/handlers{self.max_concurrent_handlers}"
        if self.strict_order:
            key += "+strict"
        if self.live_publish:
            key += f"+pub{self.publish_interval_ms}ms~{self.publish_jitter_ms}ms"
        if self.tail_every > 0:
            key += f"+tail{self.tail_ms}ms/{self.tail_every}"
        return key

    @property
    def concurrency(self) -> int:
        """Handlers that can run at once: strict order runs one, otherwise the window bounds them."""
        if self.strict_order:
            return 1
        if self.max_concurrent_handlers > 0:
            return min(self.max_concurrent_handlers, self.max_active_messages)
        return self.max_active_messages

    @property
    def tail_messages(self) -> int:
        return self.messages // self.tail_every if self.tail_every > 0 else 0

    def handler_ms(self, seq: int) -> int:
        return self.tail_ms if self.tail_every > 0 and (seq + 1) % self.tail_every == 0 else self.settle_ms

    @property
    def ideal_seconds(self) -> float | None:
        """The handler work spread over `concurrency`, but no less than the slowest single handler,
        nor than the last live-published message plus its handler. None for an instant handler."""
        if self.settle_ms == 0 and self.tail_ms == 0 and not self.live_publish:
            return None
        work_ms = (self.messages - self.tail_messages) * self.settle_ms + self.tail_messages * self.tail_ms
        slowest_ms = max(self.settle_ms, self.tail_ms) if self.tail_messages > 0 else self.settle_ms
        last_published_ms = (
            (self.messages - 1) * self.publish_interval_ms + self.handler_ms(self.messages - 1) if self.live_publish else 0
        )
        return max(work_ms / self.concurrency, slowest_ms, last_published_ms) / 1000

    def params(self) -> dict:
        return {
            "messages": self.messages,
            "settleMs": self.settle_ms,
            "maxActiveMessages": self.max_active_messages,
            "maxConcurrentHandlers": self.max_concurrent_handlers,
            "strictOrder": self.strict_order,
            "publishIntervalMs": self.publish_interval_ms,
            "publishJitterMs": self.publish_jitter_ms,
            "tailEvery": self.tail_every,
            "tailMs": self.tail_ms,
        }


LOAD_PROFILES: dict[str, LoadParams] = {
    "enqueue": LoadParams(ENQUEUE, (8,), 8, 1, 1, 256, 0, 3, 15),
    "enqueue-hot": LoadParams(ENQUEUE, (8,), 1, 1, 1, 256, 0, 3, 15),
    "enqueue-batch": LoadParams(ENQUEUE_BATCH, (4,), 4, 100, 1, 256, 0, 3, 15),
    "dequeue-ack": LoadParams(DEQUEUE_ACK, (8,), 8, 1, 1, 256, 4000, 3, 15),
    "enqueue-ramp": LoadParams(ENQUEUE, (1, 2, 4, 8, 16, 32, 64, 128, 256), None, 1, 1, 256, 0, 5, 30),
    "enqueue-hot-ramp": LoadParams(ENQUEUE, (1, 2, 4, 8, 16, 32, 64), 1, 1, 1, 256, 0, 5, 30),
    "enqueue-batch-ramp": LoadParams(ENQUEUE_BATCH, (1, 2, 4, 8, 16, 32, 64), None, 100, 1, 256, 0, 5, 30),
    "dequeue-ack-ramp": LoadParams(DEQUEUE_ACK, (1, 2, 4, 8, 16, 32, 64, 128, 256), None, 1, 1, 256, 12000, 5, 30),
}

SESSION_DRAIN_PROFILES: dict[str, SessionDrainParams] = {
    "steady-drain": SessionDrainParams(sessions=200, messages_per_session=20, settle_ms=100, session_idle_timeout_seconds=1),
    "session-churn": SessionDrainParams(sessions=300, messages_per_session=2, settle_ms=50, session_idle_timeout_seconds=1),
    "deep-session": SessionDrainParams(
        sessions=4, messages_per_session=1000, settle_ms=10, max_concurrent_sessions=4, session_idle_timeout_seconds=1
    ),
    "live-publish": SessionDrainParams(
        sessions=20, messages_per_session=50, settle_ms=100, session_idle_timeout_seconds=1,
        publish_mode=PUBLISH_CONCURRENT, publish_interval_ms=200, publish_jitter_ms=100,
    ),  # fmt: skip
    "sdk-defaults": SessionDrainParams(sessions=40, messages_per_session=5),
    "full": SessionDrainParams(),
    "wide-drain": SessionDrainParams(
        sessions=2000, messages_per_session=10, settle_ms=50, max_concurrent_sessions=200, session_idle_timeout_seconds=1
    ),
}

QUEUE_DRAIN_PROFILES: dict[str, QueueDrainParams] = {
    "queue-drain-instant": QueueDrainParams(4000, 0, 100, 0),
    "queue-drain": QueueDrainParams(4000, 10, 100, 0),
    "queue-drain-slow": QueueDrainParams(3000, 100, 100, 0),
    "queue-strict-order": QueueDrainParams(300, 0, 100, 0, strict_order=True),
    "queue-live-publish": QueueDrainParams(50, 10, 100, 0, publish_interval_ms=200, publish_jitter_ms=100),
    "queue-drain-large": QueueDrainParams(50000, 10, 500, 0),
    "queue-drain-tail": QueueDrainParams(2000, 100, 100, 0, tail_every=20, tail_ms=5000),
}

SUITES: dict[str, list[str]] = {
    "pr": [
        "enqueue", "enqueue-hot", "enqueue-batch", "dequeue-ack",
        "steady-drain", "session-churn", "deep-session", "live-publish", "sdk-defaults",
        "queue-drain-instant", "queue-drain", "queue-drain-slow", "queue-strict-order", "queue-live-publish",
    ],
    "extreme": [
        "enqueue-ramp", "enqueue-hot-ramp", "enqueue-batch-ramp", "dequeue-ack-ramp",
        "full", "wide-drain", "queue-drain-large", "queue-drain-tail",
    ],
}  # fmt: skip

PROFILES = {**LOAD_PROFILES, **SESSION_DRAIN_PROFILES, **QUEUE_DRAIN_PROFILES}


def scale_of(profile: str) -> str:
    return next((suite for suite, profiles in SUITES.items() if profile in profiles), "adhoc")
