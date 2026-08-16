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
_HERE = os.path.dirname(os.path.abspath(__file__))
_UNIT_NAMES_PATHS = [
    os.path.join(_HERE, "ec_unit_names.json"),
    os.path.join(os.path.dirname(_HERE), "dump", "ec_unit_names.json"),   # plugin output
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
        "equipment": rng(*EQUIP_ID_RANGE),          # every slot, for reference
        "equipBySlot": {str(s): equip_candidates(s)
                        for s in range(len(EQUIP_SLOT_LABELS))},
        "equipSlotLabels": EQUIP_SLOT_LABELS,
        "runes": rng(*RUNE_ID_RANGE),
        "items": [{"id": i, "name": n, "max": item_max(i),
                   "category": item_category(i)}
                  for i, n in sorted(ITEM_NAMES.items())],
        "categories": [c for c in CATEGORY_ORDER
                       if any(item_category(i) == c for i in ITEM_NAMES)],
    }


def item_name(item_id):
    """Plain-English name for an item id ('' when unknown, 'Nothing' for 0)."""
    return ITEM_NAMES.get(item_id, "")


# Item categories, read off the id layout of the extracted name table. Every block is
# internally consistent -- the labels below are taken from what the names in each range
# actually are, not guessed:
#
#   1000s  Healing Herb, Spirit Medicine, Magic Drop        -> battle consumables
#   3000s  Poached Egg, Miso Soup, Cherry Pie               -> cooked dishes
#   4000s-5000s  Runeshard of Fire 1, Magic Sword RS        -> runeshards
#   6000-6699    helmets, armour, shields                   -> equipment
#   6700-6899    badges, brooches, bangles, Mark of a Hero  -> accessories
#   7000s  Rune of Fire, Rune of Herbal Alchemy             -> runes
#   8000s  ... Recipe, ... Script                           -> recipes and scripts
#   9000s  Unknown Coin, Porcelain Vase, Astrolabe          -> valuables
#   10000s Rock Salt, Wine, Red Gemstone, Gold Bread        -> trade goods
#   12000s Rubber Duckie, Capybara Army, Autumn Leaves      -> decorations
#   20000s First Egg ... Champion Egg                       -> race eggs
#   50000s Food, Lumber, Stone, Pelt                        -> town resources
#   51000s Mystic Lumber, Iron Ore, Anc. Dragon's Tooth     -> materials
#   52000s Vegetables, Egg, Meat, Salt, Spice               -> ingredients
#   70000s Fruit of Knowledge, Essence of Crown             -> special items
#   71000s Pieter, Marin, Extra Pack 3                      -> cards
#   72000s Plantvine, Sahagin, Wind Beigoma                 -> beigoma
#   98000s Scroll of Heaven, Carrie's Ring                  -> quest items
#   99000s+ Bamboo Rod, Beigoma Box, Red Key, Golden Key    -> tools and keys
ITEM_CATEGORIES = [
    (1000, 2999, "Medicine"),
    (3000, 3999, "Dishes"),
    (4000, 5999, "Runeshards"),
    (6000, 6699, "Equipment"),
    (6700, 6999, "Accessories"),
    (7000, 7999, "Runes"),
    (8000, 8999, "Recipes & scripts"),
    (9000, 9999, "Valuables"),
    (10000, 11999, "Trade goods"),
    (12000, 19999, "Decorations"),
    (20000, 20999, "Race eggs"),
    (50000, 50999, "Town resources"),
    (51000, 51999, "Materials"),
    (52000, 52999, "Ingredients"),
    (70000, 70999, "Special items"),
    (71000, 71999, "Cards"),
    (72000, 72999, "Beigoma"),
    (98000, 98999, "Quest items"),
    (99000, 999999, "Tools & keys"),
]

CATEGORY_ORDER = [label for _, _, label in ITEM_CATEGORIES] + ["Other"]


def item_category(item_id):
    """Category label for an item id; 'Other' for anything outside the known blocks."""
    for lo, hi, label in ITEM_CATEGORIES:
        if lo <= item_id <= hi:
            return label
    return "Other"


