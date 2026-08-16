# Eiyuden Chronicle: Hundred Heroes — Save Editor

A **save editor** for the Steam (PC) release of *Eiyuden Chronicle: Hundred Heroes*. Edit
money, difficulty, your roster, character stats and gear, and your inventory — then have
the save written back so the game loads it normally.

It runs as a small local web app: **stdlib-only Python**, no install, no dependencies,
nothing uploaded. Built in the same spirit as the
[Suikoden IV Editor](https://github.com/TheSparda/Suikoden-4-Save-Editor) it's modeled on:
**never write unverified data.**

---

## Run

Requires **Python 3.8+** and a modern browser.

- **Windows:** double-click `Start Editor (Windows).bat`
- **Terminal:** `cd editor` then `py eceditor.py`

Then open `http://127.0.0.1:8751`.

> ### Two ways your edits can be undone
>
> 1. **Close the game before writing.** Hundred Heroes keeps save state in memory and
>    overwrites the file when it next saves or exits.
> 2. **Steam Cloud can replace a file on launch.** The editor shows each save's cloud
>    state and warns you; see [Steam Cloud](#steam-cloud).

Every write makes a `.bak` first, and nothing is written unless it survives a
decrypt-and-compare check.

---

## The interface

Pick a save on the left — each is labelled with level, playtime, size, when it was saved,
and its Steam Cloud state. Then work through four tabs:

### Overview
Money, playtime, New Game+ count, fortress town level and population, and **difficulty**
(Normal / Hard plus the five modifier toggles: no battle money, no recovery items in
battle, doubled MP/SP costs, no escaping, hyper inflation).

### Characters
- **Roster** — all 121 characters, recruited or not, filterable, with *Recruit all*.
- **Units** — per character: EXP, HP, MP, weapon level, four equipment slots and their
  rune holes. Equipment and runes are chosen **by name**; each slot only offers gear that
  belongs in it.

### Inventory
Stacks **grouped by category** (Medicine, Runes, Cards, Beigoma, Town resources, …) with
per-group counts, collapsed by default — a finished save carries several hundred stacks.
Filter across everything, edit counts, remove stacks, or add new items with a
category-narrowed picker.

### Raw JSON
The entire decoded save. The payload is plain JSON, so this genuinely reaches
*everything* — flags, scenario state, minigames, achievements.

### Throughout
- **Names, not numbers** — 1,142 items, 179 runes and 121 characters by name. Fields still
  accept a raw id.
- **Edit affordances** — changed fields are highlighted and get a ↺ to restore the original
  value; the toolbar shows an unsaved-change count and a **Revert all**.
- **Fast** — a 269 KB save opens in ~8 ms, and stays cached for the session.

---

## Command line

```bash
cd editor
py ecsave.py list                       # find saves, with level and playtime
py ecsave.py show UserData7.dat         # summary
py ecsave.py dump UserData7.dat out.json
py ecsave.py pack out.json UserData7.dat
py cloudcheck.py                        # Steam Cloud sync status
```

Save arguments may be a bare filename or a full path.

---

## How it works

### Save location

```
%LOCALAPPDATA%Low\505 Games S_p_A\EiyudenChronicle\<steamid64>\SaveData\
```

`UserDataN.dat` are the save slots, `UserDataInfo.dat` is the slot index the title screen
reads (that's where the level/playtime labels come from), `SystemData.dat` holds settings.

### Save format

Each file is an encrypted blob wrapping UTF-8 JSON. The editor handles that transparently,
so everything below is expressed in terms of the decoded JSON.

There's no checksum to repair — the only integrity requirement is that a file decrypts to
valid JSON. `write_save` therefore verifies its own output by decrypting what it just
produced and comparing it against the data it meant to save, *before* replacing anything
on disk.

Encryption and decryption were confirmed in both directions against captured
plaintext/ciphertext pairs, and real saves round-trip byte-for-byte — the editor emits
exactly what the game itself would have written. Decryption uses a native OS path where
available (`ctypes` is stdlib, so this costs nothing in dependencies), with `pydes.py` as
the portable fallback; the self-tests assert the two agree byte-for-byte.

### Difficulty

`_difficultyData._difficulty`: **0 = Hard, 1 = Normal.**

*Which field* came from a controlled diff — two saves from the same playthrough 11 minutes
apart, one Hard and one Normal, differed in only four leaves across the entire save, and
the only difficulty-related one was `_difficulty`.

*Which value* is the reverse of the natural guess ("0 is the default, and the default is
Normal"). Two independent lines of evidence say otherwise: the ground-truth pair above,
where the Hard save holds `0`; and the game emitting its localization literals in enum
order, `Option_Difficulty_Setting_Hard` before `..._Normal`.

### Roster and recruitment

A character is recruited exactly when they hold a record in `_unitData._units` — an early
save has 6, a finished one 120.

When recruiting, the record written matches the game's own: the same 15 keys, and **the
right number of rune holes for that character**. Hole count is fixed per character (Nowa
7, Garr 4, and 49 characters have none) and doesn't grow with level, so it can't be
guessed — it comes from `ec_unit_runeholes.json`, harvested from a save with the full
roster. All 19 local saves agreed on every character, and `test_roster.py` cross-checks a
reconstructed record against a save where that character was genuinely recruited.

Recruiting has been tested and confirmed working in-game — a character added here
behaves identically to one recruited normally. Removing is fully reversible too.

The player character and anyone currently placed in a party are **protected** — their
checkbox is disabled, because dropping them risks a save the game can't load.

**Role badges.** Every character is tagged Battle, Support, Hybrid, or Castle (neither),
filterable in the Recruit tab and shown in Characters. Unlike rune holes or stats, this
genuinely isn't derivable from any save — there's no field for it. It comes straight from
the game's own `UnitParamTable` (`CanBattle`/`CanSupport` plus the raw `UnitType` enum),
captured via the same runtime-hook approach used to recover the encryption key, this time
against a save with the full roster recruited. All 121 known characters are covered, and
the raw enum agrees with the derived flags for every one of them (`test_roles.py`).

### Equipment slots

Each slot only offers gear that belongs in it. The mapping comes from tabulating every
`(slot, item id)` pair the game itself has written across all local saves — 79 items
observed, with **no item ever appearing in two different slots** — and reading off the id
layout:

| ids | slot |
|---|---|
| 6000–6199 | Head (helmets, hats, masks) |
| 6200–6299 | **interleaved** Head *and* Body |
| 6300–6599 | Body (armour, mail, robes) |
| 6600–6699 | Hands (shields) |
| 6700–6835 | Accessory (badges, brooches, bangles) |

The 6200–6299 block alternates head/body in pairs (Headband / Cloth Wear, Scarf /
Traveler's Clothes, …). It's *almost* "even = head, odd = body", but not exactly — 6233
"Crown of Guile" is odd yet the game equips it on Head — so parity isn't used. Observation
decides for gear we've seen equipped; anything unobserved in that block is offered in
**both** lists rather than hidden.

Nothing is truly locked out: the fields accept a raw id, so filtering can never block a
legitimate edit.

### Item categories

The 1,142 items fall into clean id blocks, and the labels below are read off what the
names in each range actually are — every item lands in one, with none left over:

| ids | category | ids | category |
|---|---|---|---|
| 1000s | Medicine | 20000s | Race eggs |
| 3000s | Dishes | 50000s | Town resources |
| 4000–5999 | Runeshards | 51000s | Materials |
| 6000–6699 | Equipment | 52000s | Ingredients |
| 6700–6999 | Accessories | 70000s | Special items |
| 7000s | Runes | 71000s | Cards |
| 8000s | Recipes & scripts | 72000s | Beigoma |
| 9000s | Valuables | 98000s | Quest items |
| 10000s | Trade goods | 99000s+ | Tools & keys |
| 12000s | Decorations | | |

Adding an item uses the stack maximum the game itself writes for it, splitting oversized
requests across stacks the way the game does (20 Healing Herbs → 6+6+6+2).

### Steam Cloud

Hundred Heroes uses **Steam Auto-Cloud**: the game makes no cloud calls itself — Steam
syncs the files on its behalf, driven by a manifest at

```
<Steam>\userdata\<accountid>\1658280\remotecache.vdf
```

recording each save's size, SHA1 and local/remote timestamps. Steam reconciles it when a
game starts and when it exits. (It has also been observed syncing on its own with the game
closed, but not reliably — which is exactly why the editor checks rather than assumes.)

Two consequences: an edited save is only safe once Steam has uploaded it, and a local save
*older* than the cloud copy can be replaced on next launch.

The editor shows each save's state as a badge, warns you when the open save is at risk,
and has a ⟳ to re-check on demand. `cloudcheck.py` reports the same from the terminal and
exits non-zero if anything is at risk:

| state | meaning |
|---|---|
| `in sync` | local matches what Steam last synced |
| `LOCAL NEWER` | edited since; Steam should upload on next launch |
| `CLOUD NEWER` | **at risk** — launching may overwrite your edit |
| `MISSING locally` | tracked by Steam but not on disk |

The reliable workflow: quit the game → edit → check every touched file reads **LOCAL
NEWER** → start the game through Steam. If a **Cloud Conflict** dialog appears, choose the
**local / "Upload to Steam Cloud"** option; picking the cloud copy discards your edits.

To take Steam out of the loop entirely: **Steam → Library → Eiyuden Chronicle: Hundred
Heroes → Properties → General → uncheck "Keep game saves in the Steam Cloud."** Local
files then always win. (Anything already uploaded stays in the cloud, so re-enabling it
later can resurrect an old save.)

Note Auto-Cloud syncs *every* save-shaped file in that folder — including scratch copies
you leave there.

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
build_names.py             builds the name, stack-max, equip-slot and rune-hole tables
analyze_equip.py           working notes: how the equipment slot ranges were derived
testutil.py                locates saves for the tests (no hardcoded paths)
test_*.py, verify_py.py    self-tests (see below)
plugin/                    companion mod used to extract the name tables
ec_item_names.json         1142 item ids -> names
ec_item_maxes.json         observed per-item stack maxima
ec_equip_slots.json        observed item -> equipment slot
ec_unit_names.json         121 unit ids -> character names
ec_unit_runeholes.json     per-character rune-hole counts
ec_unit_roles.json         per-character Battle/Support/Hybrid/Other classification
```

Not in the repo, by design: `dump/` (captured plaintext saves — they contain the player's
SteamID64 and full save contents) and `reference/` (the Suikoden IV editor clone and the
community Cheat Engine table, neither ours to redistribute). `build_names.py` regenerates
the name tables from a local copy of the CT.

---

## Self-tests

```bash
cd editor
py pydes.py            # cipher self-tests against published vectors, both backends
py verify_py.py        # crypto against captured game data; real saves round-trip
py test_write.py       # full edit -> write -> read-back cycle on a scratch copy
py test_difficulty.py  # difficulty mapping against ground-truth Hard/Normal saves
py test_items.py       # inventory add/remove, stack splitting, equipment naming
py test_roster.py      # recruiting: record shape, rune holes, removal guards
py test_roles.py       # role classification: coverage and internal consistency
py test_cache.py       # session cache (needs the editor running)
```

Each works on a scratch copy and cleans up after itself; none modify your real saves.

---

## What's deferred (and why)

- **Scenario flags.** `_flagData._flags` and `_scenario` drive story progression; editing
  them can soft-lock a save, so they're reachable only through the Raw JSON tab.
- **Bulk unit editing.** Setting EXP or gear across 120 characters at once is the obvious
  next convenience, and is built entirely on fields already verified here.

## Credits

Architecture and the "never write unverified data" approach follow
[TheSparda/Suikoden-4-Save-Editor](https://github.com/TheSparda/Suikoden-4-Save-Editor).
Item and character names derive from the community Cheat Engine table and the game's own
data. Save files and game assets are **not** included in this repository.
