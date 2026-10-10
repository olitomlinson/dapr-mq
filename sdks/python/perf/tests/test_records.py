"""Port of RunRecordsTests (.NET): the record shape the shared report reads."""

import json
from datetime import datetime, timezone

from perf.metrics import HandlerRecord, compute_session_drain
from perf.profiles import LOAD_PROFILES, QUEUE_DRAIN_PROFILES, SESSION_DRAIN_PROFILES
from perf.records import LoadResult, RunContext, RunEnvironment, load_record, queue_drain_record, save, session_drain_record

CONTEXT = RunContext(
    datetime(2026, 10, 5, 10, 0, 0, tzinfo=timezone.utc),
    RunEnvironment("ci", "abc123", "main", False, "Linux", 4, "CPython 3.12.4", "testcontainers daprmq-api:test"),
    scale="pr",
    api_replicas=2,
    scheduler_replicas=3,
    sdk_version="0.1.0",
)


def step(concurrency: int, messages_per_second: float, errors: int = 0) -> dict:
    return {
        "concurrency": concurrency, "queues": concurrency, "durationSeconds": 30, "ops": 100,
        "messages": int(messages_per_second * 30), "errors": errors, "opsPerSecond": messages_per_second,
        "messagesPerSecond": messages_per_second,
        "latencyMs": {"count": 100, "mean": 2, "p50": 1, "p95": 3, "p99": 4, "max": 5},
    }  # fmt: skip


def timeline(concurrency: int, *ops: int) -> dict:
    values = list(ops)
    return {
        "opsPerSecond": values, "messagesPerSecond": values, "latencyP50Ms": values, "latencyP95Ms": values,
        "latencyP99Ms": values, "errors": [0 for _ in values], "concurrency": [concurrency for _ in values],
    }  # fmt: skip


def test_load_records_identity_topology_and_scenario() -> None:
    run = load_record(CONTEXT, "enqueue", LOAD_PROFILES["enqueue"], LoadResult([step(8, 500)], [timeline(8, 500, 500)], False, []))

    assert run["schemaVersion"] == 2
    assert run["runId"] == "20261005T100000Z_python_ci_enqueue"
    assert run["sdk"] == {"name": "python", "version": "0.1.0", "runtime": "CPython 3.12.4"}
    assert run["topology"] == {"apiReplicas": 2, "schedulerReplicas": 3, "loadBalancer": True, "server": "testcontainers daprmq-api:test"}
    assert run["scale"] == "pr"
    assert run["scenario"]["id"] == "P-01"
    assert run["scenario"]["key"] == "enqueue:c8/q8/b1/256B/3+15s"
    assert run["scenario"]["params"]["queues"] == 8
    assert run["checks"] == {"passed": True, "failures": []}
    assert datetime.fromisoformat(run["timestampUtc"]) == CONTEXT.timestamp


def test_a_ramp_headlines_its_best_step_and_concatenates_the_timelines() -> None:
    run = load_record(
        CONTEXT, "enqueue-ramp", LOAD_PROFILES["enqueue-ramp"],
        LoadResult([step(1, 100), step(2, 300), step(4, 250)], [timeline(1, 1), timeline(2, 2), timeline(4, 3, 3)], False, []),
    )  # fmt: skip

    assert run["metrics"]["messagesPerSecond"] == 300
    assert len(run["steps"]) == 3
    assert run["timeline"]["series"]["opsPerSecond"] == [1, 2, 3, 3]
    assert run["timeline"]["series"]["concurrency"] == [1, 2, 4, 4]
    assert run["scenario"]["params"]["queues"] is None


def test_load_fails_its_checks_on_errors_or_a_drained_queue() -> None:
    run = load_record(CONTEXT, "dequeue-ack", LOAD_PROFILES["dequeue-ack"], LoadResult([step(8, 100, errors=2)], [timeline(8, 1)], True, ["TimeoutError: boom"]))

    assert run["checks"] == {
        "passed": False,
        "failures": ["2 errors (first: TimeoutError: boom)", "a queue drained before the window ended: raise seedPerQueue"],
    }


