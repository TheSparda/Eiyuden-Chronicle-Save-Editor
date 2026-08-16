using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

// Architecture note: each fix is a self-contained pair -- one ConfigEntry<bool> bound in
// Plugin.Load(), one patch reading it. An ordinary [HarmonyPatch] class
// (harmony.PatchAll() discovers them automatically) is simpler and safer than a raw
// native patch when it's actually reaching the right code -- but "applies without error"
// is not the same as "reaches the right code". GetBattleFinishHealRate and
// TryRandomStart were both Harmony patches found by reflection/name-plausibility, applied
// cleanly, and were BOTH wrong: diagnostic logging (see git history) proved neither one
// sat in the real execution path the CT's own decoded targets showed. A Harmony patch
// only proves it reaches ITS method; it proves nothing about whether that method is the
// one that actually matters. Before trusting a plain patch, verify its target against
// the CT's decoded AOB for the same feature if one exists -- don't just find a
// plausibly-named method and assume it's the right one.
//
// NativePatches (direct machine-code editing) is still required, separately from that
// trust issue, whenever the relevant check is inlined into its caller (no real call for
// Harmony to intercept at all) or when the CT's own target is a full function body
// rather than a single call boundary.
//
// Support-unit abilities do NOT share one generic mechanism worth hooking once for all
// of them -- each is still its own small reverse-engineering job. Cross-check the CT
// (reference/EiyudenChronicle.CT) for a matching entry before implementing a new one;
// where one exists, match its decoded target exactly rather than guessing.
namespace SpardaECFixes
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "sparda.eiyudenchronicle.ecfixes";
        public const string Name = "SpardaECFixes";
        public const string Version = "1.0.0";

        internal static new ManualLogSource Log;
        internal static ConfigEntry<bool> AlwaysFormPartyAtSavePoints;
        internal static ConfigEntry<bool> AlwaysHaveCollectionPointBonus;
        internal static ConfigEntry<bool> AlwaysHaveRohanHealBonus;
        internal static ConfigEntry<int> RohanHealMinimumPercent;
        internal static ConfigEntry<bool> RandomSupportSkillsAlwaysActivate;
        internal static ConfigEntry<int> RandomSupportSkillChancePercent;
        internal static ConfigEntry<bool> DefaultSupportUnitWhenBlank;
        internal static ConfigEntry<int> DefaultSupportUnitId;
        internal static ConfigEntry<bool> LargeUnitsTakeOneSlot;

        public override void Load()
        {
            Log = base.Log;

            AlwaysFormPartyAtSavePoints = Config.Bind(
                "Fixes",
                "AlwaysFormPartyAtSavePoints",
                true,
                "Lets you organize your party at any save point, not just the base camp. "
                + "By default this is a Cassandra support-unit ability (party "
                + "reorganization is only allowed when she's your assigned support unit); "
                + "this makes it available everywhere regardless of your actual support "
                + "unit. Applied once as a direct code patch at startup -- toggling this "
                + "requires restarting the game. Purely a convenience fix; it has no "
                + "effect on saved data.");

            AlwaysHaveCollectionPointBonus = Config.Bind(
                "Fixes",
                "AlwaysHaveCollectionPointBonus",
                true,
                "Grants the +100% collection point rate bonus at all times. By default "
                + "this is a support-unit ability shared by Kerrin, Martha, Ormand and "
                + "Pastole -- only active when one of them is your assigned support unit; "
                + "this makes the bonus permanent regardless of your actual support unit. "
                + "Applied once as a direct code patch at startup -- toggling this "
                + "requires restarting the game. Purely a convenience fix; it has no "
                + "effect on saved data.");

            AlwaysHaveRohanHealBonus = Config.Bind(
                "Fixes",
                "AlwaysHaveRohanHealBonus",
                true,
                "Guarantees a minimum party heal after every battle (see "
                + "RohanHealMinimumPercent for how much). By default this is a Rohan "
                + "support-unit ability; this makes the minimum apply regardless of your "
                + "actual support unit (it never lowers a bigger heal -- only raises one "
                + "below the minimum). Applied once as a direct code patch at startup -- "
                + "toggling this requires restarting the game. Purely a convenience "
                + "fix; it has no effect on saved data.");

            RohanHealMinimumPercent = Config.Bind(
                "Fixes",
                "RohanHealMinimumPercent",
                20,
                new ConfigDescription(
                    "How much of the party's HP is guaranteed to be restored after "
                    + "every battle, as a percentage (Rohan's own ability grants 20; set "
                    + "any value you like). Only applies when AlwaysHaveRohanHealBonus "
                    + "is true, and only ever raises the heal, never lowers one that "
                    + "would already be bigger. Baked into the same code patch as that "
                    + "setting -- restart the game to change it.",
                    new AcceptableValueRange<int>(0, 100)));

            RandomSupportSkillsAlwaysActivate = Config.Bind(
                "Fixes",
                "RandomSupportSkillsAlwaysActivate",
                true,
                "Overrides the trigger chance for 'sometimes appears before battle' "
                + "support abilities (see RandomSupportSkillChancePercent), for any "
                + "support unit that has one (Perrielle, Cabana, Kurtz, Code L, Douglas, "
                + "and any others that share this mechanism). Only takes effect when a "
                + "support unit with a valid skill is actually assigned. Applied once "
                + "as a direct code patch at startup -- toggling this requires "
                + "restarting the game.");

            RandomSupportSkillChancePercent = Config.Bind(
                "Fixes",
                "RandomSupportSkillChancePercent",
                100,
                new ConfigDescription(
                    "Chance, as a percentage, that the ability triggers each time it's "
                    + "checked. This REPLACES the game's own hidden chance entirely -- "
                    + "it's not added on top of it -- so this number is the exact "
                    + "probability, not a bonus. 100 (the default) means always; 0 means "
                    + "never. Only applies when RandomSupportSkillsAlwaysActivate is "
                    + "true. Read fresh on every check (not baked into the code patch), "
                    + "so this one DOES take effect without restarting.",
                    new AcceptableValueRange<int>(0, 100)));

            DefaultSupportUnitWhenBlank = Config.Bind(
                "Fixes",
                "DefaultSupportUnitWhenBlank",
                true,
                "If you haven't assigned any support unit at all, treats "
                + "DefaultSupportUnitId as assigned so the other support-ability fixes "
                + "above have something to act on. Has no effect if you've assigned a "
                + "real support unit yourself -- your actual choice always takes "
                + "priority. Ordinary Harmony patch, no restart needed to toggle.");

            DefaultSupportUnitId = Config.Bind(
                "Fixes",
                "DefaultSupportUnitId",
                70,
                "Character id to treat as your support unit when none is assigned (see "
                + "DefaultSupportUnitWhenBlank). Defaults to Perrielle (70), the "
                + "community Cheat Engine table's own choice, but this can be set to "
                + "any recruited support-capable character's id -- e.g. Kerrin (340), "
                + "Martha (940), Ormond (330), Pastole (1120), or Rohan (630). See "
                + "SUPPORT_ABILITIES.md for the full list of support units and their "
                + "ids. If the chosen character isn't actually support-capable, this "
                + "simply has no visible effect rather than causing an error.");

            LargeUnitsTakeOneSlot = Config.Bind(
                "Fixes",
                "LargeUnitsTakeOneSlot",
                true,
                "Large-size units (e.g. Garoo, Vaught) take up only 1 party slot "
                + "instead of 2, same as any other character. Applied once as a direct "
                + "code patch at startup -- toggling this requires restarting the "
                + "game. Purely a convenience fix; it has no effect on saved data.");

            try
            {
                var harmony = new Harmony(Guid);
                harmony.PatchAll();
            }
            catch (Exception e)
            {
                Log.LogError($"{Name} failed to apply Harmony patches: " + e);
            }

            // Each native patch is isolated: one throwing unexpectedly must not prevent
            // the other from being attempted, or swallow its own log line.
            if (AlwaysFormPartyAtSavePoints.Value)
            {
                try { NativePatches.PatchAlwaysFormParty(Log); }
                catch (Exception e) { Log.LogError("PatchAlwaysFormParty threw: " + e); }
            }
            if (AlwaysHaveCollectionPointBonus.Value)
            {
                try { NativePatches.PatchCollectionPointBonus(Log); }
                catch (Exception e) { Log.LogError("PatchCollectionPointBonus threw: " + e); }
            }
            if (AlwaysHaveRohanHealBonus.Value)
            {
                try { NativePatches.PatchRohanHeal(Log); }
                catch (Exception e) { Log.LogError("PatchRohanHeal threw: " + e); }
            }
            if (RandomSupportSkillsAlwaysActivate.Value)
            {
                try { NativePatches.PatchTryRandomStart(Log); }
                catch (Exception e) { Log.LogError("PatchTryRandomStart threw: " + e); }
            }
            if (LargeUnitsTakeOneSlot.Value)
            {
                try { NativePatches.PatchLargeUnitsTakeOneSlot(Log); }
                catch (Exception e) { Log.LogError("PatchLargeUnitsTakeOneSlot threw: " + e); }
            }

            Log.LogInfo($"{Name} v{Version} loaded. "
                + $"AlwaysFormPartyAtSavePoints = {AlwaysFormPartyAtSavePoints.Value}, "
                + $"AlwaysHaveCollectionPointBonus = {AlwaysHaveCollectionPointBonus.Value}, "
                + $"AlwaysHaveRohanHealBonus = {AlwaysHaveRohanHealBonus.Value} "
                + $"(min {RohanHealMinimumPercent.Value}%), "
                + $"RandomSupportSkillsAlwaysActivate = {RandomSupportSkillsAlwaysActivate.Value} "
                + $"({RandomSupportSkillChancePercent.Value}% chance), "
                + $"DefaultSupportUnitWhenBlank = {DefaultSupportUnitWhenBlank.Value} "
                + $"(id {DefaultSupportUnitId.Value}), "
                + $"LargeUnitsTakeOneSlot = {LargeUnitsTakeOneSlot.Value}");
        }
    }

    // Ordinary Harmony patches -- discovered automatically by harmony.PatchAll(). These
    // target substantial, real methods (not trivial getters), so a normal prefix/postfix
    // is enough; no native memory patching needed. See the architecture note at the top
    // of this file for when that's NOT true and NativePatches is the right tool instead.

    // Party_GetBattleFinishHealRate_Patch and SupportSkillState_TryRandomStart_Patch used
    // to live here as ordinary Harmony patches. Diagnostic logging (see git history)
    // proved both were reflection-based guesses at the wrong mechanism: the heal-rate
    // getter clamped correctly but never fed the real post-battle heal (the CT's actual
    // target is a different method, Battle.StateVictory.ApplyBattleFinishHeal), and
    // TryRandomStart's IsValid was False on every real call, so the "always activate"
    // branch never ran. Both are now native patches in NativePatches, matching the CT's
    // verified targets exactly -- see PatchRohanHeal and PatchTryRandomStart below.

    [HarmonyPatch(typeof(GameData.Party), nameof(GameData.Party.SupportUnitID), MethodType.Getter)]
    internal static class Party_SupportUnitID_Patch
    {
        // Only substitutes when the real value is the game's own "nothing assigned"
        // sentinel (EmptySupportUnitId); a real assigned support unit is never
        // overridden.
        //
        // DIAGNOSTIC: reported not working in testing. This getter may simply not be
        // called very often if the game caches the assigned support unit elsewhere
        // (SupportSkillState's own _SupportUnit_k__BackingField, populated once rather
        // than re-read every check) -- logging to find out.
        private static void Postfix(ref int __result)
        {
            Plugin.Log.LogInfo($"[diag] SupportUnitID getter called, raw result = {__result}, "
                + $"EmptySupportUnitId = {GameData.Party.EmptySupportUnitId}");
            if (!Plugin.DefaultSupportUnitWhenBlank.Value) return;
            if (__result == GameData.Party.EmptySupportUnitId)
                __result = Plugin.DefaultSupportUnitId.Value;
        }
    }

    internal static class NativePatches
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize,
            uint flNewProtect, out uint lpflOldProtect);

        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize,
            uint flAllocationType, uint flProtect);

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;

        /// Locate a native method's compiled code address the same way for every fix in
        /// this file: read the interop-generated NativeMethodInfoPtr_&lt;name&gt;_* static
        /// field (the same Il2CppMethodInfo* the game's own bindings use to call it) and
        /// dereference its first field (methodPointer, offset 0 -- stable across every
        /// IL2CPP version Il2CppInterop.Runtime ships a struct definition for).
        private static IntPtr ResolveNativeCode(Type declaringType, string methodName, ManualLogSource log)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance;
            var field = declaringType.GetFields(All)
                .FirstOrDefault(f => f.IsStatic && f.Name.StartsWith("NativeMethodInfoPtr_" + methodName + "_"));
            if (field == null)
            {
                log.LogError($"{declaringType.Name}.{methodName}: native method info field not found.");
                return IntPtr.Zero;
            }
            var methodInfoPtr = (IntPtr)field.GetValue(null);
            if (methodInfoPtr == IntPtr.Zero)
            {
                log.LogError($"{declaringType.Name}.{methodName}: native method info pointer is null.");
                return IntPtr.Zero;
            }
            return Marshal.ReadIntPtr(methodInfoPtr, 0);
        }

        // History, because this took three attempts to get right:
        //
        // v1 (postfix on Initialize(), force the derived _isSaveOnly field to false
        //     directly): applied without error, but didn't unlock party organization and
        //     left the player unable to move. The postfix ran *after* Initialize had
        //     already run its setup logic (wiring an InnCanvas + a callback) under the
        //     assumption the ability wasn't active; flipping the output boolean
        //     afterward didn't undo the setup that got skipped.
        //
        // v2 (prefix on the IsAssinedUnitSupportCassandra getter, force it to return
        //     true): applied cleanly, caused no crash -- and had zero effect. Confirmed
        //     by testing: the party-organization menu option still didn't appear.
        //     Because the comparison this depends on is INLINED directly into
        //     Initialize()'s compiled code (a raw `cmp` instruction, not a call), nothing
        //     at runtime ever actually calls this getter at that point. Both v1 and v2
        //     are boundary-level Harmony patches (prefix/postfix around a whole method),
        //     and neither can reach inside a method to change an inlined decision.
        //
        // This is the same problem the community Cheat Engine table solves by patching
        // the compiled machine code directly, in place, at the exact instruction --
        // something Harmony's attribute-based patching cannot do at all. v3 replicates
        // that technique properly instead of continuing to guess at boundary patches:
        //
        //   1. Get the REAL compiled code address for Initialize(), the same way the
        //      game's own interop bindings do internally: each interop method has a
        //      cached static NativeMethodInfoPtr_* field holding an Il2CppMethodInfo*;
        //      that struct's first field (offset 0, confirmed via
        //      Il2CppInterop.Runtime's own struct definitions, stable across every IL2CPP
        //      version variant that library supports) is the actual native code pointer.
        //   2. Verify the exact 13-byte instruction sequence this patch targets is
        //      present before touching anything -- if a game update changed this method,
        //      this bails loudly instead of overwriting the wrong bytes.
        //   3. Overwrite just the comparison: `cmp dword [rax+0x20], 0x190` (7 bytes)
        //      becomes `cmp eax,eax` + 5 NOPs (same 7-byte length, so nothing after it
        //      shifts). Deliberately NOT the CT's own replacement -- theirs reads a
        //      separate memory location and jumps out to a trampoline; `cmp eax,eax`
        //      needs no allocation, no jump, doesn't touch eax's actual value (a CMP
        //      never writes back), and removes the original's memory dereference of
        //      [rax+0x20] entirely rather than adding one. It always sets ZF=1, so the
        //      unmodified `setne al` right after it always computes al=0 -- and unlike
        //      v1/v2, this happens *before* Initialize's own logic runs, so every branch
        //      inside Initialize that depends on this result -- not just the one that
        //      sets _isSaveOnly -- sees the same "ability active" outcome the game's own
        //      code would produce if Cassandra really were assigned, including whatever
        //      setup v1 was missing.
        //
        // Confirmed working end to end in-game: the Organize Party option appears at a
        // field save point (it didn't with v1/v2), and using it opens the party
        // formation screen and functions normally -- no softlock, no side effects.
        public static void PatchAlwaysFormParty(ManualLogSource log)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance;

            var savePointType = typeof(FieldStage.FacilitySavePoint);
            var nativeInfoField = savePointType.GetFields(All)
                .FirstOrDefault(f => f.IsStatic && f.Name.StartsWith("NativeMethodInfoPtr_Initialize_"));
            if (nativeInfoField == null)
            {
                log.LogError("AlwaysFormPartyAtSavePoints: could not find Initialize()'s "
                    + "native method info field -- NOT patching.");
                return;
            }

            var methodInfoPtr = (IntPtr)nativeInfoField.GetValue(null);
            if (methodInfoPtr == IntPtr.Zero)
            {
                log.LogError("AlwaysFormPartyAtSavePoints: native method info pointer is "
                    + "null -- NOT patching.");
                return;
            }

            // Il2CppMethodInfo's first field is methodPointer (offset 0) -- the actual
            // compiled code address -- true across every version variant
            // Il2CppInterop.Runtime ships a struct definition for.
            IntPtr code = Marshal.ReadIntPtr(methodInfoPtr, 0);
            if (code == IntPtr.Zero)
            {
                log.LogError("AlwaysFormPartyAtSavePoints: methodPointer is null -- NOT patching.");
                return;
            }

            byte[] expected = { 0x81, 0x78, 0x20, 0x90, 0x01, 0x00, 0x00,
                                0x0F, 0x95, 0xC0, 0x88, 0x43, 0x48 };
            // Confirmed present via a read-only diagnostic pass at offset 0x95 in this
            // build (an extra static-init guard + calls ahead of it that the community
            // CT's own 100-byte scan window didn't have to account for) -- 512 gives
            // comfortable margin without being so wide it risks matching a DIFFERENT,
            // unrelated occurrence of these bytes elsewhere (a second, differently-tailed
            // occurrence -- 0F 94 vs 0F 95, opposite SETcc polarity -- was found at 0x722,
            // clearly a different comparison and out of range here on purpose).
            const int scanRange = 512;
            var window = new byte[scanRange + expected.Length];
            Marshal.Copy(code, window, 0, window.Length);

            int foundAt = -1;
            for (int i = 0; i <= scanRange; i++)
            {
                bool match = true;
                for (int j = 0; j < expected.Length; j++)
                {
                    if (window[i + j] != expected[j]) { match = false; break; }
                }
                if (match) { foundAt = i; break; }
            }

            if (foundAt < 0)
            {
                // Diagnostic only -- nothing is written on this path. This is the same
                // fallback that found the real offset (0x95) when a future game update
                // moves it again: rather than guess at a new exact byte sequence, widen
                // out and scan for just the distinctive 4-byte immediate (400 / 0x190), a
                // much weaker but still useful signal, and log every hit with surrounding
                // context so the real comparison -- whatever form it now takes -- can be
                // read directly instead of assumed.
                const int wideRange = 2048;
                var wide = new byte[wideRange];
                Marshal.Copy(code, wide, 0, wide.Length);
                byte[] needle = { 0x90, 0x01, 0x00, 0x00 };   // little-endian 0x00000190

                log.LogError("AlwaysFormPartyAtSavePoints: expected instruction sequence "
                    + "not found near Initialize() -- NOT patching (nothing was written).");
                log.LogError($"  resolved code address: 0x{code.ToInt64():x}");
                log.LogError("  first 64 bytes there: "
                    + BitConverter.ToString(wide, 0, 64).Replace("-", " "));

                int hits = 0;
                for (int i = 0; i <= wide.Length - needle.Length && hits < 15; i++)
                {
                    bool match = true;
                    for (int j = 0; j < needle.Length; j++)
                        if (wide[i + j] != needle[j]) { match = false; break; }
                    if (!match) continue;
                    hits++;
                    int ctxStart = Math.Max(0, i - 12);
                    int ctxLen = Math.Min(28, wide.Length - ctxStart);
                    string ctx = BitConverter.ToString(wide, ctxStart, ctxLen).Replace("-", " ");
                    log.LogError($"  0x190 constant at +0x{i:x} (offset {ctxStart} context): {ctx}");
                }
                log.LogError($"  total 0x190 constant occurrences in {wideRange} bytes: {hits}"
                    + (hits >= 15 ? " (capped at 15)" : ""));
                return;
            }

            IntPtr patchAddr = IntPtr.Add(code, foundAt);
            byte[] replacement = { 0x3B, 0xC0, 0x90, 0x90, 0x90, 0x90, 0x90 };

            if (!VirtualProtect(patchAddr, (UIntPtr)replacement.Length,
                                PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.LogError("AlwaysFormPartyAtSavePoints: VirtualProtect failed, "
                    + "Win32 error " + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            try
            {
                Marshal.Copy(replacement, 0, patchAddr, replacement.Length);
            }
            finally
            {
                VirtualProtect(patchAddr, (UIntPtr)replacement.Length, oldProtect, out _);
            }

            log.LogInfo($"AlwaysFormPartyAtSavePoints: patched 7 bytes at "
                + $"Initialize()+0x{foundAt:x2} (0x{patchAddr.ToInt64():x})");
        }

        // Community CT: "Always Have Kerrin/Martha/Ormand/Pastole Support Abilities
        // (Increases Collection Point 100%)". Targets GameData.CollectionPointRate.GetCount,
        // right before it calls into whatever computes the actual bonus:
        //
        //   xor r8d, r8d      (3 bytes)
        //   mov edx, edi      (2 bytes)   <- edi is the rate value about to become an arg
        //   mov ecx, ebx      (2 bytes)
        //   call <bonus fn>   (5 bytes)
        //   add eax, ebx      (2 bytes)
        //
        // The CT's fix: if edi < 100, bump it to 100 before it's used. Unlike the
        // AlwaysFormPartyAtSavePoints fix, this genuinely needs new logic inserted (a
        // compare-and-clamp), not just a same-length neutralize -- there's no way to fit
        // "clamp to a minimum" into the 2-byte slot being replaced. The CT handles this
        // with a classic trampoline: a tiny jump at the patch site into separately
        // allocated memory holding the real logic, then a jump back.
        //
        // This replicates that, but overwrites the FULL 14-byte matched region (not just
        // the CT's minimal 2 bytes) so the redirect lands on a clean instruction boundary,
        // and uses an indirect absolute jump/call (FF25/FF15 + an inline 8-byte pointer)
        // rather than a relative one -- avoids the CT's implicit requirement that its
        // allocated memory land within a very short (2-byte, +-127) or even a near
        // (5-byte, +-2GB) jump range of the patch site. An indirect jump works from
        // anywhere in the address space, so the trampoline can be allocated with a plain
        // VirtualAlloc and no address hint, no retries, no proximity requirement at all.
        //
        // The original call's target is read from the currently-matched bytes at runtime
        // (not hardcoded) and preserved exactly, so this doesn't depend on that function's
        // address either -- only on the instruction *shapes* immediately around it.
        public static void PatchCollectionPointBonus(ManualLogSource log)
        {
            var collectionType = Type.GetType("GameData.CollectionPointRate, Assembly-CSharp");
            if (collectionType == null)
            {
                log.LogError("CollectionPointBonus: GameData.CollectionPointRate type not found -- NOT patching.");
                return;
            }

            IntPtr code = ResolveNativeCode(collectionType, "GetCount", log);
            if (code == IntPtr.Zero) return;

            byte[] expected = {
                0x45, 0x33, 0xC0,               // xor r8d, r8d
                0x8B, 0xD7,                     // mov edx, edi
                0x8B, 0xCB,                     // mov ecx, ebx
                0xE8, 0x00, 0x00, 0x00, 0x00,   // call rel32 (wildcard bytes -- checked separately)
                0x03, 0xC3,                     // add eax, ebx
            };
            const int scanRange = 512;
            var window = new byte[scanRange + expected.Length];
            Marshal.Copy(code, window, 0, window.Length);

            int foundAt = -1;
            for (int i = 0; i <= scanRange; i++)
            {
                bool match = true;
                for (int j = 0; j < expected.Length; j++)
                {
                    if (j >= 8 && j <= 11) continue;   // the call's rel32 -- any value matches
                    if (window[i + j] != expected[j]) { match = false; break; }
                }
                if (match) { foundAt = i; break; }
            }

            if (foundAt < 0)
            {
                log.LogError("CollectionPointBonus: expected instruction sequence not "
                    + "found near GetCount() -- NOT patching (nothing was written).");
                log.LogError($"  resolved code address: 0x{code.ToInt64():x}");
                log.LogError("  first 64 bytes there: "
                    + BitConverter.ToString(window, 0, Math.Min(64, window.Length)).Replace("-", " "));
                return;
            }

            IntPtr patchAddr = IntPtr.Add(code, foundAt);

            // Recover the original call's absolute target from its rel32 (standard x86
            // encoding: target = address right after the call instruction + rel32).
            int callRel32 = BitConverter.ToInt32(window, foundAt + 8);
            IntPtr callInsnEnd = IntPtr.Add(patchAddr, 12);   // offset 7 (opcode) + 5 (insn length)
            long originalCallTarget = callInsnEnd.ToInt64() + callRel32;

            IntPtr resumeAddr = IntPtr.Add(patchAddr, expected.Length);   // right after add eax,ebx

            // Trampoline: replay xor r8d,r8d, then clamp edi to a 100 floor before the
            // (replayed) mov edx,edi, then replay mov ecx,ebx / call / add eax,ebx exactly
            // as the original did, then jump back.
            var tramp = new System.Collections.Generic.List<byte>();
            tramp.AddRange(new byte[] { 0x45, 0x33, 0xC0 });                 // xor r8d,r8d
            tramp.AddRange(new byte[] { 0x83, 0xFF, 0x64 });                 // cmp edi, 0x64 (100)
            tramp.AddRange(new byte[] { 0x7D, 0x05 });                      // jge +5 (past the mov edi,100)
            tramp.AddRange(new byte[] { 0xBF, 0x64, 0x00, 0x00, 0x00 });    // mov edi, 100
            tramp.AddRange(new byte[] { 0x8B, 0xD7 });                      // mov edx, edi
            tramp.AddRange(new byte[] { 0x8B, 0xCB });                      // mov ecx, ebx
            tramp.AddRange(new byte[] { 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00 }); // call [rip+0] -> ptr below
            tramp.AddRange(BitConverter.GetBytes(originalCallTarget));      // 8-byte absolute call target
            tramp.AddRange(new byte[] { 0x03, 0xC3 });                      // add eax, ebx
            tramp.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 }); // jmp [rip+0] -> ptr below
            tramp.AddRange(BitConverter.GetBytes(resumeAddr.ToInt64()));    // 8-byte absolute resume address

            IntPtr trampMem = VirtualAlloc(IntPtr.Zero, (UIntPtr)tramp.Count,
                MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (trampMem == IntPtr.Zero)
            {
                log.LogError("CollectionPointBonus: VirtualAlloc failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            Marshal.Copy(tramp.ToArray(), 0, trampMem, tramp.Count);

            // Redirect: overwrite the full matched region with an indirect absolute jump
            // to the trampoline. 6-byte instruction + 8-byte inline pointer = 14 bytes,
            // exactly the region's length -- no padding, no leftover bytes.
            var redirect = new System.Collections.Generic.List<byte>();
            redirect.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            redirect.AddRange(BitConverter.GetBytes(trampMem.ToInt64()));
            if (redirect.Count != expected.Length)
            {
                log.LogError("CollectionPointBonus: internal error, redirect size "
                    + $"{redirect.Count} != region size {expected.Length} -- NOT patching.");
                return;
            }

            if (!VirtualProtect(patchAddr, (UIntPtr)redirect.Count,
                                PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.LogError("CollectionPointBonus: VirtualProtect failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            try
            {
                Marshal.Copy(redirect.ToArray(), 0, patchAddr, redirect.Count);
            }
            finally
            {
                VirtualProtect(patchAddr, (UIntPtr)redirect.Count, oldProtect, out _);
            }

            log.LogInfo($"CollectionPointBonus: patched 14 bytes at GetCount()+0x{foundAt:x2} "
                + $"(0x{patchAddr.ToInt64():x}), trampoline at 0x{trampMem.ToInt64():x}");
        }

        // Community CT: "Always Have Rohan's Emergency Treatment After Battle (Heals 20%)".
        // Targets Battle.StateVictory.ApplyBattleFinishHeal -- NOT GameData.Party's
        // GetBattleFinishHealRate getter, which a first attempt at this fix used (found by
        // reflection/name-plausibility, not verified against the CT). Diagnostic logging
        // proved that getter's return value never reaches the real heal: it clamped 0 to
        // 20 successfully every time it was called, but the clamped value had no visible
        // in-game effect, meaning ApplyBattleFinishHeal computes its own value rather than
        // consulting that getter. This patches the actual computation instead:
        //
        //   mov r14d, eax                    (3 bytes)
        //   test eax, eax                    (2 bytes)
        //   jle <skip-heal, rel32>            (6 bytes, fixed displacement per the CT)
        //   mov rcx, [rip+disp32]             (7 bytes, disp wildcarded)
        //
        // eax holds the raw heal percent right before it's stored into r14d and tested
        // against zero (jle skips healing entirely for eax<=0). The fix: clamp eax to the
        // configured minimum before any of that runs. Needs a trampoline (inserting a
        // compare-and-clamp, not a same-length neutralize), and the relocated code can't
        // reuse the original's RIP-relative "mov rcx,[rip+disp32]" as-is -- once moved to
        // separately allocated memory, "rip" means something different, so the trampoline
        // recomputes the absolute address that instruction pointed at and loads it with an
        // equivalent movabs+deref pair instead.
        public static void PatchRohanHeal(ManualLogSource log)
        {
            var stateVictoryType = Type.GetType("Battle.StateVictory, Assembly-CSharp");
            if (stateVictoryType == null)
            {
                log.LogError("PatchRohanHeal: Battle.StateVictory type not found -- NOT patching.");
                return;
            }

            IntPtr code = ResolveNativeCode(stateVictoryType, "ApplyBattleFinishHeal", log);
            if (code == IntPtr.Zero) return;

            byte[] expected = {
                0x44, 0x8B, 0xF0,                     // mov r14d, eax
                0x85, 0xC0,                           // test eax, eax
                0x0F, 0x8E, 0x16, 0x03, 0x00, 0x00,   // jle rel32 (fixed displacement per CT)
                0x48, 0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, // mov rcx, [rip+disp32] (disp wildcarded)
            };
            const int scanRange = 512;
            var window = new byte[scanRange + expected.Length];
            Marshal.Copy(code, window, 0, window.Length);

            int foundAt = -1;
            for (int i = 0; i <= scanRange; i++)
            {
                bool match = true;
                for (int j = 0; j < expected.Length; j++)
                {
                    if (j >= 14 && j <= 17) continue;   // mov's disp32 -- any value matches
                    if (window[i + j] != expected[j]) { match = false; break; }
                }
                if (match) { foundAt = i; break; }
            }

            if (foundAt < 0)
            {
                log.LogError("PatchRohanHeal: expected instruction sequence not found near "
                    + "ApplyBattleFinishHeal() -- NOT patching (nothing was written).");
                log.LogError($"  resolved code address: 0x{code.ToInt64():x}");
                log.LogError("  first 64 bytes there: "
                    + BitConverter.ToString(window, 0, Math.Min(64, window.Length)).Replace("-", " "));
                return;
            }

            IntPtr patchAddr = IntPtr.Add(code, foundAt);
            int min = Plugin.RohanHealMinimumPercent.Value;

            // jle's target: address right after the 6-byte jle instruction (offset 11) +
            // its rel32 (read from the matched bytes, not assumed, even though the CT's
            // own AOB pins it to a fixed value).
            int jleRel32 = BitConverter.ToInt32(window, foundAt + 7);
            IntPtr originalJleTarget = IntPtr.Add(code, foundAt + 11 + jleRel32);

            // mov rcx,[rip+disp32]'s target: address right after that 7-byte instruction
            // (offset 18) + its disp32.
            int movDisp32 = BitConverter.ToInt32(window, foundAt + 14);
            IntPtr absoluteMovTarget = IntPtr.Add(code, foundAt + 18 + movDisp32);

            IntPtr resumeAddr = IntPtr.Add(code, foundAt + expected.Length);   // right after the mov

            var tramp = new List<byte>();
            tramp.Add(0x3D);                                    // cmp eax, imm32
            tramp.AddRange(BitConverter.GetBytes(min));
            int jgePos = tramp.Count;
            tramp.AddRange(new byte[] { 0x7D, 0x00 });          // jge <placeholder>
            tramp.Add(0xB8);                                    // mov eax, imm32
            tramp.AddRange(BitConverter.GetBytes(min));
            tramp[jgePos + 1] = (byte)(tramp.Count - (jgePos + 2));

            tramp.AddRange(new byte[] { 0x44, 0x8B, 0xF0 });    // mov r14d, eax (replay, clamped)
            tramp.AddRange(new byte[] { 0x85, 0xC0 });          // test eax, eax (replay)
            int jgPos = tramp.Count;
            tramp.AddRange(new byte[] { 0x7F, 0x00 });          // jg <placeholder> -- eax>0, continue normally

            // eax<=0 path (only reachable if min was configured to 0): replay what the
            // original jle would have done.
            tramp.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            tramp.AddRange(BitConverter.GetBytes(originalJleTarget.ToInt64()));
            tramp[jgPos + 1] = (byte)(tramp.Count - (jgPos + 2));

            // continue-normally path: replay mov rcx,[rip+disp32] as an absolute load,
            // since the relocated code can't reuse RIP-relative addressing unchanged.
            tramp.AddRange(new byte[] { 0x48, 0xB9 });          // movabs rcx, imm64
            tramp.AddRange(BitConverter.GetBytes(absoluteMovTarget.ToInt64()));
            tramp.AddRange(new byte[] { 0x48, 0x8B, 0x09 });    // mov rcx, [rcx]
            tramp.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            tramp.AddRange(BitConverter.GetBytes(resumeAddr.ToInt64()));

            IntPtr trampMem = VirtualAlloc(IntPtr.Zero, (UIntPtr)tramp.Count,
                MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (trampMem == IntPtr.Zero)
            {
                log.LogError("PatchRohanHeal: VirtualAlloc failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            Marshal.Copy(tramp.ToArray(), 0, trampMem, tramp.Count);

            // Redirect: overwrite the full 18-byte matched region with an indirect
            // absolute jump (14 bytes) padded with NOPs to the region's exact length.
            var redirect = new List<byte>();
            redirect.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            redirect.AddRange(BitConverter.GetBytes(trampMem.ToInt64()));
            while (redirect.Count < expected.Length) redirect.Add(0x90);

            if (!VirtualProtect(patchAddr, (UIntPtr)redirect.Count,
                                PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.LogError("PatchRohanHeal: VirtualProtect failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            try
            {
                Marshal.Copy(redirect.ToArray(), 0, patchAddr, redirect.Count);
            }
            finally
            {
                VirtualProtect(patchAddr, (UIntPtr)redirect.Count, oldProtect, out _);
            }

            log.LogInfo($"PatchRohanHeal: patched 18 bytes at ApplyBattleFinishHeal()+0x{foundAt:x2} "
                + $"(0x{patchAddr.ToInt64():x}), minimum {min}%, trampoline at 0x{trampMem.ToInt64():x}");
        }

        // Managed callback the TryRandomStart trampoline calls into for the configurable
        // chance roll -- native code has no easy RNG, and this setting (unlike
        // RohanHealMinimumPercent) is read fresh on every call rather than baked into the
        // patch, so it takes effect without restarting. UnmanagedCallersOnly gives a
        // stable native entry point with no GC/delegate lifetime concerns, unlike
        // Marshal.GetFunctionPointerForDelegate.
        [UnmanagedCallersOnly]
        private static byte RollSupportSkillChance()
        {
            int chance = Plugin.RandomSupportSkillChancePercent.Value;
            bool activate = chance >= 100 || (chance > 0 && System.Random.Shared.Next(100) < chance);
            return (byte)(activate ? 1 : 0);
        }

        // Community CT: "Normally Random Support Skills Always Activate (Perrielle, etc.)".
        // Targets Battle.Command.SupportSkillState.TryRandomStart's function ENTRY (its
        // prologue), not a mid-function comparison -- the CT replaces the whole method
        // body. A first attempt at this fix Harmony-patched the method and gated on its
        // managed IsValid property; diagnostic logging showed IsValid was False on both
        // real calls captured in testing, so that patch's "always activate" branch never
        // ran. The CT's actual check is different: whether the raw skill pointer at
        // [rcx+0x10] is non-null. This replicates that check natively, then calls back
        // into managed code (RollSupportSkillChance) for the configurable chance instead
        // of the CT's hardcoded always-true, since native code has no convenient RNG.
        public static void PatchTryRandomStart(ManualLogSource log)
        {
            var skillStateType = typeof(Battle.Command.SupportSkillState);
            IntPtr code = ResolveNativeCode(skillStateType, "TryRandomStart", log);
            if (code == IntPtr.Zero) return;

            byte[] expected = {
                0x48, 0x89, 0x5C, 0x24, 0x08,   // mov [rsp+8], rbx
                0x57,                           // push rdi
                0x48, 0x83, 0xEC, 0x00,         // sub rsp, imm8 (wildcarded)
            };
            const int scanRange = 64;   // CT scans TryRandomStart+50; this is the function's own prologue
            var window = new byte[scanRange + expected.Length];
            Marshal.Copy(code, window, 0, window.Length);

            int foundAt = -1;
            for (int i = 0; i <= scanRange; i++)
            {
                bool match = true;
                for (int j = 0; j < expected.Length; j++)
                {
                    if (j == 9) continue;   // sub rsp's imm8 -- any value matches
                    if (window[i + j] != expected[j]) { match = false; break; }
                }
                if (match) { foundAt = i; break; }
            }

            if (foundAt < 0)
            {
                log.LogError("PatchTryRandomStart: expected instruction sequence not found "
                    + "near TryRandomStart() -- NOT patching (nothing was written).");
                log.LogError($"  resolved code address: 0x{code.ToInt64():x}");
                log.LogError("  first 64 bytes there: "
                    + BitConverter.ToString(window, 0, Math.Min(64, window.Length)).Replace("-", " "));
                return;
            }

            IntPtr patchAddr = IntPtr.Add(code, foundAt);

            IntPtr callbackPtr;
            unsafe
            {
                delegate* unmanaged<byte> fn = &RollSupportSkillChance;
                callbackPtr = (IntPtr)fn;
            }

            // Full function-body replacement -- this never resumes the original code, so
            // there's no resume address and no need to preserve anything past entry.
            var tramp = new List<byte>();
            tramp.AddRange(new byte[] { 0x48, 0x83, 0x79, 0x10, 0x00 });   // cmp qword [rcx+0x10], 0
            int jnePos = tramp.Count;
            tramp.AddRange(new byte[] { 0x75, 0x00 });                    // jne <placeholder>
            tramp.AddRange(new byte[] { 0x33, 0xC0 });                    // xor eax, eax (no valid skill)
            tramp.Add(0xC3);                                              // ret
            tramp[jnePos + 1] = (byte)(tramp.Count - (jnePos + 2));

            tramp.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });        // sub rsp, 0x28 (shadow space)
            tramp.AddRange(new byte[] { 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00 }); // call [rip+0]
            tramp.AddRange(BitConverter.GetBytes(callbackPtr.ToInt64()));
            tramp.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });        // add rsp, 0x28
            tramp.AddRange(new byte[] { 0x0F, 0xB6, 0xC0 });              // movzx eax, al
            tramp.Add(0xC3);                                              // ret

            IntPtr trampMem = VirtualAlloc(IntPtr.Zero, (UIntPtr)tramp.Count,
                MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (trampMem == IntPtr.Zero)
            {
                log.LogError("PatchTryRandomStart: VirtualAlloc failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            Marshal.Copy(tramp.ToArray(), 0, trampMem, tramp.Count);

            // Redirect: 14-byte indirect jump at the function's entry. This overwrites
            // more than the 9-byte matched signature -- safe because the whole method is
            // being replaced (nothing past entry is ever executed again), and a method
            // with real logic (as this one has, per the CT's own scan window) is
            // comfortably longer than 14 bytes.
            var redirect = new List<byte>();
            redirect.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            redirect.AddRange(BitConverter.GetBytes(trampMem.ToInt64()));

            if (!VirtualProtect(patchAddr, (UIntPtr)redirect.Count,
                                PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.LogError("PatchTryRandomStart: VirtualProtect failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            try
            {
                Marshal.Copy(redirect.ToArray(), 0, patchAddr, redirect.Count);
            }
            finally
            {
                VirtualProtect(patchAddr, (UIntPtr)redirect.Count, oldProtect, out _);
            }

            log.LogInfo($"PatchTryRandomStart: patched 14 bytes at TryRandomStart()+0x{foundAt:x2} "
                + $"(0x{patchAddr.ToInt64():x}), trampoline at 0x{trampMem.ToInt64():x}");
        }

        // Community CT: "Large Size Units Take 1 Space In Party". Targets
        // MasterDataExtension.UnitParamExtensions.IsLSize -- unlike every other fix in this
        // file, this one is a full-body replacement of a trivial property-style method, so
        // it needs neither a trampoline nor a resume address: overwrite the entry point
        // in-place with "return false" and nothing after it is ever reached again.
        public static void PatchLargeUnitsTakeOneSlot(ManualLogSource log)
        {
            var unitParamExtType = Type.GetType(
                "MasterDataExtension.UnitParamExtensions, Assembly-CSharp");
            if (unitParamExtType == null)
            {
                log.LogError("PatchLargeUnitsTakeOneSlot: MasterDataExtension.UnitParamExtensions "
                    + "type not found -- NOT patching.");
                return;
            }

            IntPtr code = ResolveNativeCode(unitParamExtType, "IsLSize", log);
            if (code == IntPtr.Zero) return;

            byte[] expected = { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x20 };   // push rbx; sub rsp,0x20
            const int scanRange = 64;   // CT scans IsLSize+50; this is the function's own prologue
            var window = new byte[scanRange + expected.Length];
            Marshal.Copy(code, window, 0, window.Length);

            int foundAt = -1;
            for (int i = 0; i <= scanRange; i++)
            {
                bool match = true;
                for (int j = 0; j < expected.Length; j++)
                {
                    if (window[i + j] != expected[j]) { match = false; break; }
                }
                if (match) { foundAt = i; break; }
            }

            if (foundAt < 0)
            {
                log.LogError("PatchLargeUnitsTakeOneSlot: expected instruction sequence not "
                    + "found near IsLSize() -- NOT patching (nothing was written).");
                log.LogError($"  resolved code address: 0x{code.ToInt64():x}");
                log.LogError("  first 64 bytes there: "
                    + BitConverter.ToString(window, 0, Math.Min(64, window.Length)).Replace("-", " "));
                return;
            }

            IntPtr patchAddr = IntPtr.Add(code, foundAt);
            // xor eax,eax ; ret -- returns false (bool lives in AL; upper bits of eax/rax
            // are zeroed as a side effect of the 32-bit xor on x64, so this is equivalent
            // to the CT's "xor rax,rax"), padded with NOPs to the matched region's length.
            byte[] replacement = { 0x33, 0xC0, 0xC3, 0x90, 0x90, 0x90 };

            if (!VirtualProtect(patchAddr, (UIntPtr)replacement.Length,
                                PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.LogError("PatchLargeUnitsTakeOneSlot: VirtualProtect failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            try
            {
                Marshal.Copy(replacement, 0, patchAddr, replacement.Length);
            }
            finally
            {
                VirtualProtect(patchAddr, (UIntPtr)replacement.Length, oldProtect, out _);
            }

            log.LogInfo($"PatchLargeUnitsTakeOneSlot: patched 6 bytes at IsLSize()+0x{foundAt:x2} "
                + $"(0x{patchAddr.ToInt64():x})");
        }
    }
}
