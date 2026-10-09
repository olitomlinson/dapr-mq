"""The scenarios, driving the public SDK surface (DaprMQClient, SessionQueueConsumer, QueueConsumer).
Ports of LoadScenario.cs, SessionDrainScenario.cs and QueueDrainScenario.cs in
sdks/dotnet/perf/DaprMQ.Client.Perf."""

from __future__ import annotations

import asyncio
import contextlib
import random
import time
import uuid
from collections.abc import AsyncIterator, Callable
from typing import Any

from daprmq_client import (
    DaprMQClient,
    EnqueueItem,
    QueueConsumer,
    QueueConsumerOptions,
    QueueMessageContext,
    SessionDelivery,
    SessionMessageContext,
    SessionQueueConsumer,
    SessionQueueConsumerOptions,
)

from .metrics import (
    STREAM_ABANDONED,
    STREAM_COMPLETED,
    HandlerRecord,
    OpRecord,
    QueueHandlerRecord,
    StreamRecord,
    compute_queue_drain,
    compute_session_drain,
    compute_step,
)
from .profiles import DEQUEUE_ACK, ENQUEUE_BATCH, PUBLISH_CONCURRENT, LoadParams, QueueDrainParams, SessionDrainParams
from .records import LoadResult

SEED_BATCH = 100
SEED_PARALLELISM = 16
MAX_SAMPLE_ERRORS = 5


def _now_unix_ms() -> int:
    return int(time.time() * 1000)


class _Clock:
    """Milliseconds on a monotonic clock since start()."""

    def __init__(self) -> None:
        self._start: float | None = None

    def start(self) -> None:
        self._start = time.perf_counter()

    def ms(self) -> float:
        return 0.0 if self._start is None else (time.perf_counter() - self._start) * 1000


def publish_delays(messages: int, interval_ms: int, jitter_ms: int, rng: random.Random) -> list[int]:
    """Delay before each message: the first after a random 0..jitter start offset (so publishers
    don't run in lockstep), each later one after interval + random 0..jitter."""
    return [(0 if i == 0 else interval_ms) + (rng.randint(0, jitter_ms) if jitter_ms > 0 else 0) for i in range(messages)]


async def _gather_limited(limit: int, jobs: list[Callable[[], Any]]) -> None:
    semaphore = asyncio.Semaphore(limit)

    async def run(job: Callable[[], Any]) -> None:
        async with semaphore:
            await job()

    await asyncio.gather(*(run(job) for job in jobs))


async def _progress(describe: Callable[[], str]) -> None:
    while True:
        await asyncio.sleep(30)
        print(f"  {describe()}", flush=True)


# --- P-01..P-03 closed-loop load -------------------------------------------------------------


