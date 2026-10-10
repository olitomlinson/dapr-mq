"""Ports of LoadMetricsTests, SessionDrainMetricsTests and QueueDrainMetricsTests (.NET)."""

import pytest

from perf.metrics import (
    HandlerRecord,
    OpRecord,
    QueueHandlerRecord,
    StreamRecord,
    compute_queue_drain,
    compute_session_drain,
    compute_step,
    distribution,
)
from perf.profiles import QueueDrainParams, SessionDrainParams

# --- load ---


def test_only_operations_finishing_inside_the_recorded_window_count() -> None:
    ops = [OpRecord(500, 99, 1, False), OpRecord(1000, 10, 1, False), OpRecord(1500, 20, 1, False), OpRecord(2999, 30, 1, False), OpRecord(3000, 99, 1, False)]

    step, _ = compute_step(2, 2, ops, warmup_ms=1000, duration_ms=2000)

    assert step["ops"] == 3
    assert step["opsPerSecond"] == pytest.approx(1.5)
    assert step["latencyMs"]["p50"] == 20
    assert step["latencyMs"]["max"] == 30
    assert step["durationSeconds"] == 2


def test_messages_count_batch_items_and_errors_are_counted_but_left_out_of_latency() -> None:
    ops = [OpRecord(100, 10, 100, False), OpRecord(200, 5000, 0, True), OpRecord(300, 20, 100, False)]

    step, _ = compute_step(1, 1, ops, warmup_ms=0, duration_ms=1000)

    assert (step["ops"], step["messages"], step["errors"]) == (3, 200, 1)
    assert step["messagesPerSecond"] == pytest.approx(200)
    assert step["latencyMs"]["count"] == 2
    assert step["latencyMs"]["max"] == 20


def test_timeline_buckets_by_second_of_completion_with_null_percentiles_for_empty_seconds() -> None:
    ops = [OpRecord(1100, 10, 1, False), OpRecord(1900, 30, 1, False), OpRecord(3500, 40, 2, False), OpRecord(3600, 50, 0, True)]

    _, timeline = compute_step(4, 4, ops, warmup_ms=1000, duration_ms=3000)

    assert timeline["opsPerSecond"] == [2, 0, 2]
    assert timeline["messagesPerSecond"] == [2, 0, 2]
    assert timeline["latencyP95Ms"] == [30, None, 40]
    assert timeline["errors"] == [0, 0, 1]
    assert timeline["concurrency"] == [4, 4, 4]


def test_percentiles_use_nearest_rank() -> None:
    d = distribution(float(i) for i in range(1, 101))
    assert (d["p50"], d["p95"], d["p99"], d["max"]) == (50, 95, 99, 100)


# --- session drain ---


def scenario(sessions: int, messages: int, slots: int, settle_ms: int = 1000) -> SessionDrainParams:
    return SessionDrainParams(sessions, messages, settle_ms, slots, prefetch_count=10, lease_seconds=30, session_idle_timeout_seconds=0)


def stream(session_id, open_ms, first, end, reason="completed") -> StreamRecord:
    return StreamRecord(open_ms, first, end, session_id, reason)


def handler(session_id: str, seq: int, start: float, end: float, latency: float | None = None) -> HandlerRecord:
    return HandlerRecord(session_id, seq, start, end, latency)


def test_perfectly_packed_slots_are_fully_utilised() -> None:
    streams = [stream("a", 0, 0, 2000), stream("b", 0, 0, 2000)]
    handlers = [handler("a", 0, 0, 1000), handler("a", 1, 1000, 2000), handler("b", 0, 0, 1000), handler("b", 1, 1000, 2000)]

    r = compute_session_drain(scenario(2, 2, 2), streams, handlers, completed_ms=2000, seed_seconds=0.5)

    assert r["wallClockSeconds"] == pytest.approx(2)
    assert r["idealSeconds"] == pytest.approx(2)
    assert r["efficiency"] == pytest.approx(1)
    assert r["seedSeconds"] == 0.5
    assert r["peak"]["utilization"] == pytest.approx(1)
    assert r["peak"]["idleSlotSeconds"] == pytest.approx(0)
    assert r["messagesHandled"] == 4


