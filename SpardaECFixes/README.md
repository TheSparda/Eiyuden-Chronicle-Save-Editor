# SpardaECFixes

A small [BepInEx](https://github.com/BepInEx/BepInEx) mod for *Eiyuden Chronicle: Hundred
Heroes* (Steam) — quality-of-life fixes, each one toggleable in a plain-text config file.

## Install

1. You need [BepInEx](https://github.com/BepInEx/BepInEx) (IL2CPP build) already set up
   for the game — if you're running any other mod, you already have this.
2. Drop `SpardaECFixes.dll` into `BepInEx/plugins/SpardaECFixes/`.
3. Launch the game once. This generates
   `BepInEx/config/sparda.eiyudenchronicle.ecfixes.cfg`.

Nothing modifies your save files — this only changes in-game behavior while running.

## Features

Six fixes so far, each independently toggleable. See
[SUPPORT_ABILITIES.md](SUPPORT_ABILITIES.md) for the full list of all 26 support units,
their character ids, and which ones these fixes cover.

### Always Form Party At Save Points

**Config:** `[Fixes] AlwaysFormPartyAtSavePoints = true` (on by default — **restart the
game to change it**, see [How it works](#how-it-works))

By default, "Organize Party" at a save point is a **Cassandra support-unit ability** —
it's only available when she's your currently assigned support unit
(`_heroParty._supportUnitID == 400`, her character id). Every other support unit leaves
field save points "save only." This fix makes the option available everywhere,
**regardless of your actual support unit** — swap party members, recruit new characters,
change your support unit as often as you like, none of it affects this fix. Confirmed
working end to end: the option appears at field save points, and using it opens party
formation normally with no side effects.

### Always Have Collection Point Bonus

**Config:** `[Fixes] AlwaysHaveCollectionPointBonus = true` (on by default — restart to
change)

Kerrin, Martha, Ormond and Pastole each grant a +100% resource rate at their own
collection-point category (logging, food, mining, hunting) when assigned as your support
unit. This makes that +100% permanent, everywhere, regardless of your actual support
unit. Applied as a native code patch (a trampoline, see [How it
works](#always-have-collection-point-bonus-1)) since it clamps a value used mid-function,
not something reachable at a normal method boundary.

### Always Have Rohan Heal Bonus

**Config:**
```
AlwaysHaveRohanHealBonus = true
RohanHealMinimumPercent = 20
```
(native code patch — **restart the game to change either setting**, see [How it
works](#always-have-rohan-heal-bonus-1))

Guarantees at least `RohanHealMinimumPercent`% of the party's HP is restored after every
battle — Rohan's own ability grants 20%, but this can be set to any value 0–100. Never
lowers a heal that would already be bigger; only raises one below the minimum.

### Random Support Skills Always Activate

**Config:**
```
RandomSupportSkillsAlwaysActivate = true
RandomSupportSkillChancePercent = 100
```
(native code patch — **restart the game to toggle the feature itself**, but
`RandomSupportSkillChancePercent` is read fresh on every check and takes effect
immediately, no restart needed for that one — see [How it
works](#random-support-skills-always-activate-1))

Perrielle, Cabana, Kurtz, Code L and Douglas each have a "sometimes appears before
battle" ability. This overrides the trigger chance to `RandomSupportSkillChancePercent`%
— **not added on top of the game's own hidden rate, but a full replacement of it**, so
the number in the config is the exact probability, not a bonus. 100 (default) means
always; 0 means never. Only takes effect when a support unit with a valid skill is
actually assigned — see the next fix if you don't want to assign one.

### Large Units Take One Party Slot

**Config:** `[Fixes] LargeUnitsTakeOneSlot = true` (on by default — restart to change)

Large-size units (e.g. Garoo, Vaught) take up only 1 party slot instead of 2, same as
any other character. Applied as a native code patch (a full-function replacement of the
game's own "is this unit large" check) since it's a self-contained, trivial method with
no meaningful body to preserve.

### Default Support Unit When Blank

**Config:**
```
DefaultSupportUnitWhenBlank = true
DefaultSupportUnitId = 70
```
(ordinary Harmony patch, no restart needed)

If you haven't assigned any support unit at all, treats `DefaultSupportUnitId` (defaults
to Perrielle, 70 — the community CT's own choice) as assigned, so the fixes above have
something to act on. Has no effect if you've assigned a real support unit yourself —
your actual choice always takes priority. Can be set to any recruited character's id
(see [SUPPORT_ABILITIES.md](SUPPORT_ABILITIES.md)); if the chosen character isn't
actually support-capable, this simply has no visible effect rather than erroring.

**Known issue:** testing showed this one does not currently work — the getter it
patches turned out to be unrelated to the actual support-unit assignment (see [When
this could stop working](#when-this-could-stop-working)). A proper fix requires either
finding a real managed path to the live save-data instance or replicating the CT's own
native pointer-chase, and hasn't landed yet.

## How it works

One fix (default support unit) is an ordinary Harmony patch on a substantial, real
method (the `SupportUnitID` getter) — it applies cleanly, but **testing showed it
doesn't actually work**; see its entry above and [When this could stop
working](#when-this-could-stop-working). The other five are native code patches, for two
different reasons: some target a check that's inlined into its caller (no real call for
Harmony to intercept at all), and two others (Rohan's heal, random support skills) were
*first* attempted as Harmony patches on plausibly-named methods found by reflection, and
diagnostic logging proved those methods — despite applying cleanly with no error — were
disconnected from what the game actually runs. Every fix below now matches the
community Cheat Engine table's own decoded target exactly, rather than a
reflection-based guess. Their stories are worth documenting in full:

### Always Form Party At Save Points

This one took three attempts, worth documenting because the failures are as informative
as the fix:

- **v1** patched `FieldStage.FacilitySavePoint.Initialize()` with a Harmony postfix,
  forcing the derived `_isSaveOnly` field to `false` after the method returned. It
  applied without error but **left the player unable to move** — the postfix ran after
  Initialize had already skipped setting up an `InnCanvas` and a callback (under the
  assumption the ability wasn't active), and overriding the output afterward didn't undo
  that skipped setup.
- **v2** patched the private `IsAssinedUnitSupportCassandra` getter instead, forcing it
  to always return `true`. Applied cleanly, caused no crash — and had zero effect. The
  comparison this depends on turned out to be **inlined directly into `Initialize()`'s
  compiled code** (a raw `cmp` instruction, not a call to the getter), which is common
  for trivial IL2CPP/AOT-compiled property getters. Nothing at runtime ever actually
  called that getter at the relevant point.

Both v1 and v2 are Harmony prefix/postfix patches, which can only intercept a method
*before it starts* or *after it returns* — a boundary-level technique. Neither can reach
*inside* a method to change a decision that's already inlined into its machine code.

- **v3** (shipping) replicates what the community's Cheat Engine table actually does:
  overwrites the compiled instruction **in place**, directly, the same way CT's own AOB
  scan + assembly injection works, just driven from C# instead of Cheat Engine:

  1. Reads `FieldStage.FacilitySavePoint`'s cached `NativeMethodInfoPtr_Initialize_*`
     static field — the same `Il2CppMethodInfo*` the game's own interop bindings use
     internally to call the method — and dereferences its first field (`methodPointer`,
     offset 0, stable across every IL2CPP version `Il2CppInterop.Runtime` supports) to
     get the real compiled code address.
  2. Scans up to 512 bytes in for the exact instruction sequence this patch targets:
     `cmp dword [rax+0x20], 0x190` (7 bytes) `; setne al` `; mov [rbx+0x48], al` — the
     same 13-byte signature CT's AOB scan uses. If it isn't found, **nothing is written**
     — see [When this could stop working](#when-this-could-stop-working).
  3. Overwrites just the 7-byte comparison with `cmp eax,eax` + 5 NOPs — same length (so
     nothing after it shifts), doesn't touch `eax`'s actual value (a `CMP` never writes
     back), and removes the original's memory read of `[rax+0x20]` entirely rather than
     adding one. It always sets `ZF=1`, so the unmodified `setne al` right after it always
     computes `al=0` — and because this happens *before* `Initialize`'s own logic runs
     (not after, like v1), every branch inside `Initialize` that depends on this result
     sees the same outcome the game's own code would produce if Cassandra really were
     assigned, including the setup v1 was missing.

  Applied once at startup, directly to the loaded process's memory — not persisted
  anywhere, so it re-verifies and re-applies fresh every launch.

### Always Have Collection Point Bonus

`GameData.CollectionPointRate.GetCount` computes the bonus by calling into another
function with the raw rate as an argument (`edi`); the CT's fix clamps that argument to a
100 floor *before* the call, so this needed a genuine trampoline, not a same-length
in-place swap like the save-point fix:

1. Same address-resolution technique as the save-point fix (the cached
   `NativeMethodInfoPtr_GetCount_*` field, dereferenced for the real code pointer).
2. Scans for the 14-byte signature (`xor r8d,r8d` / `mov edx,edi` / `mov ecx,ebx` /
   `call rel32` / `add eax,ebx`), treating the call's 4-byte relative displacement as a
   wildcard. Fails safe exactly like the save-point fix if it's not found.
3. Reads the *actual* call target from the matched bytes at runtime (never hardcoded),
   and allocates a small executable trampoline holding: replay `xor r8d,r8d`, clamp
   `edi` to a 100 floor, replay `mov edx,edi` / `mov ecx,ebx`, call the real target via
   an indirect absolute call (`FF 15` + an inline 8-byte pointer — not a relative call,
   since a plain `VirtualAlloc` can land anywhere in a 64-bit process's address space,
   often far outside the ±2GB a relative call/jump can reach; confirmed by inspecting the
   actual allocated addresses in testing, which differed from the target by roughly 138
   trillion bytes), replay `add eax,ebx`, then jump back to resume via the same indirect
   technique.
4. Overwrites the full 14-byte matched region with an indirect jump to that trampoline —
   sized to fit exactly, no padding.

Applied once at startup; fails safe the same way if the signature isn't found.

### Always Have Rohan Heal Bonus

The first attempt at this fix Harmony-patched `GameData.Party.GetBattleFinishHealRate`,
found by reflection because its name matched. It applied without error and its postfix
clamped a raw result of 0 up to 20 on both real post-battle calls captured during
testing — and had zero visible effect in-game, because that getter isn't in the code
path the game actually uses to apply the heal. The community CT's own target is a
different method entirely: `Battle.StateVictory.ApplyBattleFinishHeal`, right where it
computes the heal percent and decides whether to apply it at all:

```
mov r14d, eax        ; store the raw heal percent
test eax, eax
jle <skip healing>    ; eax <= 0 means no heal this time
mov rcx, [rip+disp]   ; (continues into applying the heal)
```

`eax` holds the raw percent at exactly this point. The fix clamps it to the configured
minimum *before* any of this runs:

1. Same address-resolution technique as the other native patches (the cached
   `NativeMethodInfoPtr_ApplyBattleFinishHeal_*` field), scanning for this 18-byte
   signature (the `mov rcx,[rip+disp]`'s displacement wildcarded, everything else
   matched against the CT's own decoded bytes).
2. A trampoline clamps `eax` to `RohanHealMinimumPercent` if it's lower, then replays
   `mov r14d,eax` and `test eax,eax` on the clamped value, then a `jg`/indirect-jump pair
   standing in for the original `jle` (needed only if the minimum is configured to 0 —
   any positive minimum means this branch is never taken).
3. The trickiest part: relocated code can't reuse the original's RIP-relative
   `mov rcx,[rip+disp32]` unchanged, since "here" means something different once this
   instruction is copied into separately allocated memory. The fix computes the absolute
   address that instruction pointed at (at patch time, from the matched bytes) and
   replays the same effect with `movabs rcx,<address>` followed by `mov rcx,[rcx]`
   instead.
4. Overwrites the full 18-byte matched region with an indirect jump to the trampoline,
   padded to length with NOPs — same fail-safe behavior as every other native patch here
   if the signature isn't found.

`RohanHealMinimumPercent` is baked into the trampoline as an immediate at patch time, so
changing it requires a restart — unlike the chance percentage in the next fix, which is
read live.

### Random Support Skills Always Activate

Same story as the heal fix: a first attempt Harmony-patched
`Battle.Command.SupportSkillState.TryRandomStart` and gated on its managed `IsValid`
property. It applied cleanly, but `IsValid` was `False` on both real calls captured
during testing, so the "always activate" branch never ran — the CT's actual check reads
a raw pointer at `[rcx+0x10]` directly, a different condition than the managed property
this assumed was equivalent.

The CT's target here is the whole function body, not one instruction inside it — its
own AOB matches `TryRandomStart`'s prologue. This fix does the same, as a full
function-body replacement (no resume address needed, since nothing after entry is ever
reached again):

```
cmp qword [rcx+0x10], 0
jne <has a valid skill>
xor eax, eax   ; ret false — nothing to activate
<has a valid skill>:
<call back into managed code for the chance roll, ret its result>
```

Unlike the CT (which hardcodes "always activate" once a valid skill is found), this
calls back into a small managed method (marked `[UnmanagedCallersOnly]`, giving it a
stable native entry point with no delegate/GC lifetime concerns) that rolls
`RandomSupportSkillChancePercent` fresh every time — native code has no convenient RNG,
and this way the percentage takes effect immediately, no restart required, unlike every
other configurable *number* in this plugin.

### Large Units Take One Party Slot

The simplest native patch here: `MasterDataExtension.UnitParamExtensions.IsLSize` is a
small, self-contained check with nothing worth preserving, so this replaces its entire
body in place — `xor eax,eax` / `ret` (always "not large") — after verifying its
6-byte prologue (`push rbx` / `sub rsp,0x20`) matches, the same signature the CT itself
scans for. No trampoline, no allocation: the replacement is short enough to fit directly
in the matched region, padded with NOPs.

### When this could stop working

The patch depends on that exact 13-byte instruction sequence existing somewhere in the
first 512 bytes of `Initialize()`'s compiled code. In testing, it was found 49 bytes
further into the function than the community CT's own 100-byte scan window assumed — the
same logic, just preceded by more code (a static-init guard, a couple of calls) in this
build than whatever build the CT's author had. That's not a version *mismatch* so much as
normal compiler/build variance; the search handles it by scanning a range rather than
assuming a fixed offset.

If a future game update restructures this method enough that the exact byte sequence
moves outside that window, or changes shape entirely (different registers, different
instruction encoding), the patch **fails safe**: it logs `"expected instruction sequence
not found near Initialize() -- NOT patching"` and writes nothing. In-game, this means
"Organize Party" quietly stops appearing at field save points — not a crash, not
corruption, just a silent revert to vanilla behavior. Check
`BepInEx/LogOutput.log` for that message if the fix seems to have stopped working.

Every other native patch in this plugin (collection point bonus, Rohan's heal, random
support skills, large units) is checked the same way — its own signature, its own scan
range, same fail mode, same log-and-bail behavior — and degrades the same way: the
feature quietly stops applying rather than anything breaking.

The one remaining Harmony-based fix (default support unit) degrades differently: if a
game update renames or changes the signature of the `SupportUnitID` getter, Harmony
itself fails to apply that specific patch and logs an error at startup (`"{Name} failed
to apply Harmony patches"`) — the other patches are unaffected, since each is isolated
from the others' failures. As documented above, this one doesn't currently work anyway
regardless of that getter's signature — testing showed it's the wrong target entirely.

## Building from source

Requires the .NET 6 SDK. Edit `<GameDir>` in `SpardaECFixes.csproj` if your game isn't
installed at the path currently set there, then:

```bash
dotnet build -c Release
```

The output DLL lands in `bin/Release/net6.0/SpardaECFixes.dll`.
