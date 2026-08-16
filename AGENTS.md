# Working in this repo — handoff notes for AI assistants

This repo holds **two separate deliverables** for *Eiyuden Chronicle: Hundred Heroes*
(Steam). Read this file first; it has the context that isn't written down anywhere else
— machine-specific paths, hard-won lessons, and what's actually done vs. still broken.
Each sub-project's own README has the deep detail once you know which one you're in.

1. **The save editor** (`editor/`) — a finished, shipped, stdlib-only Python web app that
   edits save files directly. Public repo:
   https://github.com/TheSparda/Eiyuden-Chronicle-Save-Editor. See [README.md](README.md).
2. **SpardaECFixes** (`SpardaECFixes/`) — an in-progress BepInEx IL2CPP mod that patches
   the running game's compiled code to make certain Cheat-Engine-table cheats permanent,
   each behind its own config toggle. **Not yet committed to git** (check `git status` —
   it's untracked) and not yet pushed anywhere. See [SpardaECFixes/README.md](SpardaECFixes/README.md).

They share almost no code and can be worked on independently. Figure out which one the
task is actually about before touching anything.

## Hard rules (apply to both sub-projects, no exceptions)

- **Never commit personal data**: SteamID64, real save contents, anything under `dump/`.
  Already gitignored — keep it that way, and double-check `git status`/`git diff` before
  any commit for anything that looks like it snuck in regardless.
- **Never add a "Co-Authored-By: Claude" (or any AI-attribution) trailer** to a commit or
  PR body, on this project or any other. This is a standing instruction from the repo
  owner, not a one-off.
- **Never put the save file encryption methodology in the save editor's public
  `README.md`.** The editor's README documents *behavior*, not the crypto internals.
  This restriction is specific to that one file — SpardaECFixes's README is technical
  documentation for a mod that only runs locally and can be as detailed as it needs to
  be.
- **Never write unverified data.** The save editor's core discipline: any write is
  preceded by a `.bak`, and confirmed by decrypting what was just written and comparing
  it against what was meant to be saved, before anything on disk is touched. Preserve
  this if you extend `editor/ecsave.py`'s write path.

## Machine-specific paths (this development machine)

```
Game install:      F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\
BepInEx plugins:   F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\BepInEx\plugins\SpardaECFixes\SpardaECFixes.dll
BepInEx config:    F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\BepInEx\config\sparda.eiyudenchronicle.ecfixes.cfg
BepInEx log:       F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\BepInEx\LogOutput.log   (grows large fast — grep, don't Read whole)
Live CT copy:      F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\BepInEx\config\Eiyuden Chronicles\EiyudenChronicle.CT
Save files:        %LOCALAPPDATA%Low\505 Games S_p_A\EiyudenChronicle\<steamid64>\SaveData\
```

The community Cheat Engine table (`.CT`) is the ground-truth reference for every
SpardaECFixes patch. A copy lives at `reference/EiyudenChronicle.CT` in this repo
(gitignored — third-party material, not ours to redistribute). **If the user mentions
updating their live CT, or a search of the local reference copy comes up empty for a
feature that plausibly exists, re-copy the live one over the local reference copy** —
it has been meaningfully fuller than the local copy at least once already this project
(the local copy was ~2000 lines / 107 entries; the live one is 10,625 lines with entries
the local copy was missing entirely). Command:

```bash
cp "F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\BepInEx\config\Eiyuden Chronicles\EiyudenChronicle.CT" "reference\EiyudenChronicle.CT"
```

## SpardaECFixes: build/deploy/test loop

```bash
cd SpardaECFixes
dotnet build -c Release          # requires .NET 6 SDK; edit <GameDir> in the .csproj if the game moves
cp bin/Release/net6.0/SpardaECFixes.dll "F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\BepInEx\plugins\SpardaECFixes\SpardaECFixes.dll"
echo "" > "F:\SteamLibrary\steamapps\common\Eiyuden Chronicle\BepInEx\LogOutput.log"   # clear before each test so new output is unambiguous
```

Then the user launches the game and plays for a bit. Grep the log afterward — do not
`Read` the whole file, it can reach several MB in one session:

```bash
grep -n "SpardaECFixes\|Harmony\|Exception\|patched\|failed" "F:\...\BepInEx\LogOutput.log"
```

Startup always logs one line per native patch (`"<Fix>: patched N bytes at ..."` on
success, or `"expected instruction sequence not found ... -- NOT patching"` on failure —
every native patch here fails safe and writes nothing rather than guessing) and one
summary line for the whole plugin. Diagnostic `[diag]`-prefixed log lines have been used
repeatedly this project to prove whether a patched method is even being called, and with
what values — add them liberally when a fix is suspected broken, they're cheap to add
and remove.

## The one lesson that mattered most this project

**A Harmony patch applying cleanly, with no error, proves it reaches its own target
method — it proves nothing about whether that method is the one that actually matters.**
Three separate fixes this project were built by reflection (find a plausibly-named
method, patch it, confirm Harmony doesn't throw) and every one of them turned out to be
patching the *wrong* method — silently. `GameData.Party.GetBattleFinishHealRate`
clamped its return value correctly on every call and had zero effect on the actual
post-battle heal, because the game's real heal application (`Battle.StateVictory.
ApplyBattleFinishHeal`) doesn't consult that getter. `TryRandomStart`'s Harmony patch
gated on a managed `IsValid` property that was `False` on every real call, when the
actual game logic checks a raw pointer directly. The `SupportUnitID` getter fires
constantly but turned out to be a completely unrelated property — its real values (1, 4,
5, 6, 7, 8, logged over an entire play session) never once matched the "nothing
assigned" sentinel it was supposedly reading.

**The fix, every time, was the same: stop trusting reflection-based name-matching, and
cross-check against the community CT's own decoded AOB signature for the same feature.**
The CT's author already reverse-engineered the *actual* target and instruction sequence
for a huge number of features — treat it as ground truth, not just inspiration. Where a
CT entry exists for a feature, decode its `aobscanregion`/`aobscanmodule` call to find
the real target type+method, and match your patch to that — don't assume a
similarly-named managed method is equivalent. See the top-of-file comment in
`SpardaECFixes/Plugin.cs` for the fuller version of this rule.

## Native patching techniques established in `SpardaECFixes/Plugin.cs`

All in the `NativePatches` static class, each documented in-place with a comment
explaining what it targets and why. Reuse these rather than reinventing:

- **`ResolveNativeCode(Type, methodName, log)`** — the standard way to get a compiled
  method's real code address: read its interop-generated
  `NativeMethodInfoPtr_<name>_*` static field (the same `Il2CppMethodInfo*` the game's
  own bindings use) and dereference offset 0 (`methodPointer`).
- **In-place same-length neutralize** (`PatchAlwaysFormParty`, `PatchLargeUnitsTakeOneSlot`) —
  when the whole fix fits in the same number of bytes as what's being replaced, no
  trampoline is needed at all. Simplest and safest option; use when possible.
- **Trampoline with indirect absolute jump/call** (`PatchCollectionPointBonus`,
  `PatchRohanHeal`) — `FF 25 00000000` + inline 8-byte pointer (jmp), `FF 15 00000000` +
  inline 8-byte pointer (call), for when new logic needs inserting rather than a
  same-length swap. **Never use a relative jump (E9/EB) to reach `VirtualAlloc`'d
  memory** — confirmed by testing that a plain `VirtualAlloc` can land ~138 trillion
  bytes from the target module, far outside the ±2GB a relative jump/call can reach.
  Read any wildcarded bytes (a call's rel32, a mov's RIP-relative disp32) from the
  matched bytes at runtime, never hardcode them, and recompute absolute addresses rather
  than relocating RIP-relative instructions unchanged (RIP means something different
  once code is copied to different memory).
- **Full function-body replacement** (`PatchTryRandomStart`, `PatchLargeUnitsTakeOneSlot`) —
  when the CT's own AOB targets a function's prologue rather than a mid-function
  instruction, it means the CT replaces the *whole* method. No resume address is needed
  since nothing past entry is ever executed again; safe to overwrite more bytes than the
  matched signature as long as the method has real work in it (comfortably longer than
  the ~14 bytes an indirect jump needs).
- **Managed callback from native code** (`PatchTryRandomStart`'s `RollSupportSkillChance`) —
  when a trampoline needs something native code can't easily do (RNG, in this case), mark
  a static method `[UnmanagedCallersOnly]` and take its address with C# function-pointer
  syntax (`delegate* unmanaged<byte> fn = &Method; var ptr = (IntPtr)fn;` — requires
  `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the `.csproj`, already set). Call it
  from the trampoline via the same indirect-call pattern as any other native call,
  padding the stack with `sub rsp,0x28` / `add rsp,0x28` (shadow space) around it.
  Preferred over `Marshal.GetFunctionPointerForDelegate` — no delegate object to keep
  alive, no GC lifetime concerns, stable native entry point for the process lifetime.
- **Fail-safe, always**: every native patch logs an error and writes *nothing* if its
  expected signature isn't found (game update, wrong build, etc.) — never fall back to a
  guessed offset. Keep this property in anything new.

## Current status of SpardaECFixes's six fixes

| Fix | Mechanism | Status |
|---|---|---|
| Always Form Party At Save Points | native, in-place | ✅ working, user-confirmed in-game |
| Always Have Collection Point Bonus | native, trampoline | ✅ patches cleanly; the specific in-game collection-point interaction was never explicitly re-confirmed after the trampoline rewrite |
| Always Have Rohan Heal Bonus | native, trampoline | 🆕 just re-targeted to the CT's real target (`Battle.StateVictory.ApplyBattleFinishHeal`) after the original Harmony-based version was proven disconnected from the real heal. Builds clean, deployed, **awaiting user in-game test** |
| Random Support Skills Always Activate | native, full-body replace + managed callback | 🆕 same situation — re-targeted to the CT's real target (`TryRandomStart`'s actual pointer check) after the Harmony version was proven never to fire correctly. Builds clean, deployed, **awaiting user in-game test** |
| Large Units Take One Party Slot | native, in-place | 🆕 brand new feature (CT id 410, `IsLSize` full-body replace). Builds clean, deployed, **awaiting user in-game test** |
| Default Support Unit When Blank | Harmony (still broken) | ❌ confirmed broken — the patched `SupportUnitID` getter is the wrong method entirely (see lesson above). Not yet re-fixed. |