class LoadScenario:
    """Each step runs `concurrency` workers back to back on one shared client for warmup +
    duration, worker w on queue w % queues. A ramp is a sequence of steps, each on fresh queues."""

    def __init__(self, client: DaprMQClient, load: LoadParams) -> None:
        self.client = client
        self.load = load

    async def run(self) -> LoadResult:
        load = self.load
        steps: list[dict] = []
        timelines: list[dict] = []
        errors: list[str] = []
        drained = False
        pad = "x" * load.payload_bytes

        for concurrency in load.concurrency:
            queues = load.queues_at(concurrency)
            run_id = uuid.uuid4().hex[:12]
            # No "-session-" in the base id: QueueActor treats that marker as a per-session actor.
            queue_ids = [f"perf-{load.scenario}-{run_id}-q{q}" for q in range(queues)]

            if load.scenario == DEQUEUE_ACK:
                seed_start = time.perf_counter()
                await self._seed(queue_ids, pad)
                print(f"  seeded {queues} x {load.seed_per_queue} in {time.perf_counter() - seed_start:.1f}s", flush=True)

            warmup_ms = load.warmup_seconds * 1000.0
            duration_ms = load.duration_seconds * 1000.0
            clock = _Clock()
            clock.start()
            per_worker: list[list[OpRecord]] = [[] for _ in range(concurrency)]
            step_drained = False

            async def worker(w: int) -> None:
                nonlocal step_drained
                records = per_worker[w]
                queue_id = queue_ids[w % queues]
                seq = 0
                while clock.ms() < warmup_ms + duration_ms:
                    start = clock.ms()
                    error = False
                    try:
                        messages = await self._operation(queue_id, w, seq, pad)
                        seq += max(messages, 1)
                    except Exception as exc:  # noqa: BLE001 - counted, and the worker carries on
                        messages, error = 0, True
                        if len(errors) < MAX_SAMPLE_ERRORS:
                            errors.append(f"{type(exc).__name__}: {exc}")

                    if messages < 0:
                        step_drained = True
                        break

                    end = clock.ms()
                    records.append(OpRecord(end, end - start, messages, error))

            await asyncio.gather(*(worker(w) for w in range(concurrency)))

            step, timeline = compute_step(concurrency, queues, [r for rs in per_worker for r in rs], warmup_ms, duration_ms)
            steps.append(step)
            timelines.append(timeline)
            drained |= step_drained
            latency = step["latencyMs"]
            print(
                f"  c={concurrency:<4} q={queues:<4} {step['messagesPerSecond']:10.0f} msg/s  p50 {latency['p50']:7.2f}  "
                f"p95 {latency['p95']:7.2f}  p99 {latency['p99']:7.2f} ms  errors {step['errors']}{'  DRAINED' if step_drained else ''}",
                flush=True,
            )

        return LoadResult(steps, timelines, drained, errors)

    async def _operation(self, queue_id: str, worker: int, seq: int, pad: str) -> int:
        """One operation: the messages it moved, or -1 when a dequeue found the queue empty."""
        load = self.load
        if load.scenario == DEQUEUE_ACK:
            result = await self.client.dequeue_locked(queue_id, count=load.dequeue_count)
            if result is None or not result.items:
                return -1
            for item in result.items:
                await self.client.acknowledge(queue_id, item.lock_id)
            return len(result.items)

        batch = load.batch_size if load.scenario == ENQUEUE_BATCH else 1
        enqueued = await self.client.enqueue(queue_id, self._items(worker, seq, batch, pad))
        if not enqueued.success or enqueued.items_enqueued != batch:
            raise RuntimeError(f"Enqueue returned {enqueued.items_enqueued}/{batch}: {enqueued.message}")
        return batch

    @staticmethod
    def _items(worker: int, seq: int, count: int, pad: str) -> list[EnqueueItem]:
        published_at = _now_unix_ms()
        return [EnqueueItem(item={"worker": worker, "seq": n, "publishedAt": published_at, "pad": pad}) for n in range(seq, seq + count)]

    async def _seed(self, queue_ids: list[str], pad: str) -> None:
        per_queue = self.load.seed_per_queue

        # Batches for one queue go in order (one actor serialises them anyway); queues in parallel.
        async def seed_queue(q: int, queue_id: str) -> None:
            for start in range(0, per_queue, SEED_BATCH):
                count = min(SEED_BATCH, per_queue - start)
                result = await self.client.enqueue(queue_id, self._items(q, start, count, pad))
                if not result.success or result.items_enqueued != count:
                    raise RuntimeError(f"Seeding {queue_id} failed: {result.message}")

        await _gather_limited(SEED_PARALLELISM, [lambda q=q, qid=qid: seed_queue(q, qid) for q, qid in enumerate(queue_ids)])


# --- P-04 session drain ----------------------------------------------------------------------


class RecordingClient:
    """Decorates the client handed to SessionQueueConsumer, timestamping each consume_session stream
    (open, first delivery, end): with the handler intervals that accounts for every slot-second."""

    def __init__(self, inner: DaprMQClient, clock_ms: Callable[[], float]) -> None:
        self._inner = inner
        self._clock_ms = clock_ms
        self.streams: list[StreamRecord] = []

    async def consume_session(self, queue_id: str, **kwargs: Any) -> AsyncIterator[SessionDelivery]:
        open_ms = self._clock_ms()
        first_delivery_ms: float | None = None
        session_id: str | None = None
        end_reason = STREAM_ABANDONED
        inner = self._inner.consume_session(queue_id, **kwargs)
        try:
            async with contextlib.aclosing(inner):
                try:
                    async for delivery in inner:
                        if first_delivery_ms is None:
                            first_delivery_ms, session_id = self._clock_ms(), delivery.session_id
                        yield delivery
                except Exception as exc:
                    end_reason = type(exc).__name__
                    raise
                else:
                    end_reason = STREAM_COMPLETED
        finally:
            self.streams.append(StreamRecord(open_ms, first_delivery_ms, self._clock_ms(), session_id, end_reason))


