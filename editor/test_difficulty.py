"""Verify the difficulty toggle against ground-truth saves.

Ground truth, reported by the player at the time each was made:
    UserData11 (22:40) = HARD    -> _difficulty 0
    UserData12 (22:51) = NORMAL  -> _difficulty 1
The two were taken 11 minutes apart in the same playthrough, so they differ in almost
nothing else -- a controlled pair. Corroborated by IL2CPP emitting the localization
literals in enum order, `Option_Difficulty_Setting_Hard` before `..._Normal`.
"""
import json, os, shutil
import ecsave
import testutil

HARD_SRC = testutil.save_path("UserData11.dat")     # player-reported Hard
NORMAL_SRC = testutil.save_path("UserData12.dat")   # player-reported Normal
SCRATCH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_diff_test.dat")

hard_block = ecsave.load_json(HARD_SRC)["_difficultyData"]
normal_block = ecsave.load_json(NORMAL_SRC)["_difficultyData"]

print("ground truth from the game's own saves")
print(f"  HARD   save -> _difficulty = {hard_block['_difficulty']}")
print(f"  NORMAL save -> _difficulty = {normal_block['_difficulty']}")

assert hard_block["_difficulty"] == 0, "expected Hard to be 0"
assert normal_block["_difficulty"] == 1, "expected Normal to be 1"
print(f"\nmapping in ecsave: {ecsave.DIFFICULTY_NAMES}")
assert ecsave.DIFFICULTY_NAMES[0] == "Hard"
assert ecsave.DIFFICULTY_NAMES[1] == "Normal"
print("mapping matches ground truth: yes")

# Round-trip: start from the Hard save, switch to Normal, and we should reproduce the
# game's own Normal block exactly.
shutil.copy2(HARD_SRC, SCRATCH)
obj = ecsave.load_json(SCRATCH)
print(f"\nstarting from Hard save: {ecsave.summarize(obj)['difficultyName']}")

ecsave.apply_edits(obj, {"difficulty": {"_difficulty": 1}})
ecsave.write_save(SCRATCH, obj)
after = ecsave.load_json(SCRATCH)
print(f"after switching to Normal: {ecsave.summarize(after)['difficultyName']}")
print("matches the game's own Normal block:", after["_difficultyData"] == normal_block)

# ...and back to Hard
ecsave.apply_edits(after, {"difficulty": {"_difficulty": 0}})
ecsave.write_save(SCRATCH, after)
back = ecsave.load_json(SCRATCH)
print("switched back to Hard:", ecsave.summarize(back)["difficultyName"] == "Hard")
print("matches the game's own Hard block:", back["_difficultyData"] == hard_block)

# modifiers are independent of the level
ecsave.apply_edits(back, {"difficulty": {"_isNotBattleEscape": True,
                                         "_isHyperInflation": True}})
ecsave.write_save(SCRATCH, back)
flags = ecsave.load_json(SCRATCH)["_difficultyData"]
print("\nwith modifiers:", json.dumps(flags))

try:
    ecsave.apply_edits(flags, {"difficulty": {"_difficulty": 7}})
    print("rejects invalid difficulty: NO (should have raised)")
except ValueError as e:
    print(f"rejects invalid difficulty: yes ({e})")

orig = ecsave.load_json(HARD_SRC)
final = ecsave.load_json(SCRATCH)
others = [k for k in orig if k != "_difficultyData" and orig[k] != final.get(k)]
print("other top-level keys changed:", others or "none")

for f in (SCRATCH, SCRATCH + ".bak"):
    if os.path.exists(f):
        os.remove(f)
print("\nscratch cleaned up")
