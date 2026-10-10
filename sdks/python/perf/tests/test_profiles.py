import json
from pathlib import Path

import pytest

from perf.options import parse
from perf.profiles import LOAD_PROFILES, PROFILES, QUEUE_DRAIN_PROFILES, SESSION_DRAIN_PROFILES, SUITES, QueueDrainParams, SessionDrainParams, scale_of

SHARED = json.loads((Path(__file__).resolve().parents[4] / "sdks" / "testing" / "perf" / "profiles.json").read_text())


def scenario_of(profile: str) -> tuple[str, str, str]:
    if profile in LOAD_PROFILES:
        load = LOAD_PROFILES[profile]
        return load.id, load.scenario, load.key
    if profile in QUEUE_DRAIN_PROFILES:
        return "P-05", "queue-drain", QUEUE_DRAIN_PROFILES[profile].key
    return "P-04", "session-drain", SESSION_DRAIN_PROFILES[profile].key


@pytest.mark.parametrize("profile", sorted(SHARED["profiles"]))
def test_every_profile_matches_the_shared_file(profile: str) -> None:
    expected = SHARED["profiles"][profile]

    assert scenario_of(profile) == (expected["id"], expected["name"], expected["key"])
    assert scale_of(profile) == expected["scale"]


def test_profiles_and_suites_are_exactly_the_shared_ones() -> None:
    assert sorted(PROFILES) == sorted(SHARED["profiles"])
    assert SUITES == SHARED["suites"]


def test_session_ideal_rounds_sessions_up_to_whole_slot_rounds() -> None:
    assert SessionDrainParams(sessions=1000, messages_per_session=100, max_concurrent_sessions=20).ideal_seconds == 5000
    assert SessionDrainParams(sessions=21, messages_per_session=10, settle_ms=500, max_concurrent_sessions=20).ideal_seconds == 10


def test_session_ideal_in_concurrent_mode_is_bounded_by_the_slower_of_consuming_and_publishing() -> None:
    publish_bound = SessionDrainParams(4, 10, 1000, 4, publish_mode="concurrent", publish_interval_ms=2000, publish_jitter_ms=500)
    assert publish_bound.ideal_seconds == pytest.approx(23.5)
    assert SessionDrainParams(4, 10, 1000, 4, publish_interval_ms=2000, publish_jitter_ms=500).ideal_seconds == 10


@pytest.mark.parametrize(("handlers", "active", "strict", "expected"), [(0, 100, False, 100), (10, 100, False, 10), (500, 100, False, 100), (10, 100, True, 1)])
def test_queue_concurrency_is_what_bounds_handlers_running_at_once(handlers: int, active: int, strict: bool, expected: int) -> None:
    assert QueueDrainParams(4, 1000, active, handlers, strict).concurrency == expected


def test_queue_ideal_covers_a_slow_tail_and_a_rate_limited_publisher() -> None:
    assert QueueDrainParams(20, 100, 100, 0, tail_every=10, tail_ms=5000).ideal_seconds == pytest.approx(5)
    assert QueueDrainParams(50, 100, 100, 0, publish_interval_ms=200).ideal_seconds == pytest.approx(9.9)
    assert QueueDrainParams(4, 0, 100, 0).ideal_seconds is None


def test_suite_runs_its_profiles_and_extreme_defaults_to_three_replicas() -> None:
    pr = parse(["--suite", "pr", "--env-label", "ci-x"])
    assert pr.profiles == SUITES["pr"]
    assert pr.api_replicas == 1
    assert pr.scale("enqueue") == "pr"

    extreme = parse(["--suite", "extreme"])
    assert extreme.api_replicas == 3
    assert parse(["--suite", "extreme", "--api-replicas", "5"]).api_replicas == 5


def test_a_lone_profile_records_its_own_scale() -> None:
    options = parse(["--profile", "enqueue-ramp"])
    assert options.profiles == ["enqueue-ramp"]
    assert options.scale("enqueue-ramp") == "extreme"


def test_existing_server_needs_both_endpoints_and_takes_a_grpc_url() -> None:
    with pytest.raises(SystemExit):
        parse(["--http", "http://localhost:8002"])
    with pytest.raises(SystemExit):
        parse(["--http", "http://h", "--grpc", "g:1", "--api-replicas", "2"])

    assert parse(["--http", "http://localhost:8002", "--grpc", "http://localhost:8102"]).grpc == "localhost:8102"