# Equipment slots are fixed: head, body, hands (shields), accessory. The game stores four.
EQUIP_SLOT_LABELS = ["Head", "Body", "Hands", "Accessory"]

EQUIP_ID_RANGE = (6000, 6999)
RUNE_ID_RANGE = (7000, 7999)

# Which ids belong to which slot. Established by tabulating every (slot, item) pair the
# game itself has written across all saves, then reading off the id layout:
#
#   6000-6199  Head        helmets, hats, masks
#   6200-6299  Head OR Body -- this block INTERLEAVES the two (Headband/Cloth Wear,
#              Scarf/Traveler's Clothes, ...). The alternation is *almost* even=head /
#              odd=body but not exactly (6233 "Crown of Guile" is odd yet sits on Head),
#              so parity is not trusted; observation decides, and anything not yet
#              observed is offered in both lists rather than hidden.
#   6300-6599  Body        armour, mail, robes
#   6600-6699  Hands       shields
#   6700-6999  Accessory   badges, charms, rings
EQUIP_SLOT_RANGES = {
    0: [(6000, 6199)],
    1: [(6300, 6599)],
    2: [(6600, 6699)],
    3: [(6700, 6999)],
}
EQUIP_AMBIGUOUS_RANGE = (6200, 6299)
EQUIP_AMBIGUOUS_SLOTS = (0, 1)          # head or body

_EQUIP_SLOTS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                 "ec_equip_slots.json")


def _load_equip_slots():
    try:
        with open(_EQUIP_SLOTS_PATH, encoding="utf-8") as f:
            return {int(k): int(v) for k, v in json.load(f).items()}
    except (OSError, ValueError):
        return {}


EQUIP_SLOT_OBSERVED = _load_equip_slots()


def equip_candidates(slot):
    """Items offerable in an equipment slot, most specific evidence first.

    An id the game has actually been seen using in some slot is offered only there.
    Otherwise the id ranges decide, and ids in the interleaved 6200-6299 block that we
    have never observed are offered for both head and body. Nothing is truly locked out:
    the UI accepts a raw id, so an unlisted item can still be entered deliberately.
    """
    lo_amb, hi_amb = EQUIP_AMBIGUOUS_RANGE
    out = [{"id": 0, "name": ITEM_NAMES.get(0, "Nothing")}]
    for i, name in sorted(ITEM_NAMES.items()):
        if not (EQUIP_ID_RANGE[0] <= i <= EQUIP_ID_RANGE[1]):
            continue
        seen = EQUIP_SLOT_OBSERVED.get(i)
        if seen is not None:
            keep = (seen == slot)
        elif lo_amb <= i <= hi_amb:
            keep = slot in EQUIP_AMBIGUOUS_SLOTS
        else:
            keep = any(lo <= i <= hi for lo, hi in EQUIP_SLOT_RANGES.get(slot, []))
        if keep:
            out.append({"id": i, "name": name})
    return out


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
            "role": (UNIT_ROLES.get(u.get("_id")) or {}).get("unitType"),
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
                          "category": item_category(it.get("_id") or 0),
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
        "roster": roster(obj),
        "recruitedCount": len(recruited_ids(obj)),
        "knownCount": len(UNIT_NAMES),
        "topLevelKeys": sorted(obj.keys()),
    }


# --- editing --------------------------------------------------------------------
# --- roster / recruitment --------------------------------------------------------
# A character is "recruited" exactly when they have a record in _unitData._units: an
# early save holds 6 entries, a completed one 120. The records are uniform (a single key
# shape across all 120), so a new one can be built faithfully by copying an existing
# record from the same save and neutralising the per-character progress.
#
# CAVEAT, and it is a real one: adding the record is not provably the whole of what the
# game means by "recruited". Recruitment events also set named flags in _flagData._flags,
# and no controlled before/after pair was available to prove whether the roster entry
# alone is sufficient. Treat recruiting as experimental -- hence the warning in the UI and
# the automatic backup. Removing a character is the safer direction and is fully reversible.

