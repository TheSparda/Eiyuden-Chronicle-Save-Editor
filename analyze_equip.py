"""Work out which equipment ids belong in which slot.

Two sources, cross-checked:
  * observed - every (slot, item id) pair the game itself has written across all saves;
  * the id layout of the 6000-6999 name range.
Observation is authoritative but only covers gear the player has actually equipped, so we
use it to establish the id ranges, then apply the ranges to the full catalogue.
"""
import collections
import ecsave

SLOTS = ecsave.EQUIP_SLOT_LABELS

observed = collections.defaultdict(set)     # slot index -> ids
for d in ecsave.find_save_dirs():
    for s in ecsave.list_saves(d):
        try:
            obj = ecsave.load_json(s["path"])
        except Exception:
            continue
        for u in (obj.get("_unitData") or {}).get("_units") or []:
            for i, item in enumerate(u.get("_equipment") or []):
                if item:
                    observed[i].add(item)

print("=== observed ids per slot (from real saves) ===")
for i in sorted(observed):
    ids = sorted(observed[i])
    print(f"\n{SLOTS[i] if i < len(SLOTS) else i} (slot {i}): {len(ids)} distinct")
    print(f"  range {min(ids)}..{max(ids)}")
    # bucket by hundreds to expose the layout
    buckets = collections.Counter(x // 100 * 100 for x in ids)
    print("  buckets:", dict(sorted(buckets.items())))

print("\n\n=== do the slots overlap? ===")
for a in sorted(observed):
    for b in sorted(observed):
        if a < b:
            both = observed[a] & observed[b]
            if both:
                print(f"  slot {a} & {b} share {len(both)}: {sorted(both)[:6]}")
print("  (no output above = each id belongs to exactly one slot)")

print("\n\n=== full 6000-6999 catalogue by hundred-bucket ===")
by_bucket = collections.defaultdict(list)
for i, n in sorted(ecsave.ITEM_NAMES.items()):
    if 6000 <= i <= 6999:
        by_bucket[i // 100 * 100].append((i, n))
for base in sorted(by_bucket):
    names = by_bucket[base]
    owner = [s for s in observed if any(i in observed[s] for i, _ in names)]
    label = ", ".join(SLOTS[s] for s in sorted(owner)) or "(never equipped in these saves)"
    print(f"\n{base}-{base+99}: {len(names)} items -> {label}")
    print("   ", ", ".join(n for _, n in names[:6]))