def test_idle_slot_time_is_attributed_to_claim_drain_and_between_streams() -> None:
    # One slot, two single-message sessions: claim 100 ms, 1 s handler, 3 s idle-drain, a 200 ms gap, then again.
    streams = [stream("a", 0, 100, 4100), stream("b", 4300, 4400, 8400)]
    handlers = [handler("a", 0, 100, 1100), handler("b", 0, 4400, 5400)]

    r = compute_session_drain(scenario(2, 1, 1), streams, handlers, completed_ms=5400, seed_seconds=0)

    peak = r["peak"]
    assert (peak["startSeconds"], peak["endSeconds"]) == (pytest.approx(0.1), pytest.approx(4.4))
    assert peak["capacitySlotSeconds"] == pytest.approx(4.3)
    assert peak["handlingSeconds"] == pytest.approx(1.0)
    assert peak["claimSeconds"] == pytest.approx(0.1)
    assert peak["drainWaitSeconds"] == pytest.approx(3.0)
    assert peak["betweenStreamsSeconds"] == pytest.approx(0.2)
    assert peak["inSessionWaitSeconds"] == pytest.approx(0.0)
    assert peak["idleSlotSeconds"] == pytest.approx(3.3)
    assert peak["utilization"] == pytest.approx(1.0 / 4.3)

    overall = r["overall"]
    assert overall["capacitySlotSeconds"] == pytest.approx(5.4)
    assert overall["handlingSeconds"] == pytest.approx(2.0)
    assert overall["claimSeconds"] == pytest.approx(0.2)
    assert overall["drainWaitSeconds"] == pytest.approx(3.0)
    assert overall["betweenStreamsSeconds"] == pytest.approx(0.2)

    assert r["tailSeconds"] == pytest.approx(1.0)
    assert r["timeToFirstMessageSeconds"] == pytest.approx(0.1)
    assert r["claimLatencyMs"]["count"] == 2
    assert r["claimLatencyMs"]["p50"] == pytest.approx(100)
    assert r["drainWaitMs"]["max"] == pytest.approx(3000)


def test_gaps_between_handlers_in_one_stream_are_in_session_wait() -> None:
    r = compute_session_drain(scenario(1, 2, 1), [stream("a", 0, 0, 2500)], [handler("a", 0, 0, 1000), handler("a", 1, 1500, 2500)], 2500, 0)

    assert r["overall"]["inSessionWaitSeconds"] == pytest.approx(0.5)
    assert r["interMessageGapMs"]["count"] == 1
    assert r["interMessageGapMs"]["p50"] == pytest.approx(500)


def test_a_stream_that_never_delivered_counts_as_a_failed_claim_and_reclaims_are_counted() -> None:
    streams = [stream(None, 0, None, 300, "NoSessionsAvailableError"), stream("a", 300, 400, 1400), stream("a", 1400, 1500, 2500)]
    handlers = [handler("a", 0, 400, 1400), handler("a", 1, 1500, 2500)]

    r = compute_session_drain(scenario(1, 2, 1), streams, handlers, completed_ms=2500, seed_seconds=0)

    assert r["failedClaims"] == 1
    assert r["streams"] == 3
    assert r["overall"]["claimSeconds"] == pytest.approx(0.5)
    assert r["sessionsClaimedMoreThanOnce"] == 1


def test_ordering_duplicates_and_missing_messages_are_detected() -> None:
    handlers = [
        handler("a", 0, 0, 10), handler("a", 2, 10, 20), handler("a", 1, 20, 30),  # out of order
        handler("b", 0, 0, 10), handler("b", 0, 10, 20),  # redelivered
    ]  # fmt: skip

    r = compute_session_drain(scenario(2, 3, 2), [], handlers, completed_ms=30, seed_seconds=0)

    assert (r["fifoViolations"], r["duplicates"], r["missing"], r["messagesHandled"]) == (1, 1, 2, 5)


def test_delivery_latency_is_distributed_over_handlers_that_recorded_it() -> None:
    handlers = [handler("a", 0, 0, 10, 100), handler("a", 1, 10, 20, 300), handler("a", 2, 20, 30)]

    r = compute_session_drain(scenario(1, 3, 1), [stream("a", 0, 0, 30)], handlers, completed_ms=30, seed_seconds=0)

    assert (r["deliveryLatencyMs"]["count"], r["deliveryLatencyMs"]["p50"], r["deliveryLatencyMs"]["max"]) == (2, 100, 300)


