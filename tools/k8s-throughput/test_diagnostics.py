"""Tests for diagnostics.py: python3 -m unittest discover -s tools/k8s-throughput -p 'test_*.py'"""
import unittest

import diagnostics


def metrics(coordinator=(0, 0), queue=(0, 0), extra=""):
    """A gateway's daprd /metrics: (sum ms, count) of its app's actor method calls, by actor type."""
    return f"""# HELP dapr_http_server_latency HTTP request end-to-end latency in server.
dapr_http_server_latency_sum{{app_id="daprmq-daprmq-gateway",method="PUT",path="/v1.0/actors/SessionCoordinatorActor/{{id}}/method",status="200"}} {coordinator[0]}
dapr_http_server_latency_count{{app_id="daprmq-daprmq-gateway",method="PUT",path="/v1.0/actors/SessionCoordinatorActor/{{id}}/method",status="200"}} {coordinator[1]}
dapr_http_server_latency_sum{{app_id="daprmq-daprmq-gateway",method="PUT",path="/v1.0/actors/daprmq-daprmq-QueueActor/{{id}}/method",status="200"}} {queue[0]}
dapr_http_server_latency_count{{app_id="daprmq-daprmq-gateway",method="PUT",path="/v1.0/actors/daprmq-daprmq-QueueActor/{{id}}/method",status="200"}} {queue[1]}
dapr_http_server_latency_sum{{app_id="daprmq-daprmq-gateway",method="GET",path="/v1.0/healthz",status="204"}} 5
dapr_http_server_latency_count{{app_id="daprmq-daprmq-gateway",method="GET",path="/v1.0/healthz",status="204"}} 500
{extra}"""


def cpu(periods, throttled, throttled_usec=0):
    return f"usage_usec 1000\nnr_periods {periods}\nnr_throttled {throttled}\nthrottled_usec {throttled_usec}\n"


class ActorCallLatencyTest(unittest.TestCase):
    def test_averages_only_the_calls_made_between_the_snapshots(self):
        before = {"gateways": {"g1": metrics(coordinator=(1000, 10), queue=(500, 100))}, "cgroups": {}}
        after = {"gateways": {"g1": metrics(coordinator=(3000, 20), queue=(2500, 300))}, "cgroups": {}}

        d = diagnostics.diff(before, after)

        self.assertEqual(d["actorCalls"], {"coordinator": 10, "queueActor": 200})
        self.assertEqual(d["actorCallLatencyMs"], {"coordinator": 200.0, "queueActor": 10.0})

    def test_sums_every_gateway_and_ignores_one_that_appeared_or_restarted(self):
        before = {"gateways": {"g1": metrics(coordinator=(100, 1)), "g2": metrics(coordinator=(900, 9))}, "cgroups": {}}
        after = {
            "gateways": {
                "g1": metrics(coordinator=(300, 2)),  # +200 ms over 1 call
                "g2": metrics(coordinator=(1500, 12)),  # +600 ms over 3 calls
                "g3": metrics(coordinator=(10_000, 1)),  # new pod: no baseline, so not counted
            },
            "cgroups": {},
        }

        d = diagnostics.diff(before, after)

        self.assertEqual(d["actorCalls"]["coordinator"], 4)
        self.assertEqual(d["actorCallLatencyMs"]["coordinator"], 200.0)

    def test_a_counter_that_went_backwards_is_a_restart_and_is_skipped(self):
        before = {"gateways": {"g1": metrics(coordinator=(5000, 50))}, "cgroups": {}}
        after = {"gateways": {"g1": metrics(coordinator=(100, 1))}, "cgroups": {}}

        d = diagnostics.diff(before, after)

        self.assertEqual(d["actorCalls"]["coordinator"], 0)
        self.assertIsNone(d["actorCallLatencyMs"]["coordinator"])


class CpuThrottlingTest(unittest.TestCase):
    def test_reports_the_share_of_periods_throttled_per_component(self):
        before = {"gateways": {}, "cgroups": {"worker/w1": cpu(100, 10), "worker/w2": cpu(100, 0), "gateway/g1": cpu(50, 0)}}
        after = {"gateways": {}, "cgroups": {"worker/w1": cpu(300, 60), "worker/w2": cpu(300, 0), "gateway/g1": cpu(150, 25)}}

        d = diagnostics.diff(before, after)

        # Workers: (50 + 0) throttled of (200 + 200) periods. Gateways: 25 of 100.
        self.assertEqual(d["cpuThrottledPercent"], {"worker": 12.5, "gateway": 25.0})

    def test_no_cfs_periods_means_no_cpu_limit_and_no_figure(self):
        before = {"gateways": {}, "cgroups": {"worker/w1": cpu(0, 0)}}
        after = {"gateways": {}, "cgroups": {"worker/w1": cpu(0, 0)}}

        self.assertEqual(diagnostics.diff(before, after)["cpuThrottledPercent"], {"worker": None})


if __name__ == "__main__":
    unittest.main()
