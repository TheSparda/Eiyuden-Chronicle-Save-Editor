"""Recruitment: roster view, adding a character, removing one, and the guards.

Recruitment is presence in `_unitData._units` -- an early save carries 6 records, a
finished one 120. Adding a record is treated as experimental (see ecsave's notes); these
tests check that what we write is *shaped* exactly like what the game writes, and that
nothing load-bearing can be removed by accident.
"""
import json, os, shutil
import ecsave
import testutil

SRC = testutil.pick_save(max_size=60000)          # small, early-game save
SCRATCH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_roster_test.dat")

shutil.copy2(SRC, SCRATCH)
obj = ecsave.load_json(SCRATCH)

r = ecsave.roster(obj)
have = [x for x in r if x["recruited"]]
missing = [x for x in r if not x["recruited"]]
print(f"roster: {len(r)} known characters, {len(have)} recruited, {len(missing)} missing")
print("  recruited:", ", ".join(x["name"] for x in have[:8]))
print("  missing:  ", ", ".join(x["name"] for x in missing[:8]), "...")

prot = [x["name"] for x in r if x["protected"]]
print(f"\nprotected from removal ({len(prot)}): {', '.join(prot)}")

# --- recruit someone new ---------------------------------------------------------
target = missing[0]
print(f"\nrecruiting {target['name']} (id {target['id']})…")
ecsave.apply_edits(obj, {"recruit": {str(target["id"]): True}})
ecsave.write_save(SCRATCH, obj)

after = ecsave.load_json(SCRATCH)
rec = ecsave.recruited_ids(after)
print("  now recruited:", target["id"] in rec, f"({len(rec)} total)")

# the new record must have exactly the same keys as the game's own records
units = after["_unitData"]["_units"]
shapes = {tuple(sorted(u.keys())) for u in units}
print("  distinct key-shapes across all records:", len(shapes), "(1 = new record matches)")

new = next(u for u in units if u["_id"] == target["id"])
print("  new record:", json.dumps({k: new[k] for k in
      ("_id", "_exp", "_hp", "_mp", "_weaponLevel", "_equipment")}))
print(f"  rune holes: {len(new['_runeHoles'])}, released="
      f"{[h['_released'] for h in new['_runeHoles']]}")

# Cross-check the reconstructed record against a save where this character really is
# recruited -- the hole count is master data, so it must match exactly.
full = None
for cand in ecsave.list_saves(testutil.save_dir()):
    o = ecsave.load_json(cand["path"])
    match = next((u for u in o["_unitData"]["_units"]
                  if u.get("_id") == target["id"]), None)
    if match:
        full = (cand["name"], match)
        break
if full:
    name, real = full
    print(f"  ground truth from {name}: {len(real['_runeHoles'])} holes")
    print("  hole count matches the game's own record:",
          len(real["_runeHoles"]) == len(new["_runeHoles"]))
    print("  same keys as the game's record:",
          sorted(real.keys()) == sorted(new.keys()))
else:
    print("  (no save found where this character is recruited -- no cross-check)")

# --- it survives a decrypt round-trip and shows in the summary --------------------
s = ecsave.summarize(after)
print(f"\n  summary sees {s['recruitedCount']}/{s['knownCount']} recruited")
print("  appears in unit list:",
      any(u["id"] == target["id"] for u in s["units"]))

# --- remove them again ------------------------------------------------------------
ecsave.apply_edits(after, {"recruit": {str(target["id"]): False}})
ecsave.write_save(SCRATCH, after)
back = ecsave.load_json(SCRATCH)
print("\n  removed again:", target["id"] not in ecsave.recruited_ids(back))
print("  roster back to original size:",
      len(ecsave.recruited_ids(back)) == len(have))

# --- guards ------------------------------------------------------------------------
hero = back.get("_personalUnitId")
try:
    ecsave.apply_edits(back, {"recruit": {str(hero): False}})
    print(f"\n  removing the player character ({ecsave.unit_name(hero)}): NOT BLOCKED (bad)")
except ValueError as e:
    print(f"\n  removing the player character is blocked: {e}")

# everything else must be untouched
orig = ecsave.load_json(SRC)
diffs = [k for k in orig if k != "_unitData" and orig[k] != back.get(k)]
print("  other top-level keys changed:", diffs or "none")

for f in (SCRATCH, SCRATCH + ".bak"):
    if os.path.exists(f):
        os.remove(f)
print("\nscratch cleaned up")
