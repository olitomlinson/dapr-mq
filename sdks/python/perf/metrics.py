"""Turns raw operation, stream and handler records into the numbers of sdks/testing/PERFORMANCE_TESTS.md.
Pure, so it is unit tested; a port of LoadMetrics.cs, SessionDrainMetrics.cs and QueueDrainMetrics.cs
in sdks/dotnet/perf/DaprMQ.Client.Perf. Every dict is already in the result schema's camelCase."""

from __future__ import annotations

import math
from collections import defaultdict
from collections.abc import Iterable, Sequence
from dataclasses import dataclass

from .profiles import QueueDrainParams, SessionDrainParams

BUCKET_MS = 1000

STREAM_COMPLETED = "completed"
STREAM_ABANDONED = "abandoned"


def distribution(values: Iterable[float]) -> dict:
    """count, mean and nearest-rank percentiles."""
    ordered = sorted(values)
    if not ordered:
        return {"count": 0, "mean": 0, "p50": 0, "p95": 0, "p99": 0, "max": 0}

    def percentile(p: float) -> float:
        return ordered[min(max(math.ceil(p * len(ordered)) - 1, 0), len(ordered) - 1)]

    return {
        "count": len(ordered),
        "mean": sum(ordered) / len(ordered),
        "p50": percentile(0.50),
        "p95": percentile(0.95),
        "p99": percentile(0.99),
        "max": ordered[-1],
    }


# --- P-01..P-03 closed-loop load -------------------------------------------------------------


@dataclass(frozen=True)
class OpRecord:
    """One operation; times are ms from the start of the step (warmup included)."""

    end_ms: float
    latency_ms: float
    messages: int
    error: bool


