# Eiyuden Chronicle: Hundred Heroes — Save Editor

A **save editor** for the Steam (PC) release of *Eiyuden Chronicle: Hundred Heroes* — edit
money, playtime, unit stats, equipment, and inventory, then have the save re-encrypted so
the game loads it normally. It runs as a small local web app (stdlib-only Python, no
install, nothing uploaded), in the same spirit as the
[Suikoden IV Editor](https://github.com/TheSparda/Suikoden-4-Save-Editor) it is modeled on:
**never write unverified data.**

---

## Run

Requires **Python 3.8+** and a modern browser.

- **Windows:** double-click `Start Editor (Windows).bat`
- **Terminal:** `cd editor` then `py eceditor.py`

Then open the printed `http://127.0.0.1:8751`.

> **Close the game before writing.** Hundred Heroes keeps save state in memory and will
> overwrite your edits when it next saves or exits.

> **Steam Cloud can also undo your edits** — see [Steam Cloud](#steam-cloud) below, and
> run `py editor/cloudcheck.py` to see where each save stands.

## How to use

1. Pick a slot in the left-hand list (each is labelled with level, playtime and size,
   read from `UserDataInfo.dat`).
2. Edit any exposed field, or switch to the **Raw JSON** tab for full control.
3. Click **Write save**. A `.bak` is made before the first write to a file, the save is
   re-encrypted, and the result is decrypted again and compared before anything is
   replaced.

---

## What it edits

| Area | Fields |
|---|---|
| Progress | Money, playtime seconds, New Game+ count |
| Difficulty | Normal / Hard, plus the five modifier toggles |
| Fortress town | Town level, population |
| Units | EXP, HP, MP, weapon level, 4 equipment slots, 7 rune slots |
| Inventory | Count, max, **add** new items, **remove** stacks |
| Everything else | via the **Raw JSON** tab |

Because the decrypted payload is plain JSON, the Raw tab genuinely reaches *everything* —
flags, scenario state, minigames, achievements, the lot.

**Names, not numbers.** Equipment, runes and inventory are shown and picked by name
(1,142 items) — type to search, or enter a raw id. Adding an item uses the stack maximum
the game itself writes for that item, splitting oversized requests across stacks the way
the game does (20 Healing Herbs → 6+6+6+2).

**Slot-aware pickers.** Each equipment slot offers only gear that belongs there — helmets
in Head, armour in Body, shields in Hands, accessories in Accessory. See
[Equipment slots](#equipment-slots) for how the mapping was derived and where it stays
deliberately permissive.

**Edit affordances.** A changed field is highlighted and gets a ↺ button that restores its
original value; the toolbar shows an unsaved-change count and a **Revert all**.

**Difficulty.** `0 = Hard, 1 = Normal` — note this is the reverse of the obvious guess.
See "Difficulty" below.

---

## How it works (verified internals)

**Save location**

```
%LOCALAPPDATA%Low\505 Games S_p_A\EiyudenChronicle\<steamid64>\SaveData\
```

`UserDataN.dat` are save slots, `UserDataInfo.dat` is the slot index the title screen
reads, `SystemData.dat` holds settings. All use the same encryption.

**Save format.** Each file is an encrypted blob wrapping UTF-8 JSON. The editor handles
the encryption transparently — decrypting on open, re-encrypting on write — so everything
below is expressed in terms of the decoded JSON.

There is no checksum to repair: the only integrity requirement is that a file decrypts to
valid JSON. `write_save` therefore verifies its own output by decrypting what it just
produced and comparing it against the data it meant to save, before anything on disk is
replaced.

**Verification.** Encryption and decryption were confirmed against captured
plaintext/ciphertext pairs, in both directions, and real save files round-trip
byte-for-byte — the editor emits exactly what the game itself would have written. The
self-tests re-check this.

**Speed.** Decryption goes through the OS where possible (`ctypes` is stdlib, so this
costs nothing in dependencies), taking a 269 KB save from ~4.2 s to ~8 ms. `pydes.py`
remains the portable reference implementation and the fallback on other platforms, and the
self-tests assert the two agree byte-for-byte — a save written by one must be readable by
the other, and by the game. Decrypted saves are cached in memory for the session, so
reopening a slot is instant.

### Difficulty

`_difficultyData._difficulty`: **0 = Hard, 1 = Normal**, with five independent modifier
booleans (no battle money, no recovery items in battle, doubled MP/SP costs, no escaping,
hyper inflation).

*Which field* was established by a controlled diff: two saves from the same playthrough 11
minutes apart, one Hard and one Normal, differed in only four leaves across the entire
save, and the sole difficulty-related one was `_difficulty`.

*Which value* is the reverse of the natural guess ("0 is the default, the default is
Normal"). Two independent lines of evidence say otherwise: the ground-truth pair above
(the Hard save holds 0), and IL2CPP emitting the localization literals in enum order,
`Option_Difficulty_Setting_Hard` before `..._Normal`.

### Steam Cloud

Hundred Heroes uses **Steam Auto-Cloud**: the game makes no cloud API calls itself —
Steam syncs the save files on its behalf, driven by a manifest at

```
<Steam>\userdata\<accountid>\1658280\remotecache.vdf
```

which records every save's size, SHA1 and local/remote timestamps. Steam reconciles that
manifest **when a game starts and when it exits**. Two consequences:

- An edited save is only safe once Steam has uploaded it.
- A local save that is *older* than the cloud copy can be replaced on next launch.

There is no supported way to force an upload on demand — syncing is triggered by launching
or quitting the game. The reliable workflow is:

1. **Quit the game** (and ideally Steam) before editing.
2. Edit and write.
3. Run `py editor/cloudcheck.py` — every file you touched should read **LOCAL NEWER**.
4. Start the game through Steam. Steam sees the newer local files and uploads them.
5. If a **Steam Cloud Conflict** dialog appears, choose the **local / "Upload to Steam
   Cloud"** option — picking the cloud copy discards your edits.

`cloudcheck.py` (also built into the editor's sidebar) reports each tracked file as `in sync`, `LOCAL NEWER` (pending upload),
`CLOUD NEWER` (**at risk of being overwritten**), or `MISSING locally`, and exits non-zero
when anything is at risk.

To take Steam out of the loop entirely, turn cloud saves off for the title: **Steam →
Library → Eiyuden Chronicle: Hundred Heroes → Properties → General → uncheck "Keep game
saves in the Steam Cloud."** Local files then always win. (Anything already uploaded stays
in the cloud, so re-enabling it later can still resurrect an old save.)

Note that Auto-Cloud syncs *every* file matching the game's pattern in the save folder —
including stray copies you leave there. If you keep a scratch save like
`UserData10Test.dat` alongside the real ones, Steam will happily upload that too.

### Equipment slots

Each of the four slots is restricted to gear that belongs in it. The mapping comes from
tabulating every `(slot, item id)` pair the game itself has written across all local
saves — 79 items observed, with **no item ever appearing in two different slots** — and
reading off the resulting id layout:

| ids | slot |
|---|---|
| 6000–6199 | Head (helmets, hats, masks) |
| 6200–6299 | **interleaved** Head *and* Body |
| 6300–6599 | Body (armour, mail, robes) |
| 6600–6699 | Hands (shields) |
| 6700–6999 | Accessory (badges, charms, rings) |

The 6200–6299 block alternates head/body in pairs (Headband / Cloth Wear, Scarf /
Traveler's Clothes, …). It is *almost* "even = head, odd = body", but not exactly — 6233
"Crown of Guile" is odd yet the game equips it on Head — so parity is **not** used.
Observation decides for items we have seen equipped; anything in that block we have not
observed is offered in **both** the Head and Body lists rather than hidden.

Nothing is ever truly locked out: the fields accept a raw id, so an unlisted item can
still be entered deliberately. The filtering removes obviously-wrong categories without
being able to block a legitimate edit.

### Name tables

- **Items** (`ec_item_names.json`, 1142 entries) — extracted by `build_names.py` from the
  community Cheat Engine table's dropdown list. Covers 100% of the ids present in the
  saves tested.
- **Stack maxima** (`ec_item_maxes.json`) — harvested by `build_names.py` from the
  player's own saves, so a newly added stack matches what the game would write.
- **Character names** (`ec_unit_names.json`, 121 entries) — sourced from the game's own
  name lookup. Optional: without it the editor falls back to `#<id>`.

---

## Layout

```
Start Editor (Windows).bat   <- the only thing at the top level
README.md
editor/
```

Everything else lives in `editor/`:

```
ecsave.py                  save reader/writer + edit model  (also a CLI)
eceditor.py                web server + embedded UI
pydes.py                   portable pure-Python cipher implementation
winbcrypt.py               optional native cipher via Windows CNG (ctypes)
cloudcheck.py              Steam Cloud sync status (module + CLI; the editor uses it)
build_names.py             builds the item-name, stack-max and equip-slot tables
analyze_equip.py           working notes: how the equipment slot ranges were derived
testutil.py                locates saves for the tests (no hardcoded paths)
test_*.py, verify_py.py    self-tests (see below)
plugin/                    companion mod used to extract the name tables
ec_item_names.json         1142 item ids -> names
ec_item_maxes.json         observed per-item stack maxima
ec_equip_slots.json        observed item -> equipment slot
ec_unit_names.json         121 unit ids -> character names
```

Not in the repo, by design: `dump/` (captured plaintext saves — they contain the player's
SteamID64 and full save contents) and `reference/` (the Suikoden IV editor clone and the
community Cheat Engine table, neither ours to redistribute). `build_names.py` regenerates
the name tables from a local copy of the CT.

`pydes.py` uses precomputed SP-boxes and integer bit ops rather than per-bit lists, which
is what makes a 275 KB story save load in a few seconds instead of ~30.

### CLI

```bash
cd editor
py ecsave.py list                       # find saves, with level/playtime
py ecsave.py show UserData10.dat        # summary
py ecsave.py dump UserData10.dat out.json
py ecsave.py pack out.json UserData10.dat
```

---

## Self-tests

```bash
cd editor
py pydes.py        # cipher self-tests against published vectors
py verify_py.py    # against real captured game data
py test_write.py   # full edit -> encrypt -> disk -> decrypt cycle
```

---

## What's deferred (and why)

- **Character names.** Units are shown by numeric id. The id→name table lives in Unity
  localization assets, which aren't parsed yet — rather than ship a guessed mapping, ids
  are shown as-is.
- **Item / equipment names.** Same reason; ids only.
- **Recruiting characters.** Adding a unit means more than appending to `_units` (scenario
  flags gate recruitment), so it isn't offered as a one-click action. The Raw JSON tab is
  there for anyone who wants to experiment on a copy.
- **`SystemData.dat` editing.** It decrypts fine with the same key, but holds settings
  rather than save progress, so it isn't surfaced.

## Credits

Architecture and the "never write unverified data" approach follow
[TheSparda/Suikoden-4-Save-Editor](https://github.com/TheSparda/Suikoden-4-Save-Editor).
Save files and game assets are **not** included in this repository.
