"""Test inventory add/remove and named equipment/runes."""
import json, os, shutil
import ecsave
import testutil

SRC = testutil.pick_save(max_size=60000)
SCRATCH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_item_test.dat")

print(f"name table: {len(ecsave.ITEM_NAMES)} items, "
      f"{len(ecsave.ITEM_MAXES)} observed stack maxima\n")

shutil.copy2(SRC, SCRATCH)
obj = ecsave.load_json(SCRATCH)
before = len(obj["_inventory"]["_items"])
print(f"inventory before: {before} stacks")
for it in ecsave.summarize(obj)["items"][:3]:
    print(f"   {it['id']:<7} x{it['count']:<4} {it['name']}")

# add: one within a stack, one that must split across stacks
edits = {
    "addItems": [
        {"_id": 1004, "_count": 2},     # Revival Medicine, observed max 2 -> 1 stack
        {"_id": 1000, "_count": 20},    # Healing Herb, observed max 6 -> 4 stacks
    ],
    "removeItems": [0],
}
changed = ecsave.apply_edits(obj, edits)
ecsave.write_save(SCRATCH, obj)

after = ecsave.load_json(SCRATCH)
items = after["_inventory"]["_items"]
print(f"\napplied {changed} changes; inventory now {len(items)} stacks")

added = [i for i in items if i["_id"] in (1000, 1004)]
print("\nstacks for the added ids:")
for a in added:
    print(f"   {a['_id']:<7} x{a['_count']:<4} max={a['_max']:<4} "
          f"{ecsave.item_name(a['_id'])}")

herb_total = sum(i["_count"] for i in items if i["_id"] == 1000)
print(f"\nHealing Herb total across stacks: {herb_total}")
print("no stack exceeds its own max:",
      all(i["_count"] <= i["_max"] for i in items))

# equipment / rune naming
u = ecsave.summarize(after)["units"][0]
print("\nunit 0 equipment:")
for slot, (eid, nm) in enumerate(zip(u["equipment"], u["equipmentNames"])):
    print(f"   {ecsave.EQUIP_SLOT_LABELS[slot]:<10} {eid:<7} {nm}")

# equip something by id and confirm it names correctly
ecsave.apply_edits(after, {"units": {"0": {"_equipment": [6011, None, None, None]}}})
ecsave.write_save(SCRATCH, after)
u2 = ecsave.summarize(ecsave.load_json(SCRATCH))["units"][0]
print(f"\nafter equipping 6011: {u2['equipment'][0]} = {u2['equipmentNames'][0]}")
print("other slots untouched:", u2["equipment"][1:] == u["equipment"][1:])

for f in (SCRATCH, SCRATCH + ".bak"):
    if os.path.exists(f):
        os.remove(f)
print("\nscratch cleaned up")
