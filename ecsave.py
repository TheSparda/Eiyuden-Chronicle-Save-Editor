#!/usr/bin/env python3
"""Eiyuden Chronicle: Hundred Heroes save reader/writer (stdlib only).

Saves live in
    %LOCALAPPDATA%Low\\505 Games S_p_A\\EiyudenChronicle\\<steamid64>\\SaveData\\
and are TripleDES-CBC/PKCS7 over UTF-8 JSON, whole-file (no header, no checksum).

Verified internals
------------------
The key and IV were recovered at runtime from `Rising.CryptoHelper` (IL2CPP, hooked with
a BepInEx/Harmony plugin) -- the class exposes static KEY/IV properties and an
Encryption/Composite pair. Confirmed both directions against seven plaintext/ciphertext
pairs captured from the running game: decrypting the game's ciphertext reproduces its
plaintext exactly, and re-encrypting that plaintext reproduces the game's ciphertext
byte-for-byte. Real save files on disk round-trip byte-for-byte too.

Because the payload is plain JSON there is no checksum to repair -- the only integrity
requirement is that the file decrypts to valid JSON, which `write_save` verifies by
decrypting what it just produced before replacing anything.
"""
import datetime, json, os, re, shutil, glob

import pydes

# Recovered from Rising.CryptoHelper at runtime (see module docstring).
KEY = bytes.fromhex("b3ba76ead29507bad9e68bab87b6e920fe5193bdce92a870")
IV = bytes.fromhex("2f6e9693c9779505")

SAVE_GLOB = os.path.join(
    os.environ.get("LOCALAPPDATA", ""), "..", "LocalLow", "505 Games S_p_A",
    "EiyudenChronicle", "*", "SaveData")

USER_RE = re.compile(r"UserData(\d+)\.dat$", re.I)

# Difficulty. The game offers exactly two settings (localization keys
# Option_Difficulty_Setting_Hard / _Normal).
#
# WHICH FIELD: a controlled diff of two saves taken 11 minutes apart on the same
# playthrough -- one Hard, one Normal -- differed in only four leaves across the entire
# save, and the only difficulty-related one was `_difficultyData._difficulty`.
#
# WHICH VALUE: 0 = Hard, 1 = Normal. Note this is the reverse of the "0 must be the
# default, and the default must be Normal" guess. Two independent lines of evidence:
#   * ground truth from the pair above -- the save reported as Hard holds 0, the one
#     reported as Normal holds 1;
#   * IL2CPP emits the localization literals in enum order, and `..._Setting_Hard`
#     precedes `..._Setting_Normal`.
DIFFICULTY_NAMES = {0: "Hard", 1: "Normal"}

# The five optional modifiers shown under the difficulty menu, each an independent bool.
DIFFICULTY_FLAGS = [
    ("_isNotBattleRewardMoney", "No money from battles"),
    ("_isNotBattleItemUsage", "No recovery items in battle"),
    ("_isBattleCostDouble", "MP/SP costs doubled"),
    ("_isNotBattleEscape", "Cannot escape battles"),
    ("_isHyperInflation", "Hyper inflation"),
]

_DOTNET_EPOCH = datetime.datetime(1, 1, 1)

# Item/equipment/rune names, extracted from the community Cheat Engine table by
# build_names.py. Covers every id present in real saves tested so far.
_NAMES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "ec_item_names.json")


def _load_item_names():
    try:
        with open(_NAMES_PATH, encoding="utf-8") as f:
            return {int(k): v for k, v in json.load(f).items()}
    except (OSError, ValueError):
        return {}


ITEM_NAMES = _load_item_names()

# Per-item stack maxima the game itself has written, harvested from real saves by
# build_names.py. Used when adding a new stack so it matches what the game would store.
_MAXES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "ec_item_maxes.json")


def _load_item_maxes():
    try:
        with open(_MAXES_PATH, encoding="utf-8") as f:
            return {int(k): int(v) for k, v in json.load(f).items()}
    except (OSError, ValueError):
        return {}


ITEM_MAXES = _load_item_maxes()
DEFAULT_STACK_MAX = 99

# Unit id -> character name, dumped from the game's own GetCharacterName(int) by the
# BepInEx plugin (see plugin/Plugin.cs). Optional: without it the editor shows raw ids.
_UNIT_NAMES_PATHS = [
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "ec_unit_names.json"),
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "dump", "ec_unit_names.json"),
]


def _load_unit_names():
    for path in _UNIT_NAMES_PATHS:
        try:
            with open(path, encoding="utf-8") as f:
                return {int(k): v for k, v in json.load(f).items()}
        except (OSError, ValueError):
            continue
    return {}


UNIT_NAMES = _load_unit_names()


