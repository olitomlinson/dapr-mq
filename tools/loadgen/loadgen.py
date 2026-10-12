#!/usr/bin/env python3
"""
Steady, varied traffic against a DaprMQ gateway, so the admin console has something to show.

Per queue, a producer enqueues at a rate that swings on a slow wave (so depth rises and falls), and
competing consumers lock-dequeue, "work", then mostly acknowledge. A few items are poison: consumers
always nack those, so after MaxDeliveryCount (10) they land in the queue's DLQ. Some locks are
abandoned to expire. A publisher feeds a topic with subscribers.

Standard library only. Configured by environment variables (see DEFAULTS). Stop with Ctrl+C / SIGTERM.
"""
import json
import math
import os
import random
import signal
import threading
import time
import urllib.error
import urllib.request

DEFAULTS = {
    "GATEWAY_URL": "http://localhost:8002",
    "QUEUES": "orders,emails,payments",
    "PRODUCE_RATE": "20",          # items/s per queue, average
    "PRODUCE_WAVE": "0.6",         # rate swings +/- this fraction of PRODUCE_RATE
    "WAVE_PERIOD_SECONDS": "300",  # one full swing
    "CONSUMERS": "2",              # consumer threads per queue
    "CONSUME_BATCH": "10",         # items per locked dequeue
    "WORK_MS": "400",              # simulated work per batch
    "LOCK_TTL_SECONDS": "30",
    "PRIORITY0_PERCENT": "10",     # fast lane
    "POISON_PERCENT": "1",         # always nacked -> dead-lettered after MaxDeliveryCount
    "ABANDON_PERCENT": "1",        # lock left to expire, redelivered later
    "TOPIC": "news",               # "" to skip
    "TOPIC_SUBSCRIBERS": "audit,search",
    "PUBLISH_RATE": "2",           # items/s
    "PAYLOAD_BYTES": "200",
    "DURATION_SECONDS": "0",       # 0 = until stopped
}


def setting(name, cast=str):
    return cast(os.environ.get(name, DEFAULTS[name]))


GATEWAY = setting("GATEWAY_URL").rstrip("/")
stop = threading.Event()
stats_lock = threading.Lock()
stats = {"enqueued": 0, "dequeued": 0, "acked": 0, "nacked": 0, "abandoned": 0, "published": 0, "errors": 0}


def count(key, n=1):
    with stats_lock:
        stats[key] += n


def post(path, body=None, headers=None):
    """POST JSON; returns (status, parsed body or None). Network errors count and return (0, None)."""
    data = json.dumps(body).encode() if body is not None else b""
    request = urllib.request.Request(GATEWAY + path, data=data, method="POST",
                                     headers={"content-type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as error:
        if error.code >= 500:
            count("errors")
        return error.code, None
    except (urllib.error.URLError, TimeoutError, ConnectionError):
        count("errors")
        return 0, None


def payload(queue, n):
    filler = "x" * max(0, setting("PAYLOAD_BYTES", int) - 60)
    return {"queue": queue, "n": n, "at": time.time(), "pad": filler}


def producer(queue):
    rate, wave, period = setting("PRODUCE_RATE", float), setting("PRODUCE_WAVE", float), setting("WAVE_PERIOD_SECONDS", float)
    p0, poison = setting("PRIORITY0_PERCENT", float), setting("POISON_PERCENT", float)
    phase = random.random() * period  # queues out of step with each other
    n, owed = 0, 0.0
    while not stop.is_set():
        current = max(0.0, rate * (1 + wave * math.sin(2 * math.pi * (time.time() + phase) / period)))
        owed += current
        batch = int(owed)
        owed -= batch
        if batch:
            items = []
            for _ in range(batch):
                n += 1
                item = payload(queue, n)
                if random.uniform(0, 100) < poison:
                    item["poison"] = True
                items.append({"item": item, "priority": 0 if random.uniform(0, 100) < p0 else 1})
            status, _ = post(f"/queue/{queue}/enqueue", {"items": items})
            if status == 200:
                count("enqueued", batch)
        stop.wait(1)


def consumer(queue):
    batch, work, ttl = setting("CONSUME_BATCH", int), setting("WORK_MS", int) / 1000, setting("LOCK_TTL_SECONDS", int)
    abandon = setting("ABANDON_PERCENT", float)
    while not stop.is_set():
        status, body = post(f"/queue/{queue}/dequeue", headers={
            "require-ack": "true", "ttl-seconds": str(ttl), "count": str(batch), "allow-competing-consumers": "true"})
        items = (body or {}).get("items") or [] if status == 200 else []
        if not items:
            stop.wait(1)  # empty (204) or locked (423): back off
            continue
        count("dequeued", len(items))
        stop.wait(work)

        acks = []
        for entry in items:
            item = entry.get("item") or {}
            if isinstance(item, dict) and item.get("poison"):
                if post(f"/queue/{queue}/nack", {"lockId": entry["lockId"]})[0] == 200:
                    count("nacked")
            elif random.uniform(0, 100) < abandon:
                count("abandoned")  # never settled: the lock expires and the item comes back
            else:
                acks.append(entry["lockId"])
        if acks and post(f"/queue/{queue}/acknowledge-batch", {"lockIds": acks})[0] == 200:
            count("acked", len(acks))


def publisher(topic):
    rate = setting("PUBLISH_RATE", float)
    for subscriber in filter(None, setting("TOPIC_SUBSCRIBERS").split(",")):
        post(f"/topic/{topic}/subscribers/{subscriber.strip()}")  # 201, or 409 if already subscribed
    n, owed = 0, 0.0
    while not stop.is_set():
        owed += rate
        batch = int(owed)
        owed -= batch
        if batch:
            items = [{"item": {"headline": f"story {n + i}", "at": time.time()}} for i in range(batch)]
            n += batch
            if post(f"/topic/{topic}/publish", {"items": items})[0] in (200, 202):
                count("published", batch)
        stop.wait(1)


def report():
    previous = dict(stats)
    while not stop.wait(10):
        with stats_lock:
            current = dict(stats)
        delta = {k: (current[k] - previous[k]) / 10 for k in current}
        previous = current
        print("per second: " + "  ".join(f"{k} {v:.1f}" for k, v in delta.items()), flush=True)


def main():
    signal.signal(signal.SIGTERM, lambda *_: stop.set())
    queues = [q.strip() for q in setting("QUEUES").split(",") if q.strip()]
    print(f"loadgen -> {GATEWAY}: queues {queues}, topic {setting('TOPIC') or '-'}", flush=True)

    while not stop.is_set():  # wait for the gateway (compose starts everything at once)
        try:
            with urllib.request.urlopen(GATEWAY + "/health/ready", timeout=5) as response:
                if response.status == 200:
                    break
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            pass
        print("waiting for the gateway to be ready...", flush=True)
        stop.wait(3)

    threads = [threading.Thread(target=producer, args=(q,), daemon=True) for q in queues]
    threads += [threading.Thread(target=consumer, args=(q,), daemon=True)
                for q in queues for _ in range(setting("CONSUMERS", int))]
    if setting("TOPIC"):
        threads.append(threading.Thread(target=publisher, args=(setting("TOPIC"),), daemon=True))
    threads.append(threading.Thread(target=report, daemon=True))
    for thread in threads:
        thread.start()

    duration = setting("DURATION_SECONDS", float)
    try:
        stop.wait(duration if duration > 0 else None)
    except KeyboardInterrupt:
        pass
    stop.set()
    print(f"stopped: {stats}", flush=True)


if __name__ == "__main__":
    main()
