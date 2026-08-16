"""Extract the item id -> name table from the community Cheat Engine table.

The CT ships a 1142-entry DropDownList mapping item ids to English names. That is exactly
the lookup the editor needs to show equipment, runes and inventory in plain English
instead of raw ids, so we pull it out once into ec_item_names.json.
"""
import json, os, re, collections

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                # reference/ sits beside editor/
CT = os.path.join(ROOT, "reference", "EiyudenChronicle.CT")
OUT = os.path.join(HERE, "ec_item_names.json")

raw = open(CT, encoding="utf-8", errors="replace").read()

best = {}
for m in re.finditer(r"<DropDownList[^>]*>(.*?)</DropDownList>", raw, re.S):
    entries = {}
    for line in m.group(1).splitlines():
        line = line.strip()
        if not line:
            continue
        key, sep, name = line.partition(":")
        if not sep or not key.strip().isdigit():
            continue
        entries[int(key.strip())] = name.strip()
    if len(entries) > len(best):
        best = entries

print(f"largest dropdown: {len(best)} entries")

# Group by id range to sanity-check what the ranges mean
buckets = collections.defaultdict(list)
for k, v in sorted(best.items()):
    buckets[k // 1000 * 1000].append((k, v))

print("\nid ranges:")
for base in sorted(buckets):
    sample = ", ".join(n for _, n in buckets[base][:3])
    print(f"  {base:>6}-{base+999:<6} {len(buckets[base]):>4} items   e.g. {sample}")

json.dump({str(k): v for k, v in sorted(best.items())},
          open(OUT, "w", encoding="utf-8"), indent=0, ensure_ascii=False)
print(f"\nwrote {OUT} ({len(best)} names)")

# --- observed stack maxima -------------------------------------------------------
# Adding an item to the inventory needs a plausible `_max` for the stack, and the game
# varies it per item (Healing Herb 6, Premium Healing Herb 4, Revival Medicine 2).
# Rather than guess, harvest what the game itself has written across every save.
import ecsave  # noqa: E402  (after the names file exists)

MAX_OUT = os.path.join(HERE, "ec_item_maxes.json")
seen = collections.defaultdict(collections.Counter)
scanned = 0
for save_dir in ecsave.find_save_dirs():
    for s in ecsave.list_saves(save_dir):
        try:
            obj = ecsave.load_json(s["path"])
        except Exception:
            continue
        scanned += 1
        for it in (obj.get("_inventory") or {}).get("_items") or []:
            if isinstance(it, dict) and it.get("_id") is not None \
                    and it.get("_max") is not None:
                seen[it["_id"]][it["_max"]] += 1

maxes = {str(i): c.most_common(1)[0][0] for i, c in sorted(seen.items())}
json.dump(maxes, open(MAX_OUT, "w", encoding="utf-8"), indent=0)
print(f"wrote {MAX_OUT} ({len(maxes)} stack maxima observed across {scanned} saves)")

# --- observed equipment slot per item -------------------------------------------
# Which of the four slots the game itself has put each piece of gear in. The id layout
# is mostly clean (see ecsave.EQUIP_SLOT_RANGES), but 6200-6299 interleaves head and
# body gear irregularly, so observation is the only reliable source there.
SLOT_OUT = os.path.join(HERE, "ec_equip_slots.json")
slot_seen = collections.defaultdict(collections.Counter)
for save_dir in ecsave.find_save_dirs():
    for s in ecsave.list_saves(save_dir):
        try:
            obj = ecsave.load_json(s["path"])
        except Exception:
            continue
        for u in (obj.get("_unitData") or {}).get("_units") or []:
            for slot, item in enumerate(u.get("_equipment") or []):
                if item:
                    slot_seen[item][slot] += 1

conflicts = [i for i, c in slot_seen.items() if len(c) > 1]
slots = {str(i): c.most_common(1)[0][0] for i, c in sorted(slot_seen.items())}
json.dump(slots, open(SLOT_OUT, "w", encoding="utf-8"), indent=0)
print(f"wrote {SLOT_OUT} ({len(slots)} items seen equipped; "
      f"{len(conflicts)} appeared in more than one slot)")