def unit_name(unit_id):
    """Character name for a unit id, or '#<id>' when the name table isn't present."""
    return UNIT_NAMES.get(unit_id) or f"#{unit_id}"


def item_max(item_id):
    """Best known stack maximum for an item id."""
    return ITEM_MAXES.get(item_id, DEFAULT_STACK_MAX)


def catalog():
    """Grouped id/name lists for the editor's pickers."""
    def rng(lo, hi):
        # id 0 ("Nothing") leads every slot list so an empty slot reads as a real choice
        # rather than a bare number, and can be picked to clear the slot.
        out = [{"id": 0, "name": ITEM_NAMES.get(0, "Nothing")}]
        out += [{"id": i, "name": n} for i, n in sorted(ITEM_NAMES.items())
                if lo <= i <= hi]
        return out

    return {
        "equipment": rng(*EQUIP_ID_RANGE),
        "runes": rng(*RUNE_ID_RANGE),
        "items": [{"id": i, "name": n, "max": item_max(i)}
                  for i, n in sorted(ITEM_NAMES.items())],
    }


def item_name(item_id):
    """Plain-English name for an item id ('' when unknown, 'Nothing' for 0)."""
    return ITEM_NAMES.get(item_id, "")


# Equipment slots are fixed: head, body, hands(?), accessory. The game stores four.
EQUIP_SLOT_LABELS = ["Head", "Body", "Hands", "Accessory"]

# Rough id ranges, used to offer a sensible shortlist per slot rather than all 1142.
# Derived from the id ranges in the extracted table.
EQUIP_ID_RANGE = (6000, 6999)
RUNE_ID_RANGE = (7000, 7999)


