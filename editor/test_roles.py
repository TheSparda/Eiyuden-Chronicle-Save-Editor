"""Battle/support/castle-only classification.

Unlike rune holes or stats, this genuinely isn't derivable from any save -- it came from
a runtime hook against the game's own UnitParamTable (CanBattle/CanSupport plus the raw
UnitType enum). This just checks the shipped table is complete and internally consistent.
"""
import ecsave

roles = ecsave.UNIT_ROLES
print(f"characters with role data: {len(roles)} / {len(ecsave.UNIT_NAMES)} known")

missing = [uid for uid in ecsave.UNIT_NAMES if uid not in roles]
print(f"known characters missing role data: {len(missing)}"
      + (f" ({[ecsave.unit_name(u) for u in missing[:5]]})" if missing else ""))

bad = []
for uid, r in roles.items():
    t, battle, support = r.get("unitType"), r.get("battle"), r.get("support")
    ok = ((t == "Battle" and battle and not support) or
          (t == "Support" and support and not battle) or
          (t == "Hybrid" and battle and support) or
          (t == "Other" and not battle and not support))
    if not ok:
        bad.append((uid, r))
print(f"unitType disagrees with (battle, support) flags: {len(bad)}"
      + (f" e.g. {bad[:3]}" if bad else ""))

import collections
dist = collections.Counter(r["unitType"] for r in roles.values())
print("distribution:", dict(dist))

print("\nALL ROLE CHECKS PASSED" if not missing and not bad else "SOMETHING FAILED")