UNIT_TEMPLATE = {
    "_id": 0,
    "_exp": 0,
    "_hp": 0,
    "_mp": 0,
    "_weaponLevel": 1,
    "_equipment": [0, 0, 0, 0],
    "_invalidEquipOperation": False,
    "_registState": 0,
    "_organizeState": 0,
    "_runeHoles": [],          # filled in per character -- see _rune_holes_for()
    "_autoCommandPreset": {
        "_prioritySet1": {"_priorityType": 1, "_active": True},
        "_prioritySet2": {"_priorityType": 3, "_active": True},
        "_prioritySet3": {"_priorityType": 3, "_active": True},
        "_prioritySet4": {"_priorityType": 2, "_active": True},
        "_restrictType": 1,
        "_targetType": 1,
    },
    "_partyAssignedData": {"_secondsJoinedInParty": 0.0, "_secondsSpentAtParty": 0.0},
    "_bathedHotSpringLevel": 0,
    "_bathedRelaxPlaySeconds": 0.0,
    "_isInherited": False,
}


# How many rune holes each character has. This is fixed master data, not something that
# grows with level: harvested from every local save by build_names.py, 120 characters
# agreed across all of them with no disagreement. A save with the full roster is what
# makes an accurate record possible when recruiting into an earlier save.
_HOLES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "ec_unit_runeholes.json")


def _load_rune_holes():
    try:
        with open(_HOLES_PATH, encoding="utf-8") as f:
            return {int(k): int(v) for k, v in json.load(f).items()}
    except (OSError, ValueError):
        return {}


UNIT_RUNE_HOLES = _load_rune_holes()

# Battle/support/castle-only classification, straight from the game's own UnitParamTable
# (CanBattle/CanSupport and the raw UnitType enum) rather than inferred from save data --
# unlike rune holes, this genuinely isn't in any save. Captured via a runtime hook against
# a live, fully-recruited party so every character's true role is covered. "Other" means a
# character is neither a battle nor a support unit: castle/story-only.
_ROLES_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "ec_unit_roles.json")


def _load_unit_roles():
    try:
        with open(_ROLES_PATH, encoding="utf-8") as f:
            return {int(k): v for k, v in json.load(f).items()}
    except (OSError, ValueError):
        return {}


UNIT_ROLES = _load_unit_roles()

# Real (exp, hp, mp, weaponLevel) samples per character, harvested the same way as the
# rune-hole table. _hp/_mp turn out to be CURRENT hp/mp -- the same character at the same
# exp shows different values across saves (battle damage) -- so there's no single "max"
# to store. Recruiting instead picks the sample whose exp is closest to the hero's
# current exp: a value that character has actually had, not a number borrowed from Nowa.
_STATS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "ec_unit_stats.json")


def _load_unit_stats():
    try:
        with open(_STATS_PATH, encoding="utf-8") as f:
            raw = json.load(f)
        return {int(k): [tuple(s) for s in v] for k, v in raw.items()}
    except (OSError, ValueError):
        return {}


UNIT_STATS = _load_unit_stats()


def _scaled_stats(unit_id, target_exp):
    """This character's own hp/mp/weaponLevel, scaled to the hero's current exp.

    Nearest-sample alone isn't enough: a character only ever seen recruited late-game
    (e.g. every real sample near exp 62000) would hand an early save exp=3000 party a
    late-game stat block wholesale -- a level-6-weapon, 400+ HP unit standing next to a
    party still in double digits. Party exp is shared across every recruited character
    (see decode_save), so the sample's own exp *is* what the hero's exp was when that
    sample was taken; scaling hp/mp/weaponLevel by target_exp / sample_exp keeps the new
    recruit's stats proportionate to where the sample actually came from, using only that
    character's own real numbers -- never the hero's.
    """
    samples = UNIT_STATS.get(unit_id)
    if not samples:
        return None
    exp, hp, mp, wl = min(samples, key=lambda s: abs(s[0] - target_exp))
    if exp <= 0:
        return None
    ratio = target_exp / exp
    return (max(1, round(hp * ratio)), max(1, round(mp * ratio)),
            max(1, min(round(wl * ratio), max(s[3] for s in samples))))