def compute_step(concurrency: int, queues: int, ops: Sequence[OpRecord], warmup_ms: float, duration_ms: float) -> tuple[dict, dict]:
    """(step, timeline): only operations finishing inside [warmup, warmup + duration) count, and
    errors are counted but left out of latency."""
    window = [o for o in ops if warmup_ms <= o.end_ms < warmup_ms + duration_ms]
    seconds = duration_ms / 1000.0
    messages = sum(o.messages for o in window)

    step = {
        "concurrency": concurrency,
        "queues": queues,
        "durationSeconds": seconds,
        "ops": len(window),
        "messages": messages,
        "errors": sum(1 for o in window if o.error),
        "opsPerSecond": len(window) / seconds,
        "messagesPerSecond": messages / seconds,
        "latencyMs": distribution(o.latency_ms for o in window if not o.error),
    }

    buckets: list[list[OpRecord]] = [[] for _ in range(math.ceil(duration_ms / BUCKET_MS))]
    for op in window:
        buckets[int((op.end_ms - warmup_ms) // BUCKET_MS)].append(op)

    def percentile(bucket: list[OpRecord], name: str) -> float | None:
        ok = [o.latency_ms for o in bucket if not o.error]
        return round(distribution(ok)[name], 3) if ok else None

    timeline = {
        "opsPerSecond": [len(b) for b in buckets],
        "messagesPerSecond": [sum(o.messages for o in b) for b in buckets],
        "latencyP50Ms": [percentile(b, "p50") for b in buckets],
        "latencyP95Ms": [percentile(b, "p95") for b in buckets],
        "latencyP99Ms": [percentile(b, "p99") for b in buckets],
        "errors": [sum(1 for o in b if o.error) for b in buckets],
        "concurrency": [concurrency for _ in buckets],
    }
    return step, timeline


# --- P-04 session drain ----------------------------------------------------------------------


@dataclass(frozen=True)
class StreamRecord:
    """One consume_session stream as a consumer slot saw it, ms from the start of the consume phase.
    first_delivery_ms/session_id are None when the claim itself failed."""

    open_ms: float
    first_delivery_ms: float | None
    end_ms: float
    session_id: str | None
    end_reason: str


@dataclass(frozen=True)
class HandlerRecord:
    """One handler invocation, ms from the start of the consume phase; delivery latency is
    publish -> handler start."""

    session_id: str
    seq: int
    start_ms: float
    end_ms: float
    delivery_latency_ms: float | None = None


def _breakdown(
    slots: int,
    streams: Sequence[StreamRecord],
    delivered: list[tuple[StreamRecord, list[HandlerRecord]]],
    start: float,
    end: float,
) -> dict:
    """Where every slot-second in [start, end) went: handling + claim + in-session wait + drain
    wait + between streams = capacity."""

    def clip(a: float, b: float) -> float:
        return max(0.0, min(b, end) - max(a, start))

    handling = claim = in_session = drain = 0.0
    for s in streams:
        if s.first_delivery_ms is None:
            claim += clip(s.open_ms, s.end_ms)

    for s, hs in delivered:
        first = s.first_delivery_ms
        last_handler_end = min(hs[-1].end_ms, s.end_ms) if hs else first
        stream_handling = sum(clip(h.start_ms, h.end_ms) for h in hs)
        claim += clip(s.open_ms, first)
        handling += stream_handling
        in_session += max(0.0, clip(first, last_handler_end) - stream_handling)
        drain += clip(last_handler_end, s.end_ms)

    capacity = slots * max(0.0, end - start)
    in_streams = sum(clip(s.open_ms, s.end_ms) for s in streams)
    capacity_s, handling_s = capacity / 1000.0, handling / 1000.0
    return {
        "startSeconds": start / 1000.0,
        "endSeconds": end / 1000.0,
        "capacitySlotSeconds": capacity_s,
        "handlingSeconds": handling_s,
        "claimSeconds": claim / 1000.0,
        "inSessionWaitSeconds": in_session / 1000.0,
        "drainWaitSeconds": drain / 1000.0,
        "betweenStreamsSeconds": max(0.0, capacity - in_streams) / 1000.0,
        "idleSlotSeconds": capacity_s - handling_s,
        "utilization": handling_s / capacity_s if capacity_s > 0 else 0,
    }


def _busy_timeline(intervals: Iterable[tuple[float, float]], completed_ms: float, buckets: int) -> list[float]:
    """Average busy handlers per bucket."""
    busy = [0.0] * buckets
    for start, end in intervals:
        b = int(start // BUCKET_MS)
        while b < buckets and b * BUCKET_MS < end:
            busy[b] += max(0.0, min(end, (b + 1) * BUCKET_MS) - max(start, b * BUCKET_MS))
            b += 1
    return [round(ms / BUCKET_MS, 3) for ms in busy]


def compute_session_drain(
    scenario: SessionDrainParams,
    streams: Sequence[StreamRecord],
    handlers: Sequence[HandlerRecord],
    completed_ms: float,
    seed_seconds: float,
) -> dict:
    """The P-04 metrics, plus timelineBucketMs and busySlotsTimeline (moved out into the timeline
    when the run is recorded)."""
    ideal = scenario.ideal_seconds
    wall = completed_ms / 1000.0

    by_session: dict[str, list[HandlerRecord]] = defaultdict(list)
    for h in handlers:
        by_session[h.session_id].append(h)
    for hs in by_session.values():
        hs.sort(key=lambda h: h.start_ms)

    def handlers_of(s: StreamRecord) -> list[HandlerRecord]:
        return [h for h in by_session.get(s.session_id, []) if s.first_delivery_ms <= h.start_ms <= s.end_ms]

    delivered = [(s, handlers_of(s)) for s in streams if s.first_delivery_ms is not None and s.session_id is not None]
    first_handler_start = min((h.start_ms for h in handlers), default=0)

    # Peak = first delivery until the last session gets its first delivery; after that the slots
    # going idle is the unavoidable tail, not a claim/drain cost.
    last_session_start = max((hs[0].start_ms for hs in by_session.values()), default=0)
    peak_end = last_session_start if last_session_start > first_handler_start else completed_ms

    gaps = [b.start_ms - a.end_ms for _, hs in delivered for a, b in zip(hs, hs[1:])]
    unique = len({(h.session_id, h.seq) for h in handlers})
    claims: dict[str, int] = defaultdict(int)
    for s, _ in delivered:
        claims[s.session_id] += 1

    return {
        "seedSeconds": seed_seconds,
        "wallClockSeconds": wall,
        "idealSeconds": ideal,
        "efficiency": ideal / wall if wall > 0 else 0,
        "timeToFirstMessageSeconds": first_handler_start / 1000.0,
        "tailSeconds": (completed_ms - peak_end) / 1000.0,
        "peak": _breakdown(scenario.max_concurrent_sessions, streams, delivered, first_handler_start, peak_end),
        "overall": _breakdown(scenario.max_concurrent_sessions, streams, delivered, 0, completed_ms),
        "claimLatencyMs": distribution(s.first_delivery_ms - s.open_ms for s, _ in delivered),
        "drainWaitMs": distribution(s.end_ms - hs[-1].end_ms for s, hs in delivered if hs),
        "interMessageGapMs": distribution(gaps),
        "deliveryLatencyMs": distribution(h.delivery_latency_ms for h in handlers if h.delivery_latency_ms is not None),
        "streams": len(streams),
        "failedClaims": sum(1 for s in streams if s.first_delivery_ms is None),
        "sessionsClaimedMoreThanOnce": sum(1 for n in claims.values() if n > 1),
        "messagesHandled": len(handlers),
        "duplicates": len(handlers) - unique,
        "missing": max(0, scenario.sessions * scenario.messages_per_session - unique),
        "fifoViolations": sum(1 for hs in by_session.values() for a, b in zip(hs, hs[1:]) if b.seq < a.seq),
        "timelineBucketMs": BUCKET_MS,
        "busySlotsTimeline": _busy_timeline(
            ((h.start_ms, h.end_ms) for h in handlers), completed_ms, math.ceil(completed_ms / BUCKET_MS)
        ),
    }


# --- P-05 queue drain ------------------------------------------------------------------------


@dataclass(frozen=True)
class QueueHandlerRecord:
    seq: int
    start_ms: float
    end_ms: float
    delivery_latency_ms: float


def _peak_overlap(handled: Sequence[QueueHandlerRecord]) -> int:
    # Ends sort before starts at the same instant: back-to-back handlers don't overlap.
    events = sorted([(h.start_ms, 1) for h in handled] + [(h.end_ms, -1) for h in handled])
    running = peak = 0
    for _, delta in events:
        running += delta
        peak = max(peak, running)
    return peak


def compute_queue_drain(p: QueueDrainParams, handled: Sequence[QueueHandlerRecord], completed_ms: float, seed_seconds: float) -> dict:
    """The P-05 metrics, plus messagesPerSecondTimeline and busyHandlersTimeline (moved out into the
    timeline when the run is recorded)."""
    wall = completed_ms / 1000
    unique = len({h.seq for h in handled})
    ideal = p.ideal_seconds

    # Handler starts in time order; a message started after a later one is out of queue order.
    starts = [h.seq for h in sorted(handled, key=lambda h: h.start_ms)]
    order_violations = sum(1 for a, b in zip(starts, starts[1:]) if b < a)

    buckets = max(1, math.ceil(completed_ms / BUCKET_MS))
    finished = [0] * buckets
    for h in handled:
        finished[min(int(h.end_ms // BUCKET_MS), buckets - 1)] += 1

    return {
        "seedSeconds": seed_seconds,
        "wallClockSeconds": wall,
        "messagesPerSecond": unique / wall if wall > 0 else 0,
        "idealSeconds": ideal,
        "efficiency": ideal / wall if ideal is not None and wall > 0 else None,
        "timeToFirstMessageSeconds": min(h.start_ms for h in handled) / 1000 if handled else 0,
        "peakConcurrentHandlers": _peak_overlap(handled),
        "deliveryLatencyMs": distribution(h.delivery_latency_ms for h in handled),
        "messagesHandled": unique,
        "duplicates": len(handled) - unique,
        "missing": p.messages - unique,
        "orderViolations": order_violations,
        "messagesPerSecondTimeline": finished,
        "busyHandlersTimeline": _busy_timeline(((h.start_ms, h.end_ms) for h in handled), completed_ms, buckets),
    }
