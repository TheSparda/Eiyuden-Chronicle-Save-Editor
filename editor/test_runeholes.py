"""Unlocking rune holes.

Invariants taken from 353 real holes across the local saves:
  * a locked hole (_released false) never holds a rune -- 0 occurrences;
  * (_released, _isViewedReleaseEffect) is seen as (True, True) and (True, False),
    never (False, True).
So unlocking sets both true, and re-locking must clear the hole.
"""
import json, os, shutil
import ecsave
import testutil

SRC = testutil.pick_save(max_size=60000)      # early save: plenty of locked holes
SCRATCH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_holes_test.dat")

shutil.copy2(SRC, SCRATCH)
obj = ecsave.load_json(SCRATCH)

u = obj["_unitData"]["_units"][0]
name = ecsave.unit_name(u["_id"])
before = [(h["_released"], h["_itemId"]) for h in u["_runeHoles"]]
print(f"{name}: {len(before)} holes, released={[r for r, _ in before]}")

locked = [i for i, (rel, _) in enumerate(before) if not rel]
print(f"locked slots: {locked}")

# --- unlock two, and put a rune in one of them in the same write ------------------
target, other = locked[0], locked[1]
RUNE = 7000        # Rune of Fire
ecsave.apply_edits(obj, {"units": {"0": {
    "_runeHoleReleased": {str(target): True, str(other): True},
    "_runeHoles": [None] * target + [RUNE],
}}})
ecsave.write_save(SCRATCH, obj)

after = ecsave.load_json(SCRATCH)["_unitData"]["_units"][0]["_runeHoles"]
print(f"\nafter unlocking slots {target} and {other}, filling {target}:")
for i in (target, other):
    h = after[i]
    print(f"  slot {i}: released={h['_released']} viewed={h['_isViewedReleaseEffect']} "
          f"rune={h['_itemId']} ({ecsave.item_name(h['_itemId']) or 'empty'})")

ok_unlock = after[target]["_released"] and after[other]["_released"]
ok_rune = after[target]["_itemId"] == RUNE
print(f"\n  both unlocked: {ok_unlock}")
print(f"  rune placed in the newly unlocked slot: {ok_rune}")

# --- a still-locked hole must refuse a rune ---------------------------------------
obj2 = ecsave.load_json(SCRATCH)
still_locked = [i for i, h in enumerate(obj2["_unitData"]["_units"][0]["_runeHoles"])
                if not h["_released"]]
if still_locked:
    s = still_locked[0]
    ecsave.apply_edits(obj2, {"units": {"0": {"_runeHoles": [None] * s + [RUNE]}}})
    got = obj2["_unitData"]["_units"][0]["_runeHoles"][s]["_itemId"]
    print(f"  locked slot {s} refuses a rune: {got == 0}")

# --- re-locking clears the hole ----------------------------------------------------
ecsave.apply_edits(obj2, {"units": {"0": {"_runeHoleReleased": {str(target): False}}}})
h = obj2["_unitData"]["_units"][0]["_runeHoles"][target]
print(f"  re-locking clears the rune: {h['_itemId'] == 0 and not h['_released']}")

# --- nothing else moved -------------------------------------------------------------
orig = ecsave.load_json(SRC)
final = ecsave.load_json(SCRATCH)
diffs = [k for k in orig if k != "_unitData" and orig[k] != final.get(k)]
print(f"  other top-level keys changed: {diffs or 'none'}")

other_units_same = all(
    orig["_unitData"]["_units"][i] == final["_unitData"]["_units"][i]
    for i in range(1, len(orig["_unitData"]["_units"])))
print(f"  other characters untouched: {other_units_same}")

for f in (SCRATCH, SCRATCH + ".bak"):
    if os.path.exists(f):
        os.remove(f)
print("\nscratch cleaned up")
