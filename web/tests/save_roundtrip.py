"""Engine round-trip against a synthetic save built from the engine's own constants.

Ships no game data: it manufactures a valid encrypted save with the real ecsave/pydes
crypto, then drives the *exact* JS-embedded glue (extracted from web/app.js) through
decode -> summarize -> apply_edits -> write_save -> re-decode, asserting edits persist,
the file re-decrypts, and JSON string-keyed indices coerce correctly.

Run: python3 tests/save_roundtrip.py   (from web/)
"""
import os, re, sys, json, tempfile

TESTS = os.path.dirname(os.path.abspath(__file__))
WEB = os.path.dirname(TESTS)
REPO = os.path.dirname(WEB)
EDITOR = os.path.join(REPO, "editor")
sys.path.insert(0, EDITOR)
os.chdir(EDITOR)                      # so ecsave finds its ec_*.json next to __file__

import ecsave

# --- extract the GLUE python string verbatim from web/app.js -------------------
appjs = open(os.path.join(WEB, "app.js"), encoding="utf-8").read()
m = re.search(r"const GLUE = `(.*?)`;", appjs, re.S)
assert m, "could not find GLUE template in app.js"
glue = {}
exec(compile(m.group(1), "GLUE", "exec"), glue)

fails = []
def check(cond, msg):
    print(("  ok  " if cond else " FAIL ") + msg)
    if not cond:
        fails.append(msg)

# --- build a structurally-valid synthetic save --------------------------------
uid = sorted(ecsave.UNIT_NAMES)[0] if ecsave.UNIT_NAMES else 1
obj = {
    "_money": 1000, "_seconds": 3600.0, "_personalUnitId": uid, "_lapPlayCount": 0,
    "_versionCode": 1, "_appVersionCode": 1, "_mainScenaionCleared": False, "_datetime": None,
    "_difficultyData": {"_difficulty": 1},
    "_fortressTown": {"_fortressTownLevel": 1, "_population": 10},
    "_unitData": {"_units": [{
        "_id": uid, "_exp": 100, "_hp": 50, "_mp": 20, "_weaponLevel": 3,
        "_equipment": [0, 0, 0, 0],
        "_runeHoles": [{"_itemId": 0, "_released": True, "_isViewedReleaseEffect": True}],
    }]},
    "_inventory": {"_items": [{"_id": 1001, "_count": 5, "_max": 99}]},
}
for k, _ in ecsave.DIFFICULTY_FLAGS:
    obj["_difficultyData"][k] = False

path = os.path.join(tempfile.mkdtemp(), "UserDataTest.dat")
size = ecsave.write_save(path, obj, make_backup=False)
check(size > 0, f"synthetic save written ({size} bytes)")

# --- glue endpoints ------------------------------------------------------------
res = json.loads(glue["api_save"](path))
check("error" not in res, "api_save decodes without error")
check(res["summary"]["money"] == 1000, "decoded money == 1000")

saves = json.loads(glue["api_saves"](json.dumps([{"path": path, "name": "UserDataTest.dat"}])))
check(bool(saves["dirs"]) and saves["dirs"][0]["saves"][0]["name"] == "UserDataTest.dat",
      "api_saves lists the file")
check(saves["cloud"]["available"] is False, "cloud degrades gracefully")

edits = {
    "top": {"_money": 424242},
    "difficulty": {"_difficulty": 0},
    "town": {"_fortressTownLevel": 7},
    "units": {"0": {"_hp": 999, "_equipment": [6011, None, None, None]}},   # STRING index key
    "items": {"0": {"_count": 77}},
    "addItems": [{"_id": 1001, "_count": 3, "_max": 99}],
}
wr = json.loads(glue["api_write"](path, json.dumps(edits)))
check(wr.get("ok") is True and wr["changed"] >= 5, f"api_write applied edits ({wr.get('changed')})")

after = ecsave.load_json(path)     # fresh decode straight off disk
check(after["_money"] == 424242, "money persisted")
check(after["_difficultyData"]["_difficulty"] == 0, "difficulty persisted")
check(after["_unitData"]["_units"][0]["_hp"] == 999, "unit hp persisted")
check(after["_unitData"]["_units"][0]["_equipment"][0] == 6011, "equipment persisted")
check(after["_inventory"]["_items"][0]["_count"] == 77, "item count persisted")
check(len(after["_inventory"]["_items"]) == 2, "addItems appended a stack")

# v2: unit reference glue (Reference tab + inline notes)
uref = json.loads(glue["api_unitref"]())
check(isinstance(uref, list) and len(uref) > 0, f"api_unitref returns units ({len(uref)})")
sample_unit = next((u for u in uref if u.get("samples")), None)
check(sample_unit is not None and len(sample_unit["samples"][0]) == 4,
      "unit ref carries (exp,hp,mp,wl) stat samples")
check(all(("id" in u and "runeHoles" in u and "role" in u) for u in uref),
      "unit ref rows have id/role/runeHoles")

# unrecognized input is rejected
bad = os.path.join(os.path.dirname(path), "garbage.dat")
open(bad, "wb").write(b"not a real save")
err = json.loads(glue["api_save"](bad))
check("error" in err, "garbage file rejected")

print("\nbackend:", __import__("pydes").backend())
print("save_roundtrip: " + ("all checks passed" if not fails else f"{len(fails)} FAILED"))
sys.exit(1 if fails else 0)