def decode_dotnet_datetime(value):
    """Convert a .NET DateTime.ToBinary() long into a local-time datetime.

    The top two bits are the DateTimeKind (which is why these serialize as large negative
    numbers); the low 62 bits are ticks of 100 ns since 0001-01-01. The game stores UTC,
    so the result is shifted into local time for display.
    """
    if not isinstance(value, int):
        return None
    try:
        ticks = value & 0x3FFFFFFFFFFFFFFF
        utc = _DOTNET_EPOCH + datetime.timedelta(microseconds=ticks // 10)
        return (utc.replace(tzinfo=datetime.timezone.utc)
                   .astimezone()
                   .replace(tzinfo=None))
    except (OverflowError, ValueError, OSError):
        return None


# --- crypto ---------------------------------------------------------------------
def decrypt(blob):
    return pydes.cbc_decrypt(KEY, IV, blob)


def encrypt(plain):
    return pydes.cbc_encrypt(KEY, IV, plain)


def load_json(path):
    """Decrypt a .dat and parse its JSON."""
    with open(path, "rb") as f:
        blob = f.read()
    return json.loads(decrypt(blob).decode("utf-8"))


def dump_json(obj):
    """Serialize the way the game does: compact separators, non-ASCII preserved."""
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def write_save(path, obj, make_backup=True):
    """Encrypt and write `obj`, verifying the result decrypts back to the same data.

    Nothing is written until the verification round-trip passes, and the original is
    copied to `<path>.bak` (once) first.
    """
    plain = dump_json(obj)
    blob = encrypt(plain)

    check = json.loads(decrypt(blob).decode("utf-8"))
    if check != obj:
        raise ValueError("verification failed: re-decrypted save does not match edits")

    if make_backup and os.path.exists(path):
        bak = path + ".bak"
        if not os.path.exists(bak):
            shutil.copy2(path, bak)

    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(blob)
    os.replace(tmp, path)
    return len(blob)


# --- discovery ------------------------------------------------------------------
def find_save_dirs():
    return [os.path.normpath(p) for p in glob.glob(SAVE_GLOB) if os.path.isdir(p)]


def _slot_index(path):
    m = USER_RE.search(os.path.basename(path))
    return int(m.group(1)) if m else None


def list_saves(save_dir):
    """Every UserData*.dat in a save folder, annotated from UserDataInfo.dat when possible."""
    info_by_slot = {}
    info_path = os.path.join(save_dir, "UserDataInfo.dat")
    if os.path.exists(info_path):
        try:
            info = load_json(info_path)
            for entry in info.get("_slotList", []):
                if isinstance(entry, dict) and "No" in entry:
                    info_by_slot[entry["No"]] = entry.get("Data") or {}
        except Exception:
            pass

    out = []
    for path in sorted(glob.glob(os.path.join(save_dir, "UserData*.dat"))):
        base = os.path.basename(path)
        if base.lower() == "userdatainfo.dat":
            continue
        slot = _slot_index(path)
        meta = info_by_slot.get(slot, {})
        saved = decode_dotnet_datetime(meta.get("Datetime"))
        if saved is None:      # test copies and stray files aren't in the slot index
            saved = datetime.datetime.fromtimestamp(os.path.getmtime(path))
        out.append({
            "path": path,
            "name": base,
            "slot": slot,
            "size": os.path.getsize(path),
            "mtime": os.path.getmtime(path),
            "saved": saved.strftime("%Y-%m-%d %H:%M"),
            "level": meta.get("Level"),
            "playtime": meta.get("Playtime"),
            "fortressTownLevel": meta.get("FortressTownLevel"),
        })
    out.sort(key=lambda s: (s["slot"] is None, s["slot"] if s["slot"] is not None else 0))
    return out


# --- decode for the UI ------------------------------------------------------------
EDITABLE_TOP = [
    ("_money", "Money", int),
    ("_seconds", "Playtime (seconds)", float),
    ("_personalUnitId", "Personal unit id", int),
    ("_lapPlayCount", "New Game+ count", int),
]

UNIT_FIELDS = [
    ("_exp", "EXP", int),
    ("_hp", "HP", int),
    ("_mp", "MP", int),
    ("_weaponLevel", "Weapon Lv", int),
]


def summarize(obj):
    """Flatten the parts of a save the editor exposes."""
    units = []
    for i, u in enumerate((obj.get("_unitData") or {}).get("_units") or []):
        if not isinstance(u, dict):
            continue
        equipment = list(u.get("_equipment") or [])
        runes = [rh.get("_itemId") for rh in (u.get("_runeHoles") or [])
                 if isinstance(rh, dict)]
        units.append({
            "index": i,
            "id": u.get("_id"),
            "name": unit_name(u.get("_id")),
            "exp": u.get("_exp"),
            "hp": u.get("_hp"),
            "mp": u.get("_mp"),
            "weaponLevel": u.get("_weaponLevel"),
            "equipment": equipment,
            "equipmentNames": [item_name(e) for e in equipment],
            "runeHoles": runes,
            "runeNames": [item_name(r) for r in runes],
            # a hole the game hasn't unlocked yet can't hold a rune
            "runeReleased": [bool(rh.get("_released"))
                             for rh in (u.get("_runeHoles") or [])
                             if isinstance(rh, dict)],
        })

    items = []
    for i, it in enumerate((obj.get("_inventory") or {}).get("_items") or []):
        if isinstance(it, dict):
            items.append({"index": i, "id": it.get("_id"),
                          "name": item_name(it.get("_id")),
                          "count": it.get("_count"), "max": it.get("_max")})

    town = obj.get("_fortressTown") or {}
    diff = obj.get("_difficultyData") or {}
    saved_at = decode_dotnet_datetime(obj.get("_datetime"))
    return {
        "difficulty": diff.get("_difficulty"),
        "difficultyName": DIFFICULTY_NAMES.get(diff.get("_difficulty"), "?"),
        "difficultyFlags": {k: bool(diff.get(k)) for k, _ in DIFFICULTY_FLAGS},
        "difficultyFlagLabels": {k: label for k, label in DIFFICULTY_FLAGS},
        "savedAt": saved_at.strftime("%Y-%m-%d %H:%M:%S") if saved_at else None,
        "versionCode": obj.get("_versionCode"),
        "appVersionCode": obj.get("_appVersionCode"),
        "money": obj.get("_money"),
        "seconds": obj.get("_seconds"),
        "personalUnitId": obj.get("_personalUnitId"),
        "lapPlayCount": obj.get("_lapPlayCount"),
        "mainScenarioCleared": obj.get("_mainScenaionCleared"),
        "fortressTownLevel": town.get("_fortressTownLevel"),
        "population": town.get("_population"),
        "units": units,
        "items": items,
        "topLevelKeys": sorted(obj.keys()),
    }


# --- editing --------------------------------------------------------------------
def _coerce(value, kind):
    if kind is int:
        return int(value)
    if kind is float:
        return float(value)
    return value


def apply_edits(obj, edits):
    """Apply a UI edit payload in place. Returns the number of fields changed.

    edits = {
      "top":   {"_money": 99999, "_seconds": 1234.5},
      "town":  {"_fortressTownLevel": 5, "_population": 100},
      "units": {"<index>": {"_exp": 1, "_hp": 2, "_equipment": [...],
                            "_runeHoles": [...]}},
      "items": {"<index>": {"_count": 99, "_max": 99}},
      "difficulty": {"_difficulty": 1, "_isNotBattleEscape": true, ...},
    }
    """
    changed = 0
    kinds = {k: t for k, _, t in EDITABLE_TOP}

    for key, val in (edits.get("top") or {}).items():
        if key in kinds:
            obj[key] = _coerce(val, kinds[key])
            changed += 1

    town_edits = edits.get("town") or {}
    if town_edits:
        town = obj.setdefault("_fortressTown", {})
        for key in ("_fortressTownLevel", "_population"):
            if key in town_edits:
                town[key] = int(town_edits[key])
                changed += 1

    diff_edits = edits.get("difficulty") or {}
    if diff_edits:
        diff = obj.setdefault("_difficultyData", {})
        if "_difficulty" in diff_edits:
            level = int(diff_edits["_difficulty"])
            if level not in DIFFICULTY_NAMES:
                raise ValueError(f"difficulty must be one of {sorted(DIFFICULTY_NAMES)}")
            diff["_difficulty"] = level
            changed += 1
        for key, _ in DIFFICULTY_FLAGS:
            if key in diff_edits:
                diff[key] = bool(diff_edits[key])
                changed += 1

    units = (obj.get("_unitData") or {}).get("_units") or []
    unit_kinds = {k: t for k, _, t in UNIT_FIELDS}
    for idx, fields in (edits.get("units") or {}).items():
        i = int(idx)
        if not (0 <= i < len(units)) or not isinstance(units[i], dict):
            continue
        unit = units[i]
        for key, val in (fields or {}).items():
            if key in unit_kinds:
                unit[key] = _coerce(val, unit_kinds[key])
                changed += 1
            elif key == "_equipment" and isinstance(val, list):
                # fixed-length slot array; only overwrite slots the UI actually sent
                equip = unit.setdefault("_equipment", [])
                for slot, item_id in enumerate(val):
                    if slot < len(equip) and item_id is not None:
                        equip[slot] = int(item_id)
                        changed += 1
            elif key == "_runeHoles" and isinstance(val, list):
                holes = unit.get("_runeHoles") or []
                for slot, item_id in enumerate(val):
                    if slot < len(holes) and isinstance(holes[slot], dict) \
                            and item_id is not None:
                        holes[slot]["_itemId"] = int(item_id)
                        changed += 1

    inventory = obj.setdefault("_inventory", {})
    items = inventory.setdefault("_items", [])
    for idx, fields in (edits.get("items") or {}).items():
        i = int(idx)
        if not (0 <= i < len(items)) or not isinstance(items[i], dict):
            continue
        for key in ("_count", "_max", "_id"):
            if key in (fields or {}):
                items[i][key] = int(fields[key])
                changed += 1

    # Removals run before additions so indices in `removeItems` refer to what the UI saw.
    remove = sorted({int(i) for i in (edits.get("removeItems") or [])}, reverse=True)
    for i in remove:
        if 0 <= i < len(items):
            del items[i]
            changed += 1

    for entry in (edits.get("addItems") or []):
        item_id = int(entry.get("_id", 0))
        if item_id <= 0:
            continue
        count = max(1, int(entry.get("_count", 1)))
        stack_max = int(entry.get("_max") or item_max(item_id))
        # The game stores one entry per stack and never exceeds a stack's own max, so
        # split larger requests across as many stacks as needed.
        while count > 0:
            n = min(count, stack_max)
            items.append({"_id": item_id, "_count": n, "_max": stack_max})
            count -= n
            changed += 1

    return changed


if __name__ == "__main__":
    import sys
    if len(sys.argv) < 2:
        print("usage:")
        print("  ecsave.py list                      # find saves")
        print("  ecsave.py show <UserDataN.dat>      # summary")
        print("  ecsave.py dump <UserDataN.dat> [out.json]")
        print("  ecsave.py pack <in.json> <UserDataN.dat>")
        sys.exit(1)

    cmd = sys.argv[1]
    if cmd == "list":
        for d in find_save_dirs():
            print(d)
            for s in list_saves(d):
                pt = s["playtime"]
                extra = f"  Lv{s['level']}" if s["level"] is not None else ""
                extra += f"  {pt//3600}h{(pt%3600)//60:02d}m" if isinstance(pt, int) else ""
                print(f"   {s['name']:<20} {s['size']:>8} bytes{extra}")
    elif cmd == "show":
        info = summarize(load_json(sys.argv[2]))
        print(json.dumps({k: v for k, v in info.items() if k != "topLevelKeys"},
                         indent=2)[:4000])
    elif cmd == "dump":
        obj = load_json(sys.argv[2])
        out = sys.argv[3] if len(sys.argv) > 3 else "save_decoded.json"
        json.dump(obj, open(out, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
        print("wrote", out)
    elif cmd == "pack":
        obj = json.load(open(sys.argv[2], encoding="utf-8"))
        n = write_save(sys.argv[3], obj)
        print(f"wrote {n} bytes to {sys.argv[3]} (backup at .bak)")
    else:
        print("unknown command:", cmd)
