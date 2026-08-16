"""Check the session cache: a save should decrypt once, then load instantly."""
import json, os, time, urllib.parse, urllib.request

import testutil

BASE = "http://127.0.0.1:8751"
TARGET = testutil.pick_save(min_size=200000)   # a big story save: slowest to decrypt


def fetch(path):
    url = BASE + "/api/save?" + urllib.parse.urlencode({"path": path})
    with urllib.request.urlopen(url, timeout=120) as r:
        return json.loads(r.read())


print(f"target: {os.path.basename(TARGET)} ({os.path.getsize(TARGET)/1024:.0f} KB)\n")
for i in (1, 2, 3):
    t0 = time.time()
    j = fetch(TARGET)
    wall = (time.time() - t0) * 1000
    if "error" in j:
        print("error:", j["error"]); break
    print(f"  load {i}: server={j['ms']:>5} ms   round-trip={wall:>6.0f} ms   "
          f"cached={j['cached']}")

s = j["summary"]
print(f"\ndifficulty: {s['difficultyName']} ({s['difficulty']})")
print(f"saved at:   {s['savedAt']}")
print(f"money:      {s['money']}   units: {len(s['units'])}")