class SessionDrainScenario:
    """Seed sessions x messages, then drain them with one SessionQueueConsumer whose handler takes
    settle_ms per message. Completion is the moment every (session, seq) has been handled once."""

    def __init__(self, client: DaprMQClient, scenario: SessionDrainParams) -> None:
        self.client = client
        self.scenario = scenario

    async def run(self) -> dict:
        s = self.scenario
        queue_id = f"perf-drain-{uuid.uuid4().hex}"
        print(
            f"Queue {queue_id}: {s.key}, prefetch {s.prefetch_count}, lease {s.lease_seconds}s, "
            f"idle-timeout {s.session_idle_timeout_seconds}s, publish {s.publish_mode}",
            flush=True,
        )

        # The consume clock: it starts with the consumer, which in concurrent mode is also when
        # publishing starts - so there the wall clock includes the publish.
        clock = _Clock()
        recorder = RecordingClient(self.client, clock.ms)
        handlers: list[HandlerRecord] = []
        seen: set[tuple[str, int]] = set()
        expected = s.sessions * s.messages_per_session
        all_handled = asyncio.Event()
        completed_ms = 0.0

        async def handler(ctx: SessionMessageContext) -> None:
            nonlocal completed_ms
            start = clock.ms()
            delivery_latency_ms = _now_unix_ms() - ctx.item["publishedAt"]
            await asyncio.sleep(s.settle_ms / 1000)
            end = clock.ms()
            seq = ctx.item["seq"]
            handlers.append(HandlerRecord(ctx.session_id, seq, start, end, delivery_latency_ms))
            key = (ctx.session_id, seq)
            if key not in seen:
                seen.add(key)
                if len(seen) == expected:
                    completed_ms = end
                    all_handled.set()

        consumer = SessionQueueConsumer(
            recorder,
            queue_id,
            SessionQueueConsumerOptions(
                max_concurrent_sessions=s.max_concurrent_sessions,
                prefetch_count=s.prefetch_count,
                lease_seconds=s.lease_seconds,
                session_idle_timeout_seconds=s.session_idle_timeout_seconds,
                drain_timeout_seconds=5,
            ),
            handler,
        )
        timeout = s.ideal_seconds * 3 + 600
        progress = asyncio.ensure_future(
            _progress(lambda: f"t={clock.ms() / 1000:.0f}s  handled {len(seen)}/{expected}  streams {len(recorder.streams)}")
        )
        try:
            if s.publish_mode == PUBLISH_CONCURRENT:
                clock.start()
                consumer.start()
                try:
                    seed_seconds = await self._seed(queue_id)
                    await self._wait(all_handled, timeout, lambda: len(seen), expected)
                finally:
                    await consumer.stop()
            else:
                seed_seconds = await self._seed(queue_id)
                clock.start()
                consumer.start()
                try:
                    await self._wait(all_handled, timeout, lambda: len(seen), expected)
                finally:
                    await consumer.stop()
        finally:
            progress.cancel()

        return compute_session_drain(s, recorder.streams, handlers, completed_ms, seed_seconds)

    @staticmethod
    async def _wait(done: asyncio.Event, timeout: float, handled: Callable[[], int], expected: int) -> None:
        try:
            await asyncio.wait_for(done.wait(), timeout)
        except asyncio.TimeoutError:
            raise TimeoutError(f"Only {handled()}/{expected} messages handled after {timeout:.0f}s.") from None

    async def _seed(self, queue_id: str) -> float:
        s = self.scenario
        start = time.perf_counter()
        if s.rate_limited:
            # Every session publishes at once, one message at a time on its own schedule.
            async def publish(session: int) -> None:
                for seq, delay in enumerate(publish_delays(s.messages_per_session, s.publish_interval_ms, s.publish_jitter_ms, random.Random())):
                    await asyncio.sleep(delay / 1000)
                    await self._enqueue(queue_id, self._session_id(session), [seq])

            await asyncio.gather(*(publish(session) for session in range(s.sessions)))
        else:
            await _gather_limited(
                SEED_PARALLELISM,
                [lambda n=n: self._enqueue(queue_id, self._session_id(n), range(s.messages_per_session)) for n in range(s.sessions)],
            )
        seconds = time.perf_counter() - start
        print(f"Seeded {s.sessions * s.messages_per_session} messages in {seconds:.1f}s", flush=True)
        return seconds

    @staticmethod
    def _session_id(session: int) -> str:
        return f"s{session:05d}"

    async def _enqueue(self, queue_id: str, session_id: str, seqs) -> None:
        # Same process publishes and consumes, so wall-clock ms is a consistent publish -> handler clock.
        published_at = _now_unix_ms()
        items = [EnqueueItem(item={"session": session_id, "seq": seq, "publishedAt": published_at}, session_id=session_id) for seq in seqs]
        result = await self.client.enqueue(queue_id, items)
        if not result.success or result.items_enqueued != len(items):
            raise RuntimeError(f"Publishing to session {session_id} failed: {result.message}")


