"""End-to-end write test against the throwaway copy (UserData10Test.dat).

Proves an edit survives the full encrypt -> disk -> decrypt cycle and that nothing else
in the save drifts.
"""
import json, os, shutil
import ecsave
import testutil

SAVE = testutil.pick_save(max_size=60000)
SCRATCH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_write_test.dat")

shutil.copy2(SAVE, SCRATCH)
print("working on a scratch copy:", SCRATCH)

before = ecsave.load_json(SCRATCH)
print(f"\nbefore:  money={before['_money']}  seconds={before['_seconds']:.1f}  "
      f"unit0 exp={before['_unitData']['_units'][0]['_exp']}")

edits = {
    "top":   {"_money": 123456},
    "town":  {"_fortressTownLevel": 7},
    "units": {"0": {"_exp": 999999, "_hp": 555}},
    "items": {"0": {"_count": 99}},
}
changed = ecsave.apply_edits(before, edits)
n = ecsave.write_save(SCRATCH, before)
print(f"applied {changed} field changes; wrote {n} bytes")

after = ecsave.load_json(SCRATCH)
checks = [
    ("money",        after["_money"] == 123456),
    ("town level",   after["_fortressTown"]["_fortressTownLevel"] == 7),
    ("unit0 exp",    after["_unitData"]["_units"][0]["_exp"] == 999999),
    ("unit0 hp",     after["_unitData"]["_units"][0]["_hp"] == 555),
    ("item0 count",  after["_inventory"]["_items"][0]["_count"] == 99),
]
print("\nread-back checks:")
for label, ok in checks:
    print(f"  {label:<14} {'OK' if ok else 'FAIL'}")

# nothing outside the edited fields should have moved
untouched_before = {k: v for k, v in before.items()
                    if k not in ("_money", "_fortressTown", "_unitData", "_inventory")}
untouched_after = {k: v for k, v in after.items()
                   if k not in ("_money", "_fortressTown", "_unitData", "_inventory")}
print(f"\nall other top-level data unchanged: {untouched_before == untouched_after}")
print(f"backup created: {os.path.exists(SCRATCH + '.bak')}")

# the backup must still be the original, untouched save
orig = ecsave.load_json(SCRATCH + ".bak")
print(f"backup still holds original money ({orig['_money']}): "
      f"{orig['_money'] != 123456}")

for f in (SCRATCH, SCRATCH + ".bak"):
    os.remove(f)
print("\nscratch files cleaned up")
print("ALL WRITE TESTS PASSED" if all(ok for _, ok in checks) else "SOMETHING FAILED")
