# One KEDA ScaledJob run: dequeue one item, work for WORK_SECONDS while heartbeating ExtendLock every
# TTL/2, acknowledge, exit 0. An empty dequeue exits 0 too. Logs START/extend/ACK lines run.sh asserts on.
import json, os, sys, threading, time, urllib.request

BASE = os.environ["GATEWAY_URL"].rstrip("/") + "/queue/" + os.environ["QUEUE_ID"]
TTL = int(os.environ.get("TTL", "10"))
WORK = int(os.environ.get("WORK_SECONDS", "30"))


def log(msg):
    print(f"{time.strftime('%X')} {msg}", flush=True)


def post(path, body=None, headers=None):
    req = urllib.request.Request(BASE + path, data=json.dumps(body or {}).encode(), method="POST",
                                 headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")


status, res = post("/dequeue", headers={"require-ack": "true", "ttl-seconds": str(TTL),
                                        "allow-competing-consumers": "true", "count": "1"})
if status == 204 or not isinstance(res, dict) or not res.get("items"):
    log(f"EMPTY status={status}")
    sys.exit(0)

item = res["items"][0]
n, lock = item["item"]["n"], item["lockId"]
log(f"START n={n} lock={lock}")

done = threading.Event()


def heartbeat():
    while not done.wait(TTL / 2):
        s, _ = post("/extend-lock", {"lockId": lock, "additionalTtlSeconds": TTL})
        log(f"EXTEND n={n} status={s}")


threading.Thread(target=heartbeat, daemon=True).start()
time.sleep(WORK)
done.set()
s, _ = post("/acknowledge", {"lockId": lock})
log(f"ACK n={n} status={s}")
sys.exit(0 if s == 200 else 1)