def _rune_holes_for(unit_id):
    """A freshly recruited character's holes: the right number for them, with only the
    first unlocked -- the pattern real early-game saves show."""
    n = UNIT_RUNE_HOLES.get(unit_id)
    if n is None:          # unknown character: empty is a shape the game already uses
        return []
    return [{"_itemId": 0, "_released": i == 0, "_isViewedReleaseEffect": i == 0}
            for i in range(n)]


def _units_of(obj):
    return (obj.setdefault("_unitData", {})).setdefault("_units", [])


def recruited_ids(obj):
    return {u.get("_id") for u in _units_of(obj) if isinstance(u, dict)}


def protected_ids(obj):
    """Characters that must not be removed: the player character and anyone currently
    placed in a party. Dropping these risks a save the game cannot load."""
    keep = set()
    pid = obj.get("_personalUnitId")
    if pid is not None:
        keep.add(pid)
    for section in ("_heroParty",):
        block = obj.get(section) or {}
        for key in ("_partyUnitList", "_partyAttendantUnitList"):
            for entry in block.get(key) or []:
                if isinstance(entry, dict) and entry.get("_id") is not None:
                    keep.add(entry["_id"])
                elif isinstance(entry, int):
                    keep.add(entry)
        sup = block.get("_supportUnitID")
        if isinstance(sup, int) and sup >= 0:
            keep.add(sup)
    stock = (obj.get("_unitData") or {}).get("_stockParty") or {}
    for key in ("_partyUnitList", "_partyAttendantUnitList"):
        for entry in stock.get(key) or []:
            if isinstance(entry, dict) and entry.get("_id") is not None:
                keep.add(entry["_id"])
    return keep


def roster(obj):
    """Every known character with whether they are currently recruited."""
    have = recruited_ids(obj)
    guard = protected_ids(obj)
    ids = set(UNIT_NAMES) | have
    out = []
    for uid in sorted(ids):
        out.append({
            "id": uid,
            "name": unit_name(uid),
            "recruited": uid in have,
            "protected": uid in guard,
            "known": uid in UNIT_NAMES,
            "runeHoles": UNIT_RUNE_HOLES.get(uid),
            "role": (UNIT_ROLES.get(uid) or {}).get("unitType"),
        })
    return out


def _hero_record(obj):
    """The player character's own unit record, if it's recruited (it always should be)."""
    pid = obj.get("_personalUnitId")
    for u in _units_of(obj):
        if isinstance(u, dict) and u.get("_id") == pid:
            return u
    return None


def _new_unit_record(obj, unit_id):
    """A record shaped exactly like the ones already in this save.

    EXP always matches the player character, so a freshly recruited unit is at the
    party's level rather than a fresh level 1 (a unit added with 0 EXP/0 HP is recruited
    dead -- confirmed in testing, Chandra spawned into battle at 0/0 as a corpse).

    HP/MP/weapon level are NOT copied from the hero -- they're that character's own real
    numbers (harvested from other local saves), scaled to the hero's current exp so an
    early recruit doesn't inherit a late-game stat block. If this exact character has
    never been observed recruited anywhere, the hero's own numbers are the fallback --
    alive beats exactly accurate.
    """
    existing = [u for u in _units_of(obj) if isinstance(u, dict)]
    base = json.loads(json.dumps(existing[0])) if existing \
        else json.loads(json.dumps(UNIT_TEMPLATE))
    fresh = json.loads(json.dumps(UNIT_TEMPLATE))
    # keep any keys this save's records carry that our template doesn't know about
    for key in base:
        if key not in fresh:
            fresh[key] = base[key]
    fresh["_id"] = int(unit_id)
    fresh["_runeHoles"] = _rune_holes_for(int(unit_id))

    hero = _hero_record(obj)
    hero_exp = hero.get("_exp") if hero else None
    own = _scaled_stats(int(unit_id), hero_exp) if hero_exp else None
    if hero_exp is not None:
        fresh["_exp"] = hero_exp
    if own:
        fresh["_hp"], fresh["_mp"], fresh["_weaponLevel"] = own
    elif hero:
        # no data for this character at all: better alive at the hero's numbers than dead
        fresh["_hp"] = hero.get("_hp", fresh["_hp"])
        fresh["_mp"] = hero.get("_mp", fresh["_mp"])
        fresh["_weaponLevel"] = hero.get("_weaponLevel", fresh["_weaponLevel"])
    return fresh