# --- P-05 queue drain ------------------------------------------------------------------------


class QueueDrainScenario:
    """Publish messages to a plain queue and drain them with one QueueConsumer whose handler takes
    settle_ms per message. Completion is the moment every seq has been handled once."""

    def __init__(self, client: DaprMQClient, scenario: QueueDrainParams) -> None:
        self.client = client
        self.scenario = scenario

    async def run(self) -> dict:
        s = self.scenario
        queue_id = f"perf-queue-{uuid.uuid4().hex}"
        print(f"Queue {queue_id}: {s.key}", flush=True)

        clock = _Clock()
        handled: list[QueueHandlerRecord] = []
        seen: set[int] = set()
        all_handled = asyncio.Event()
        completed_ms = 0.0

        async def handler(ctx: QueueMessageContext) -> None:
            nonlocal completed_ms
            start = clock.ms()
            delivery_latency_ms = _now_unix_ms() - ctx.item["publishedAt"]
            seq = ctx.item["seq"]
            settle_ms = s.handler_ms(seq)
            if settle_ms > 0:
                await asyncio.sleep(settle_ms / 1000)
            end = clock.ms()
            handled.append(QueueHandlerRecord(seq, start, end, delivery_latency_ms))
            if seq not in seen:
                seen.add(seq)
                if len(seen) == s.messages:
                    completed_ms = end
                    all_handled.set()

        consumer = QueueConsumer(
            self.client,
            queue_id,
            QueueConsumerOptions(
                max_active_messages=s.max_active_messages,
                max_concurrent_handlers=s.max_concurrent_handlers,
                strict_order=s.strict_order,
                drain_timeout_seconds=5,
            ),
            handler,
        )
        timeout = (s.ideal_seconds or 0) * 3 + 600
        progress = asyncio.ensure_future(_progress(lambda: f"t={clock.ms() / 1000:.0f}s  handled {len(seen)}/{s.messages}"))
        try:
            # With a live publisher the clock starts with publishing, so the wall clock includes it.
            if s.live_publish:
                clock.start()
                consumer.start()
                seed_seconds = await self._publish_live(queue_id)
            else:
                seed_seconds = await self._seed(queue_id)
                clock.start()
                consumer.start()
            print(f"Published {s.messages} messages in {seed_seconds:.1f}s", flush=True)
            try:
                await asyncio.wait_for(all_handled.wait(), timeout)
            except asyncio.TimeoutError:
                raise TimeoutError(f"Only {len(seen)}/{s.messages} messages handled after {timeout:.0f}s.") from None
            finally:
                await consumer.stop()
        finally:
            progress.cancel()

        return compute_queue_drain(s, handled, completed_ms, seed_seconds)

    async def _seed(self, queue_id: str) -> float:
        s = self.scenario
        start = time.perf_counter()
        # Parallel batches land out of seq order; strict order checks handler order against queue
        # order, so it seeds with one publisher.
        batches = [range(b, min(b + SEED_BATCH, s.messages)) for b in range(0, s.messages, SEED_BATCH)]
        await _gather_limited(1 if s.strict_order else SEED_PARALLELISM, [lambda b=b: self._enqueue(queue_id, b) for b in batches])
        return time.perf_counter() - start

    async def _publish_live(self, queue_id: str) -> float:
        s = self.scenario
        start = time.perf_counter()
        for seq, delay in enumerate(publish_delays(s.messages, s.publish_interval_ms, s.publish_jitter_ms, random.Random())):
            await asyncio.sleep(delay / 1000)
            await self._enqueue(queue_id, [seq])
        return time.perf_counter() - start

    async def _enqueue(self, queue_id: str, seqs) -> None:
        published_at = _now_unix_ms()
        items = [EnqueueItem(item={"seq": seq, "publishedAt": published_at}) for seq in seqs]
        result = await self.client.enqueue(queue_id, items)
        if not result.success or result.items_enqueued != len(items):
            raise RuntimeError(f"Publishing to {queue_id} failed: {result.message}")