def test_session_drain_moves_the_busy_slots_timeline_out_of_the_metrics() -> None:
    scenario = SESSION_DRAIN_PROFILES["steady-drain"]
    result = compute_session_drain(scenario, [], [HandlerRecord("s0", 0, 0, 1500)], 2000, 1)
    result["missing"] = 0

    run = session_drain_record(CONTEXT, "steady-drain", scenario, result)

    assert (run["scenario"]["id"], run["scenario"]["name"], run["scenario"]["key"]) == ("P-04", "session-drain", "200x20@100ms/20slots+idle1s")
    assert run["scenario"]["params"]["sessionIdleTimeoutSeconds"] == 1
    assert "busySlotsTimeline" not in run["metrics"]
    assert "timelineBucketMs" not in run["metrics"]
    assert run["timeline"] == {"bucketMs": 1000, "series": {"busySlots": [1.0, 0.5]}}
    assert run["checks"]["passed"] is True


def queue_result(missing: int = 0, order_violations: int = 0) -> dict:
    return {
        "seedSeconds": 0.4, "wallClockSeconds": 5, "messagesPerSecond": 800, "idealSeconds": 0.4, "efficiency": 0.08,
        "timeToFirstMessageSeconds": 0.02, "peakConcurrentHandlers": 100,
        "deliveryLatencyMs": {"count": 4000, "mean": 2000, "p50": 2000, "p95": 4000, "p99": 4500, "max": 5000},
        "messagesHandled": 4000 - missing, "duplicates": 0, "missing": missing, "orderViolations": order_violations,
        "messagesPerSecondTimeline": [700, 900], "busyHandlersTimeline": [95.5, 99],
    }  # fmt: skip


def test_queue_drain_records_the_scenario_and_moves_the_timelines_out_of_the_metrics() -> None:
    run = queue_drain_record(CONTEXT, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queue_result())

    assert (run["scenario"]["id"], run["scenario"]["name"], run["scenario"]["key"]) == ("P-05", "queue-drain", "queue:4000@10ms/active100")
    assert run["scenario"]["params"]["messages"] == 4000
    assert "key" not in run["scenario"]["params"]
    assert run["metrics"]["deliveryLatencyMs"]["p95"] == 4000
    assert "messagesPerSecondTimeline" not in run["metrics"]
    assert run["timeline"]["series"] == {"messagesPerSecond": [700, 900], "busyHandlers": [95.5, 99]}


def test_queue_drain_fails_on_missing_messages_or_out_of_order_only_under_strict_order() -> None:
    missing = queue_drain_record(CONTEXT, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queue_result(missing=3))
    assert missing["checks"]["failures"] == ["3 messages missing"]

    strict = queue_drain_record(CONTEXT, "queue-strict-order", QUEUE_DRAIN_PROFILES["queue-strict-order"], queue_result(order_violations=2))
    assert strict["checks"]["failures"] == ["2 order violations"]

    unordered = queue_drain_record(CONTEXT, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queue_result(order_violations=2))
    assert unordered["checks"]["passed"] is True


def test_save_writes_the_run_in_full_and_history_without_the_timeline(tmp_path) -> None:
    run = queue_drain_record(CONTEXT, "queue-drain", QUEUE_DRAIN_PROFILES["queue-drain"], queue_result())

    path = save(tmp_path, run)

    assert path == tmp_path / "sdk-python" / "runs" / "20261005T100000Z_python_ci_queue-drain.json"
    assert json.loads(path.read_text())["timeline"]["bucketMs"] == 1000
    history = [json.loads(line) for line in (tmp_path / "sdk-python" / "history.jsonl").read_text().splitlines()]
    assert len(history) == 1
    assert "timeline" not in history[0]