## Known pending work (also tracked in this session's task list)

1. **Fix Default Support Unit properly.** The CT's real mechanism (`reference/EiyudenChronicle.CT`,
   entry "Make Support Unit Perrielle If Nothing Set...", id 385) is a raw native field
   poll, not a getter: `UserDataContainerPtr -> +0x208 (Current) -> +0x20 (_party) -> +0x20
   (_supportUnitID)`, written directly if it reads the "empty" sentinel `0xFFFFFFFF`. Two
   viable approaches, neither attempted yet:
   - Find a clean **managed** reflection path to the live `GameData.Party` instance (via
     some game singleton) and identify its *real* backing field for the support unit id
     — the property this project hooked before was proven to be a different, unrelated
     getter, so don't trust a similarly-named field without verifying it against
     observed values first (log it and watch what it does before wiring up a fix).
   - Or replicate the CT's own native pointer-chase, including however it captures
     `UserDataContainerPtr` in the first place (the CT's base "Activate Trainer" entry
     hooks `FieldStage.UI.MainMenu.Initialize` to capture it into a global — see the very
     first `<CheatEntry>` in the CT file for the full mechanism).
2. **Implement "Can Attack Any Enemy From Any Row"** (CT id 445) — the largest remaining
   item. It patches *six* separate methods (`CommandValidation.IsReachableRange` ×3
   overloads, `IsAttackableArea` ×2 overloads, `CommandValidation.GetSkillRange`), and
   **all six depend on a shared `CurrentBattleUnitList`/`TotalBattleUnits` ally-tracking
   table** that the CT populates via an entirely separate, always-on hook (its base
   "Activate Trainer 2.2" entry — `GameManager.Update` + `Battle.Context.Terminate`
   hooks, see CT id 3). None of that tracking infrastructure exists in SpardaECFixes
   yet. Build it first, then the six trampolines on top of it. Scope/sequence this with
   the user before starting — it's substantially bigger than everything else in this
   project combined.
3. **Save editor**: see its own README's "What's deferred" section (scenario flags,
   bulk unit editing, a proven recruit flag) — smaller, independent, well-scoped items.

## Where to look for more

- `SpardaECFixes/Plugin.cs` — the whole mod; read the top-of-file comment and each
  `NativePatches` method's doc comment before changing anything.
- `SpardaECFixes/README.md` — user-facing feature docs and full "how it works" writeups
  for every native patch, including failure histories worth reading before repeating a
  mistake (`Always Form Party At Save Points` alone took three attempts).
- `SpardaECFixes/SUPPORT_ABILITIES.md` — all 26 support units, their character ids, and
  which ones have a fix; useful when picking the next support-ability feature to tackle.
- `reference/EiyudenChronicle.CT` — ground truth for basically everything above. Search
  it by `<Description>` text or character name before reverse-engineering anything from
  scratch; there's a good chance the community table already solved it.
- `README.md` (repo root) — the save editor's full user-facing documentation.