def test_session_timeline_reports_average_busy_slots_per_second() -> None:
    r = compute_session_drain(scenario(1, 1, 1), [stream("a", 0, 500, 2500)], [handler("a", 0, 500, 2500)], 2500, 0)

    assert r["timelineBucketMs"] == 1000
    assert r["busySlotsTimeline"] == [0.5, 1.0, 0.5]


# --- queue drain ---


def queue(messages: int = 4, settle_ms: int = 1000, active: int = 100, handlers: int = 2, strict: bool = False) -> QueueDrainParams:
    return QueueDrainParams(messages, settle_ms, active, handlers, strict)


def test_throughput_wall_clock_and_first_message_come_from_the_handler_records() -> None:
    handled = [QueueHandlerRecord(0, 100, 1100, 50), QueueHandlerRecord(1, 120, 1120, 70), QueueHandlerRecord(2, 1100, 2100, 1050), QueueHandlerRecord(3, 1120, 2000, 1070)]

    r = compute_queue_drain(queue(), handled, completed_ms=2100, seed_seconds=0.5)

    assert r["wallClockSeconds"] == pytest.approx(2.1)
    assert r["messagesPerSecond"] == pytest.approx(4 / 2.1)
    assert r["timeToFirstMessageSeconds"] == pytest.approx(0.1)
    assert r["seedSeconds"] == 0.5
    assert (r["deliveryLatencyMs"]["count"], r["deliveryLatencyMs"]["max"]) == (4, 1070)
    assert (r["messagesHandled"], r["missing"], r["duplicates"]) == (4, 0, 0)


def test_queue_efficiency_is_ideal_over_wall_clock_and_unset_for_an_instant_handler() -> None:
    handled = [QueueHandlerRecord(0, 0, 1000, 0), QueueHandlerRecord(1, 0, 1000, 0), QueueHandlerRecord(2, 1000, 2000, 0), QueueHandlerRecord(3, 1000, 2500, 0)]

    r = compute_queue_drain(queue(), handled, completed_ms=2500, seed_seconds=0)
    assert r["idealSeconds"] == pytest.approx(2)
    assert r["efficiency"] == pytest.approx(0.8)

    instant = compute_queue_drain(queue(settle_ms=0), [QueueHandlerRecord(0, 0, 1, 0)], completed_ms=1, seed_seconds=0)
    assert instant["idealSeconds"] is None
    assert instant["efficiency"] is None


def test_peak_concurrent_handlers_is_the_most_running_at_once() -> None:
    handled = [QueueHandlerRecord(0, 0, 1000, 0), QueueHandlerRecord(1, 100, 900, 0), QueueHandlerRecord(2, 200, 300, 0), QueueHandlerRecord(3, 1000, 1500, 0)]

    assert compute_queue_drain(queue(), handled, 1500, 0)["peakConcurrentHandlers"] == 3


def test_queue_missing_duplicates_and_order_violations_are_counted() -> None:
    r = compute_queue_drain(queue(messages=4), [QueueHandlerRecord(0, 0, 10, 0), QueueHandlerRecord(0, 20, 30, 0), QueueHandlerRecord(2, 40, 50, 0)], 50, 0)
    assert (r["messagesHandled"], r["duplicates"], r["missing"]) == (2, 1, 2)

    ordered = [QueueHandlerRecord(0, 0, 10, 0), QueueHandlerRecord(2, 10, 20, 0), QueueHandlerRecord(1, 20, 30, 0), QueueHandlerRecord(3, 30, 40, 0)]
    assert compute_queue_drain(queue(strict=True), ordered, 40, 0)["orderViolations"] == 1


def test_queue_timeline_counts_messages_finished_and_average_busy_handlers_per_second() -> None:
    handled = [QueueHandlerRecord(0, 0, 1000, 0), QueueHandlerRecord(1, 0, 500, 0), QueueHandlerRecord(2, 1000, 1500, 0)]

    r = compute_queue_drain(queue(messages=3), handled, completed_ms=1500, seed_seconds=0)

    assert r["messagesPerSecondTimeline"] == [1, 2]
    assert r["busyHandlersTimeline"] == [1.5, 0.5]