def add_unit(obj, unit_id):
    if unit_id in recruited_ids(obj):
        return False
    _units_of(obj).append(_new_unit_record(obj, unit_id))
    return True


def remove_unit(obj, unit_id):
    if unit_id in protected_ids(obj):
        raise ValueError(
            f"{unit_name(unit_id)} is the player character or is placed in a party; "
            "remove them from the party in-game first")
    units = _units_of(obj)
    before = len(units)
    units[:] = [u for u in units if not (isinstance(u, dict) and u.get("_id") == unit_id)]
    return len(units) != before


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

    # Recruitment runs before the per-unit edits below, so a newly added character can be
    # edited in the same write; indices are recomputed from the updated list.
    for uid, want in (edits.get("recruit") or {}).items():
        uid = int(uid)
        if want:
            if add_unit(obj, uid):
                changed += 1
        else:
            if remove_unit(obj, uid):
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
            elif key == "_runeHoleReleased" and isinstance(val, dict):
                # Unlocking a rune hole. Across 353 real holes, a locked one
                # (_released false) never once held a rune, so that invariant is enforced
                # here: re-locking a hole clears whatever was in it. Both
                # (released, viewed) = (True, True) and (True, False) occur in real
                # saves; the settled one is used so the game doesn't replay the unlock
                # effect. Runs before _runeHoles below so a slot can be unlocked and
                # filled in the same write.
                holes = unit.get("_runeHoles") or []
                for slot, released in (val or {}).items():
                    slot = int(slot)
                    if not (0 <= slot < len(holes)) or not isinstance(holes[slot], dict):
                        continue
                    released = bool(released)
                    holes[slot]["_released"] = released
                    holes[slot]["_isViewedReleaseEffect"] = released
                    if not released:
                        holes[slot]["_itemId"] = 0
                    changed += 1

    # second pass: rune contents, after any unlocks above have been applied
    for idx, fields in (edits.get("units") or {}).items():
        i = int(idx)
        if not (0 <= i < len(units)) or not isinstance(units[i], dict):
            continue
        val = (fields or {}).get("_runeHoles")
        if isinstance(val, list):
            holes = units[i].get("_runeHoles") or []
            for slot, item_id in enumerate(val):
                if slot < len(holes) and isinstance(holes[slot], dict) \
                        and item_id is not None:
                    # a rune can only sit in an unlocked hole
                    if not holes[slot].get("_released") and int(item_id):
                        continue
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


def resolve_save(arg):
    """Accept either a full path or a bare 'UserData7.dat' from the save folder."""
    if os.path.exists(arg):
        return arg
    if not os.path.dirname(arg):
        for d in find_save_dirs():
            candidate = os.path.join(d, arg)
            if os.path.exists(candidate):
                return candidate
    raise SystemExit(f"save not found: {arg}\n"
                     f"(try `ecsave.py list` to see what's available)")


if __name__ == "__main__":
    import sys
    if len(sys.argv) < 2:
        print("usage:")
        print("  ecsave.py list                      # find saves")
        print("  ecsave.py show <UserDataN.dat>      # summary")
        print("  ecsave.py dump <UserDataN.dat> [out.json]")
        print("  ecsave.py pack <in.json> <UserDataN.dat>")
        print()
        print("Save arguments may be a bare filename or a full path.")
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
        info = summarize(load_json(resolve_save(sys.argv[2])))
        print(json.dumps({k: v for k, v in info.items() if k != "topLevelKeys"},
                         indent=2)[:4000])
    elif cmd == "dump":
        obj = load_json(resolve_save(sys.argv[2]))
        out = sys.argv[3] if len(sys.argv) > 3 else "save_decoded.json"
        json.dump(obj, open(out, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
        print("wrote", out)
    elif cmd == "pack":
        obj = json.load(open(sys.argv[2], encoding="utf-8"))
        target = resolve_save(sys.argv[3])
        n = write_save(target, obj)
        print(f"wrote {n} bytes to {target} (backup at .bak)")
    else:
        print("unknown command:", cmd)
