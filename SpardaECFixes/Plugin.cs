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
        internal static ConfigEntry<bool> AnyRowAttacks;
        internal static ConfigEntry<bool> CanEquipAnything;

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
                false,
                "EXPERIMENTAL / currently disabled: guarantees a minimum party heal after every battle (see "
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
                false,
                "EXPERIMENTAL / currently disabled: overrides the trigger chance for 'sometimes appears before battle' "
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
                false,
                "EXPERIMENTAL / currently disabled: if you haven't assigned any support unit at all, treats "
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

            AnyRowAttacks = Config.Bind(
                "Fixes", "AnyRowAttacks", true,
                "Lets any of your units attack any enemy regardless of front/back row, "
                + "while enemies still follow normal row restrictions against you (and "
                + "against each other -- see SUPPORT_ABILITIES.md/README for how the ally "
                + "check works). Applied once as a direct code patch at startup -- "
                + "toggling this requires restarting the game. Purely a convenience fix; "
                + "it has no effect on saved data.");

            CanEquipAnything = Config.Bind(
                "Fixes", "CanEquipAnything", true,
                "Any character can equip any piece of gear, including shields normally "
                + "restricted to certain characters. Applied once as a direct code patch "
                + "at startup -- toggling this requires restarting the game. Purely a "
                + "convenience fix; it has no effect on saved data.");

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
            if (AnyRowAttacks.Value)
            {
                try { NativePatches.PatchBattleUnitListTracking(Log); }
                catch (Exception e) { Log.LogError("PatchBattleUnitListTracking threw: " + e); }
                try { NativePatches.PatchAnyRowAttacks(Log); }
                catch (Exception e) { Log.LogError("PatchAnyRowAttacks threw: " + e); }
            }
            if (CanEquipAnything.Value)
            {
                try { NativePatches.PatchCanEquipAnything(Log); }
                catch (Exception e) { Log.LogError("PatchCanEquipAnything threw: " + e); }
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
                + $"LargeUnitsTakeOneSlot = {LargeUnitsTakeOneSlot.Value}, "
                + $"AnyRowAttacks = {AnyRowAttacks.Value}, "
                + $"CanEquipAnything = {CanEquipAnything.Value}");
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
            if (!Plugin.DefaultSupportUnitWhenBlank.Value) return;
            Plugin.Log.LogInfo($"[diag] SupportUnitID getter called, raw result = {__result}, "
                + $"EmptySupportUnitId = {GameData.Party.EmptySupportUnitId}");
            if (__result == GameData.Party.EmptySupportUnitId)
                __result = Plugin.DefaultSupportUnitId.Value;
        }
    }

    [HarmonyPatch(typeof(Battle.Command.CommandValidation), "GetSkillRange")]
    internal static class RowAttackDiag_Dump_Patch
    {
        // GetSkillRange is the one method AnyRowAttacks's native patches deliberately
        // leave untouched (see the note on PatchAnyRowAttacks -- patching it softlocked
        // the turn resolver in earlier testing), so it's a safe, ordinary Harmony
        // postfix target with zero interaction risk against anything this plugin
        // changes, and it's called frequently during battle target/range calculations
        // -- exactly when there's something worth seeing in the ring buffer.
        private static void Postfix()
        {
            if (!Plugin.AnyRowAttacks.Value) return;
            NativePatches.DumpRowAttackDiagIfAny(Plugin.Log);
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

        // Reserve executable memory close enough to a target for a 32-bit relative jump.
        // VirtualAlloc's normal "anywhere" allocation can land many terabytes away from
        // the IL2CPP module, so it cannot be used with a 5-byte E9 jump. Windows reserves
        // regions at 64 KiB granularity; probing outward from the method is deterministic
        // and runs only once during plugin startup.
        private static IntPtr AllocateNear(IntPtr target, int size)
        {
            const long granularity = 0x10000;
            const long maxDistance = 0x7FFF0000; // safely inside signed rel32 reach
            long center = target.ToInt64() & ~(granularity - 1);

            for (long offset = granularity; offset <= maxDistance; offset += granularity)
            {
                long high = center + offset;
                IntPtr allocation = VirtualAlloc(new IntPtr(high), (UIntPtr)size,
                    MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                if (allocation != IntPtr.Zero) return allocation;

                long low = center - offset;
                if (low > 0)
                {
                    allocation = VirtualAlloc(new IntPtr(low), (UIntPtr)size,
                        MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                    if (allocation != IntPtr.Zero) return allocation;
                }
            }
            return IntPtr.Zero;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        private struct MODULEINFO { public IntPtr lpBaseOfDll; public uint SizeOfImage; public IntPtr EntryPoint; }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule,
            out MODULEINFO lpmodinfo, uint cb);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// Module-wide byte-pattern scan (the C# equivalent of the CT's own
        /// aobscanmodule) -- for patch targets that aren't a named, reflectable method,
        /// only known by their raw bytes somewhere inside a whole DLL. Scans the entire
        /// mapped image once; a one-time startup cost, not something done per-frame.
        private static IntPtr ScanModuleForAOB(string moduleName, byte[] pattern, ManualLogSource log)
        {
            IntPtr hMod = GetModuleHandle(moduleName);
            if (hMod == IntPtr.Zero)
            {
                log.LogError($"ScanModuleForAOB: module {moduleName} not found.");
                return IntPtr.Zero;
            }
            if (!GetModuleInformation(GetCurrentProcess(), hMod, out var info,
                    (uint)Marshal.SizeOf<MODULEINFO>()))
            {
                log.LogError("ScanModuleForAOB: GetModuleInformation failed, Win32 error "
                    + Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }
            int size = (int)info.SizeOfImage;
            var buffer = new byte[size];
            Marshal.Copy(info.lpBaseOfDll, buffer, 0, size);
            for (int i = 0; i <= size - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return IntPtr.Add(info.lpBaseOfDll, i);
            }
            return IntPtr.Zero;
        }

        // Small helpers for hand-built trampolines that need to reference a KNOWN,
        // already-allocated absolute address (never a self-referential one -- every
        // trampoline in this file always knows every address it needs to embed before
        // it starts building its own bytes, avoiding a two-pass patch-after-allocate
        // step entirely). All three assume 64-bit GPRs; register indices follow the
        // standard x86-64 encoding order (RAX=0 .. RDI=7, R8=8 .. R15=15). Verified
        // against Iced's disassembler output before ever being used against a real
        // process -- see the "why" note above PatchBattleUnitListTracking.
        private static void EmitMovAbs(List<byte> t, byte reg, ulong imm64)
        {
            t.Add((byte)(0x48 | (reg >= 8 ? 1 : 0)));
            t.Add((byte)(0xB8 + (reg & 7)));
            t.AddRange(BitConverter.GetBytes(imm64));
        }

        private static void EmitMovStoreIndirect(List<byte> t, byte addrReg, byte srcReg)
        {
            t.Add((byte)(0x48 | (srcReg >= 8 ? 4 : 0) | (addrReg >= 8 ? 1 : 0)));
            t.Add(0x89);
            t.Add((byte)(((srcReg & 7) << 3) | (addrReg & 7)));
        }

        private static void EmitMovLoadIndirect(List<byte> t, byte dstReg, byte addrReg)
        {
            t.Add((byte)(0x48 | (dstReg >= 8 ? 4 : 0) | (addrReg >= 8 ? 1 : 0)));
            t.Add(0x8B);
            t.Add((byte)(((dstReg & 7) << 3) | (addrReg & 7)));
        }

        private const byte RAX = 0, RCX = 1, RDX = 2, RBX = 3, RSP = 4, RBP = 5, RSI = 6, RDI = 7;
        private const byte R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15;

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
            // Load the target into r11 and use a register-direct call, NOT
            // "call [rip+0]" with the 8-byte pointer inlined immediately after --
            // that pattern has a subtle, serious bug: a RIP-relative call with
            // disp32=0 computes its own return address the same way it computes the
            // memory operand's effective address (both are "address right after this
            // instruction"), so the two are IDENTICAL. The callee's `ret` then jumps
            // straight into the pointer bytes and tries to execute them as code,
            // instead of landing on whatever comes next in the trampoline. This
            // affected every managed callback in this file (see the note on
            // PatchRohanHeal, PatchTryRandomStart, and PatchAnyRowAttacks) and is the
            // likely real explanation for every inconsistent crash/softlock this
            // session traced to a managed callback -- a register-direct call has no
            // such collision, since nothing about its own address is exposed via the
            // callee's return address.
            EmitMovAbs(tramp, R11, (ulong)originalCallTarget);
            tramp.AddRange(new byte[] { 0x41, 0xFF, 0xD3 });                // call r11
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

            var tramp = new List<byte>();
            // Native-only clamp: do not call into managed code from this hot battle path.
            // This has no ABI or stack-alignment assumptions because it neither calls nor
            // changes the stack. The configured minimum is baked in at startup, so changing
            // it requires a game restart.
            tramp.Add(0x3D);                                    // cmp eax, imm32
            tramp.AddRange(BitConverter.GetBytes(min));
            int jgePos = tramp.Count;
            tramp.AddRange(new byte[] { 0x7D, 0x00 });          // jge <continue>
            tramp.Add(0xB8);                                    // mov eax, imm32
            tramp.AddRange(BitConverter.GetBytes(min));
            tramp[jgePos + 1] = (byte)(tramp.Count - (jgePos + 2));

            tramp.AddRange(new byte[] { 0x44, 0x8B, 0xF0 });    // mov r14d, eax (replay, clamped)
            tramp.AddRange(new byte[] { 0x85, 0xC0 });          // test eax, eax (replay)
            int returnJumpPos = tramp.Count;
            tramp.AddRange(new byte[] { 0xE9, 0x00, 0x00, 0x00, 0x00 }); // jmp ApplyBattleFinishHeal+5

            // A 5-byte relative jump lets the original jle and RIP-relative load remain
            // completely untouched. This is the same control-flow shape as the CT script;
            // it avoids recreating any of the game's subsequent instructions by hand.
            IntPtr trampMem = AllocateNear(patchAddr, tramp.Count);
            if (trampMem == IntPtr.Zero)
            {
                log.LogError("PatchRohanHeal: unable to reserve nearby trampoline memory "
                    + "within relative-jump range -- NOT patching.");
                return;
            }
            long relativeReturn = IntPtr.Add(patchAddr, 5).ToInt64()
                - IntPtr.Add(trampMem, returnJumpPos + 5).ToInt64();
            if (relativeReturn < int.MinValue || relativeReturn > int.MaxValue)
            {
                log.LogError("PatchRohanHeal: nearby trampoline was outside relative-jump range -- NOT patching.");
                return;
            }
            byte[] returnOffset = BitConverter.GetBytes((int)relativeReturn);
            for (int i = 0; i < returnOffset.Length; i++) tramp[returnJumpPos + 1 + i] = returnOffset[i];
            Marshal.Copy(tramp.ToArray(), 0, trampMem, tramp.Count);

            long relativeEntry = trampMem.ToInt64() - IntPtr.Add(patchAddr, 5).ToInt64();
            var redirect = new List<byte> { 0xE9 };
            redirect.AddRange(BitConverter.GetBytes((int)relativeEntry));

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

            log.LogInfo($"PatchRohanHeal: patched 5 bytes at ApplyBattleFinishHeal()+0x{foundAt:x2} "
                + $"(0x{patchAddr.ToInt64():x}), minimum {min}%, trampoline at 0x{trampMem.ToInt64():x}");
        }

        // Managed callback the TryRandomStart trampoline calls into for the configurable
        // chance roll -- native code has no easy RNG, and this setting (unlike
        // RohanHealMinimumPercent) is read fresh on every call rather than baked into the
        // patch, so it takes effect without restarting. UnmanagedCallersOnly gives a
        // stable native entry point with no GC/delegate lifetime concerns, unlike
        // Marshal.GetFunctionPointerForDelegate.
        // Rolls the configured chance AND logs the result in one managed call. Originally
        // two separate calls (roll, then a separate log call) -- merged into one after
        // that two-call version crashed the game on battle start. Root cause: the second
        // call's hand-computed stack-shadow-space size assumed a stack parity that turned
        // out to be wrong, misaligning the stack for whatever aligned SSE instruction the
        // runtime hit first while formatting the log string. Rather than keep
        // hand-verifying push/pop parity across two calls, this collapses back down to
        // exactly one call per branch -- the same shape already proven safe by the
        // invalid-skill branch below, which logs and returns in one call and has never
        // crashed.
        [UnmanagedCallersOnly]
        private static byte RollAndLogSupportSkillChance()
        {
            int chance = Plugin.RandomSupportSkillChancePercent.Value;
            bool activate = chance >= 100 || (chance > 0 && System.Random.Shared.Next(100) < chance);
            Plugin.Log.LogInfo($"[diag] TryRandomStart: validSkill = True, activated = {activate}");
            return (byte)(activate ? 1 : 0);
        }

        // Diagnostic callback the invalid-skill branch calls into -- confirms
        // TryRandomStart is reached at all (unlike the earlier Harmony-based attempt,
        // which gated on a property that was False on every real call) even when there's
        // nothing to activate.
        [UnmanagedCallersOnly]
        private static void LogTryRandomStartInvalid()
        {
            Plugin.Log.LogInfo("[diag] TryRandomStart: validSkill = False, activated = False");
        }

        // Community CT: "Normally Random Support Skills Always Activate (Perrielle, etc.)".
        // Targets Battle.Command.SupportSkillState.TryRandomStart's function ENTRY (its
        // prologue), not a mid-function comparison -- the CT replaces the whole method
        // body. A first attempt at this fix Harmony-patched the method and gated on its
        // managed IsValid property; diagnostic logging showed IsValid was False on both
        // real calls captured in testing, so that patch's "always activate" branch never
        // ran. The CT's actual check is different: whether the raw skill pointer at
        // [rcx+0x10] is non-null. This replicates that check natively, then calls back
        // into managed code (RollAndLogSupportSkillChance) for the configurable chance
        // instead of the CT's hardcoded always-true, since native code has no convenient
        // RNG.
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

            IntPtr rollLogPtr;
            IntPtr invalidLogPtr;
            unsafe
            {
                delegate* unmanaged<byte> fn = &RollAndLogSupportSkillChance;
                rollLogPtr = (IntPtr)fn;
                delegate* unmanaged<void> invalidFn = &LogTryRandomStartInvalid;
                invalidLogPtr = (IntPtr)invalidFn;
            }

            // Full function-body replacement -- this never resumes the original code, so
            // there's no resume address and no need to preserve anything past entry. Both
            // branches below are exactly one call each, with no pushes/pops around them --
            // simplest possible shape, deliberately: a two-call version of the valid
            // branch (roll, then a separate log call, with a push/pop to carry the result
            // across) crashed the game on battle start from a hand-computed stack
            // alignment mistake. One call per branch removes that whole risk category.
            var tramp = new List<byte>();
            tramp.AddRange(new byte[] { 0x48, 0x83, 0x79, 0x10, 0x00 });   // cmp qword [rcx+0x10], 0
            int jnePos = tramp.Count;
            tramp.AddRange(new byte[] { 0x75, 0x00 });                    // jne <placeholder>

            // No valid skill -- log, then return false. True function entry leaves
            // rsp 8 mod 16 (the ABI guarantee right after any `call`), so 0x28 (40, not a
            // multiple of 16) is the correct shadow-space size to reach 16-alignment here.
            tramp.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });        // sub rsp, 0x28
            // Register-direct call, not "call [rip+0]" with an inline pointer -- see
            // the note on PatchCollectionPointBonus for why that pattern is broken
            // (the callee's `ret` collides with the pointer bytes). rcx/rdx are free
            // to use here: this is a full-body replacement that never resumes the
            // original function, so nothing downstream needs them preserved.
            EmitMovAbs(tramp, R11, (ulong)invalidLogPtr.ToInt64());
            tramp.AddRange(new byte[] { 0x41, 0xFF, 0xD3 });              // call r11
            tramp.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });        // add rsp, 0x28
            tramp.AddRange(new byte[] { 0x33, 0xC0 });                    // xor eax, eax (no valid skill)
            tramp.Add(0xC3);                                              // ret
            tramp[jnePos + 1] = (byte)(tramp.Count - (jnePos + 2));

            // Valid skill -- roll the chance and log in one managed call, return its result.
            // Same rsp-8-mod-16 entry state as the branch above (a conditional jump never
            // touches rsp), so the same 0x28 shadow-space size applies here too.
            tramp.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });        // sub rsp, 0x28
            EmitMovAbs(tramp, R11, (ulong)rollLogPtr.ToInt64());
            tramp.AddRange(new byte[] { 0x41, 0xFF, 0xD3 });              // call r11
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

        private const uint PAGE_READWRITE = 0x04;
        private const int MaxTrackedBattleUnits = 24;

        // Shared native scratch this ally-tracking mechanism uses -- populated once at
        // startup by PatchBattleUnitListTracking, referenced by absolute address (never
        // self-referentially) from all three hook trampolines below, and later read by
        // PatchAnyRowAttacks to tell allies apart from enemies. Layout mirrors the CT's
        // own exactly: [0]=TotalBattleUnits (qword count), [8..8+24*8)=
        // CurrentBattleUnitList (24 unit-pointer slots). The trailing 24 bytes
        // ([200..224)) are R13Save/R14Save/R15Save -- shared scratch for whichever of
        // the three hooks is running (never concurrently, matching how the CT itself
        // gets away with equally simple global scratch here).
        private static IntPtr s_battleUnitListMem = IntPtr.Zero;
        private static IntPtr TotalBattleUnitsAddr => s_battleUnitListMem;
        private static IntPtr CurrentBattleUnitListAddr => IntPtr.Add(s_battleUnitListMem, 8);
        private static IntPtr R13SaveAddr => IntPtr.Add(s_battleUnitListMem, 200);
        private static IntPtr R14SaveAddr => IntPtr.Add(s_battleUnitListMem, 208);
        private static IntPtr R15SaveAddr => IntPtr.Add(s_battleUnitListMem, 216);

        // Community CT: the "Activate Trainer 2.2" base entry's battle-unit tracking --
        // not a cheat by itself, just bookkeeping several of the CT's more advanced
        // features (including "Can Attack Any Enemy From Any Row") depend on to tell
        // allies apart from enemies. Nothing in SpardaECFixes replicated this until now;
        // Codex's first attempt at AnyRowAttacks skipped it and returned true
        // unconditionally for every unit instead, which is a materially bigger
        // behavior change than the CT makes (it removes range validation for
        // EVERYONE, not just the player's side) and is the likely cause of that
        // attempt softlocking GetSkillRange and not working correctly elsewhere.
        //
        // Three hooks populate the shared buffer above, matching the CT's own three
        // exactly:
        //   1. A reset, hooked into wherever the game begins assembling the battle
        //      unit list for a fight -- zeroes the tracked count and array first.
        //   2. An append, hooked into the per-unit loop that follows -- appends each
        //      unit pointer (rax at that point) as it's processed.
        //   3. A second reset, hooked into Battle.Context.Terminate -- clears the list
        //      again when a battle ends, so stale pointers from a finished fight are
        //      never mistaken for allies in the next one.
        //
        // Hooks 1 and 2 aren't named, reflectable methods -- the CT finds them with a
        // module-wide byte scan (aobscanmodule) rather than pointing at a specific
        // method, so ScanModuleForAOB replicates that here. Hook 3 targets a real
        // method (Battle.Context.Terminate) and uses the same ResolveNativeCode
        // technique as every other native patch in this file.
        //
        // Every hand-built trampoline byte sequence below was verified against Iced
        // (a well-known .NET x86-64 disassembler, NuGet package "Iced") in a throwaway
        // scratch console project before ever being written here -- decoding the exact
        // bytes back to readable assembly and confirming each instruction matches
        // intent, catching at least one real bug (a hand-counted jump-offset mistake in
        // the append hook) before it ever reached a real process. Given this file's
        // history of getting native patches wrong in ways that only surfaced as a game
        // crash, that offline check is worth doing for any future patch this
        // structurally involved -- see AGENTS.md.
        public static void PatchBattleUnitListTracking(ManualLogSource log)
        {
            s_battleUnitListMem = VirtualAlloc(IntPtr.Zero, (UIntPtr)224,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (s_battleUnitListMem == IntPtr.Zero)
            {
                log.LogError("BattleUnitListTracking: VirtualAlloc for shared scratch failed, "
                    + "Win32 error " + Marshal.GetLastWin32Error() + " -- NOT installing any hooks.");
                return;
            }

            bool ok1 = PatchBattleUnitListReset(log);
            bool ok2 = PatchBattleUnitListAppend(log);
            bool ok3 = PatchBattleUnitListTerminateReset(log);

            if (ok1 && ok2 && ok3)
                log.LogInfo("BattleUnitListTracking: all 3 hooks installed.");
            else
                log.LogError("BattleUnitListTracking: incomplete (see errors above) -- "
                    + "ally-aware features depending on this list may not identify allies correctly.");
        }

        // Builds the register save/restore + reset-loop bytes shared by hooks 1 and 3
        // (which are otherwise identical apart from which register is safe to use as a
        // scratch bootstrap and how many original bytes get replayed afterward).
        private static void EmitBattleUnitListResetLogic(List<byte> t, byte bootstrapReg)
        {
            EmitMovAbs(t, bootstrapReg, (ulong)R13SaveAddr.ToInt64()); EmitMovStoreIndirect(t, bootstrapReg, R13);
            EmitMovAbs(t, bootstrapReg, (ulong)R14SaveAddr.ToInt64()); EmitMovStoreIndirect(t, bootstrapReg, R14);
            EmitMovAbs(t, bootstrapReg, (ulong)R15SaveAddr.ToInt64()); EmitMovStoreIndirect(t, bootstrapReg, R15);
            t.AddRange(new byte[] { 0x4D, 0x31, 0xF6 });                    // xor r14, r14
            EmitMovAbs(t, R15, (ulong)CurrentBattleUnitListAddr.ToInt64());
            t.AddRange(new byte[] { 0x4D, 0x31, 0xED });                    // xor r13, r13
            int loopPos = t.Count;
            t.AddRange(new byte[] { 0x41, 0x83, 0xFD, 0x18 });              // cmp r13d, 24
            int jgePos = t.Count;
            t.AddRange(new byte[] { 0x0F, 0x8D, 0, 0, 0, 0 });              // jge done (placeholder)
            t.AddRange(new byte[] { 0x4F, 0x89, 0x34, 0xEF });              // mov [r15+r13*8], r14
            t.AddRange(new byte[] { 0x49, 0xFF, 0xC5 });                    // inc r13
            int jmpBackDelta = loopPos - (t.Count + 5);
            t.Add(0xE9); t.AddRange(BitConverter.GetBytes(jmpBackDelta));   // jmp loop
            int donePos = t.Count;
            var jgeBytes = BitConverter.GetBytes(donePos - (jgePos + 6));
            for (int i = 0; i < 4; i++) t[jgePos + 2 + i] = jgeBytes[i];

            EmitMovAbs(t, R15, (ulong)TotalBattleUnitsAddr.ToInt64());
            EmitMovStoreIndirect(t, R15, R14);
            EmitMovAbs(t, bootstrapReg, (ulong)R13SaveAddr.ToInt64()); EmitMovLoadIndirect(t, R13, bootstrapReg);
            EmitMovAbs(t, bootstrapReg, (ulong)R14SaveAddr.ToInt64()); EmitMovLoadIndirect(t, R14, bootstrapReg);
            EmitMovAbs(t, bootstrapReg, (ulong)R15SaveAddr.ToInt64()); EmitMovLoadIndirect(t, R15, bootstrapReg);
        }

        public static bool PatchBattleUnitListReset(ManualLogSource log)
        {
            byte[] pattern = {
                0x33, 0xC0, 0x8B, 0xD8, 0x66, 0x66, 0x66, 0x0F, 0x1F, 0x84,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0x16,
            };
            IntPtr code = ScanModuleForAOB("GameAssembly.dll", pattern, log);
            if (code == IntPtr.Zero)
            {
                log.LogError("PatchBattleUnitListReset: signature not found in GameAssembly.dll "
                    + "-- NOT patching (nothing was written).");
                return false;
            }

            // Overwrite only the first 15 bytes (xor eax,eax / mov ebx,eax / the 11-byte
            // alignment NOP) -- NOT the full 18-byte matched pattern. 15 is the nearest
            // instruction boundary at or past the 14 bytes an indirect jump needs, and
            // it's the CT's own resume point too: its version writes a much smaller
            // 5-byte relative jump here and never touches bytes 15-17 (`mov r10,[rsi]`)
            // at all. A first attempt at this hook copied the CT's REPLAYED byte count
            // (18, including that instruction) without copying its WRITTEN byte count,
            // overwriting all 18 bytes where the CT only ever overwrites 5 -- destroying
            // 13 bytes of original code the CT's own script deliberately leaves alone.
            // That version crashed reproducibly a few instructions past this hook's
            // patch site, with a corrupted register (r10, set by the now-relocated
            // `mov r10,[rsi]`) feeding a bad pointer dereference in code the CT never
            // touches. Stopping at the same boundary the CT stops at -- leaving `mov
            // r10,[rsi]` physically untouched in place, executed from there rather than
            // replayed from this trampoline -- removes that whole discrepancy.
            const int overwriteLength = 15;
            IntPtr resumeAddr = IntPtr.Add(code, overwriteLength);

            var tramp = new List<byte>();
            // RAX is safe as the save/restore bootstrap here specifically because the
            // very first replayed original instruction is `xor eax,eax` -- whatever
            // RAX held on entry is about to be discarded by the game's own code anyway.
            EmitBattleUnitListResetLogic(tramp, RAX);
            tramp.AddRange(pattern.Take(overwriteLength));   // replay only what's actually overwritten
            tramp.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 });
            tramp.AddRange(BitConverter.GetBytes(resumeAddr.ToInt64()));

            return InstallJumpTrampoline(log, "PatchBattleUnitListReset", code, overwriteLength, tramp);
        }

        public static bool PatchBattleUnitListAppend(ManualLogSource log)
        {
            byte[] pattern = {
                0x49, 0x8B, 0x7D, 0x20, 0x4C, 0x8B, 0xF0, 0x48,
                0x85, 0xFF, 0x0F, 0x84, 0x11, 0x03, 0x00, 0x00,
            };
            IntPtr code = ScanModuleForAOB("GameAssembly.dll", pattern, log);
            if (code == IntPtr.Zero)
            {
                log.LogError("PatchBattleUnitListAppend: signature not found in GameAssembly.dll "
                    + "-- NOT patching (nothing was written).");
                return false;
            }

            // The original's last instruction (je rel32, 6 bytes at the tail of the
            // pattern) is position-dependent -- read straight from the pattern's own
            // literal bytes here rather than re-derived, since this signature (unlike
            // the wildcarded ones elsewhere in this file) is entirely fixed.
            int jeRel32 = BitConverter.ToInt32(pattern, 12);
            IntPtr originalJeTarget = IntPtr.Add(code, pattern.Length + jeRel32);
            IntPtr resumeAddr = IntPtr.Add(code, pattern.Length);

            var tramp = new List<byte>();
            // RDI is safe as the bootstrap here: the first replayed instruction
            // (`mov rdi,[r13+0x20]`) overwrites it unconditionally regardless of what
            // it held on entry. RAX must NOT be touched -- it holds the unit pointer
            // this hook appends.
            byte bootstrap = RDI;
            EmitMovAbs(tramp, bootstrap, (ulong)R13SaveAddr.ToInt64()); EmitMovStoreIndirect(tramp, bootstrap, R13);
            EmitMovAbs(tramp, bootstrap, (ulong)R14SaveAddr.ToInt64()); EmitMovStoreIndirect(tramp, bootstrap, R14);
            EmitMovAbs(tramp, bootstrap, (ulong)R15SaveAddr.ToInt64()); EmitMovStoreIndirect(tramp, bootstrap, R15);
            EmitMovAbs(tramp, R15, (ulong)CurrentBattleUnitListAddr.ToInt64());
            EmitMovAbs(tramp, R14, (ulong)TotalBattleUnitsAddr.ToInt64());
            EmitMovLoadIndirect(tramp, R14, R14);                             // r14 = *TotalBattleUnitsAddr
            // Bounds check the CT itself doesn't have: its own append logic writes
            // CurrentBattleUnitList[TotalBattleUnits] and increments unconditionally,
            // with only the RESET loop capped at MaxTrackedBattleUnits. If append ever
            // fires more times than reset resets it, the count grows past the array's
            // 24 slots and this write walks off the end -- straight into R13Save/
            // R14Save/R15Save (allocated immediately after CurrentBattleUnitList),
            // corrupting exactly the registers restored below before replaying
            // `mov rdi,[r13+0x20]`. A crash 30-16 bytes past this hook's patch site,
            // with a corrupted r13 feeding a bad pointer dereference shortly after, is
            // exactly what a real crash here looked like -- add the bounds check the CT
            // itself is missing rather than assume 24 can never be exceeded.
            tramp.AddRange(new byte[] { 0x41, 0x83, 0xFE, (byte)MaxTrackedBattleUnits }); // cmp r14d, 24
            int jgeSkipAppendPos = tramp.Count;
            tramp.AddRange(new byte[] { 0x0F, 0x8D, 0, 0, 0, 0 });            // jge skipAppend (placeholder)
            tramp.AddRange(new byte[] { 0x4B, 0x89, 0x04, 0xF7 });            // mov [r15+r14*8], rax
            EmitMovAbs(tramp, R14, (ulong)TotalBattleUnitsAddr.ToInt64());
            tramp.AddRange(new byte[] { 0x49, 0xFF, 0x06 });                  // inc qword [r14]
            int skipAppendPos = tramp.Count;
            PatchRel32(tramp, jgeSkipAppendPos + 2, skipAppendPos - (jgeSkipAppendPos + 6));
            EmitMovAbs(tramp, bootstrap, (ulong)R13SaveAddr.ToInt64()); EmitMovLoadIndirect(tramp, R13, bootstrap);
            EmitMovAbs(tramp, bootstrap, (ulong)R14SaveAddr.ToInt64()); EmitMovLoadIndirect(tramp, R14, bootstrap);
            EmitMovAbs(tramp, bootstrap, (ulong)R15SaveAddr.ToInt64()); EmitMovLoadIndirect(tramp, R15, bootstrap);

            // Replay the original semantics, with a position-independent equivalent of
            // the final `je rel32` (computed above from the pattern's own literal bytes).
            tramp.AddRange(new byte[] { 0x49, 0x8B, 0x7D, 0x20 });            // mov rdi, [r13+0x20]
            tramp.AddRange(new byte[] { 0x4C, 0x8B, 0xF0 });                  // mov r14, rax
            tramp.AddRange(new byte[] { 0x48, 0x85, 0xFF });                  // test rdi, rdi
            int jnePos = tramp.Count;
            tramp.AddRange(new byte[] { 0x75, 0x00 });                       // jne continueNormal (placeholder)
            tramp.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 });           // jmp [rip+0] -> originalJeTarget
            tramp.AddRange(BitConverter.GetBytes(originalJeTarget.ToInt64()));
            int continueNormalPos = tramp.Count;
            tramp[jnePos + 1] = (byte)(continueNormalPos - (jnePos + 2));
            tramp.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 });           // jmp [rip+0] -> resumeAddr
            tramp.AddRange(BitConverter.GetBytes(resumeAddr.ToInt64()));

            return InstallJumpTrampoline(log, "PatchBattleUnitListAppend", code, pattern.Length, tramp);
        }

        public static bool PatchBattleUnitListTerminateReset(ManualLogSource log)
        {
            var contextType = Type.GetType("Battle.Context, Assembly-CSharp");
            if (contextType == null)
            {
                log.LogError("PatchBattleUnitListTerminateReset: Battle.Context type not found -- NOT patching.");
                return false;
            }
            IntPtr code = ResolveNativeCode(contextType, "Terminate", log);
            if (code == IntPtr.Zero) return false;

            byte[] pattern = {
                0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83,
                0xEC, 0x20, 0x33, 0xD2, 0x48, 0x8B, 0xF9,
            };
            const int scanRange = 100;   // matches the CT's own Terminate+100 window
            var window = new byte[scanRange + pattern.Length];
            Marshal.Copy(code, window, 0, window.Length);
            int foundAt = -1;
            for (int i = 0; i <= scanRange; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                    if (window[i + j] != pattern[j]) { match = false; break; }
                if (match) { foundAt = i; break; }
            }
            if (foundAt < 0)
            {
                log.LogError("PatchBattleUnitListTerminateReset: expected instruction sequence not "
                    + "found near Terminate() -- NOT patching (nothing was written).");
                log.LogError($"  resolved code address: 0x{code.ToInt64():x}");
                log.LogError("  first 64 bytes there: "
                    + BitConverter.ToString(window, 0, Math.Min(64, window.Length)).Replace("-", " "));
                return false;
            }

            IntPtr patchAddr = IntPtr.Add(code, foundAt);
            IntPtr resumeAddr = IntPtr.Add(patchAddr, pattern.Length);

            var tramp = new List<byte>();
            // RDX is the only safe bootstrap register here: RBX and RCX are both READ
            // by the replayed original instructions (must keep their entry values),
            // and RDI's entry value gets pushed to the stack (also must be preserved).
            // RDX is the one register the replayed code overwrites unconditionally
            // (`xor edx,edx`), so its entry value is the one nothing depends on.
            EmitBattleUnitListResetLogic(tramp, RDX);
            tramp.AddRange(pattern);   // replay the original 15 bytes verbatim
            tramp.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 });
            tramp.AddRange(BitConverter.GetBytes(resumeAddr.ToInt64()));

            return InstallJumpTrampoline(log, "PatchBattleUnitListTerminateReset",
                patchAddr, pattern.Length, tramp);
        }

        /// Shared tail for any trampoline-based patch in this file: allocate the
        /// trampoline, copy it in, then overwrite the matched region at the patch site
        /// with an indirect jump to it, padded with NOPs to the region's exact length.
        private static bool InstallJumpTrampoline(ManualLogSource log, string name,
            IntPtr patchAddr, int regionLength, List<byte> tramp)
        {
            IntPtr trampMem = VirtualAlloc(IntPtr.Zero, (UIntPtr)tramp.Count,
                MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (trampMem == IntPtr.Zero)
            {
                log.LogError($"{name}: VirtualAlloc failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return false;
            }
            Marshal.Copy(tramp.ToArray(), 0, trampMem, tramp.Count);

            var redirect = new List<byte>();
            redirect.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 });
            redirect.AddRange(BitConverter.GetBytes(trampMem.ToInt64()));
            while (redirect.Count < regionLength) redirect.Add(0x90);

            if (!VirtualProtect(patchAddr, (UIntPtr)redirect.Count,
                                PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.LogError($"{name}: VirtualProtect failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return false;
            }
            try
            {
                Marshal.Copy(redirect.ToArray(), 0, patchAddr, redirect.Count);
            }
            finally
            {
                VirtualProtect(patchAddr, (UIntPtr)redirect.Count, oldProtect, out _);
            }

            log.LogInfo($"{name}: patched {regionLength} bytes at 0x{patchAddr.ToInt64():x}, "
                + $"trampoline at 0x{trampMem.ToInt64():x}");
            return true;
        }

        // Community CT: "Can Attack Any Enemy From Any Row" -- id 445, five separate
        // boolean validation entry points (a sixth, GetSkillRange, is deliberately left
        // untouched: an earlier attempt at patching it returned a synthetic range object
        // and softlocked the turn resolver in testing, matching the CT's own target list
        // exactly -- CT's AOB6 targets it too, but nothing here or in the CT itself
        // claims that one is safe to blanket-patch the way the other five are).
        //
        // A first attempt at all five (superseded by this one) made every one of them
        // return true UNCONDITIONALLY -- for every unit, ally or enemy. That's a much
        // bigger behavior change than the CT actually makes: it forces true only when
        // the relevant unit is specifically an ally (via CurrentBattleUnitList, see
        // PatchBattleUnitListTracking above), and otherwise falls through to the game's
        // real range-checking logic untouched. Removing range validation for BOTH sides
        // rather than just the player's is the likely reason that unconditional version
        // softlocked GetSkillRange and behaved oddly elsewhere. This replicates the CT's
        // actual conditional design instead, verified byte-for-byte against Iced (see
        // the note on PatchBattleUnitListTracking) before ever touching a real process.
        //
        // The five targets split into two shapes, matching the CT's own AOB1-AOB5:
        //   - Three (AOB1-3) check the CALLER (rcx) -- found ally => return true.
        //   - Two (AOB4-5) check the TARGET (rdx) -- found ally => fall through
        //     (normal restrictions still apply when reaching an ally, e.g. for heals);
        //     NOT found (i.e. an enemy) => return true. Inverted from the first three.
        public static void PatchAnyRowAttacks(ManualLogSource log)
        {
            var type = Type.GetType("Battle.Command.CommandValidation, Assembly-CSharp");
            if (type == null) { log.LogError("PatchAnyRowAttacks: CommandValidation type not found."); return; }
            if (s_battleUnitListMem == IntPtr.Zero)
            {
                log.LogError("PatchAnyRowAttacks: battle-unit-list tracking wasn't installed "
                    + "-- NOT patching (nothing to check allies against).");
                return;
            }

            // Resolved by the EXACT field Il2CppInterop generated for each overload --
            // NOT by scanning every NativeMethodInfoPtr_ field on CommandValidation for
            // a byte-pattern match, which a first attempt at this used. That approach
            // never verified WHICH method it was actually finding, and offline
            // inspection of this game's real interop assembly (see AGENTS.md) confirmed
            // a genuine ambiguity risk: two of these five targets --
            // IsAttackableArea(ISceneUnit,RangeType,ILocationMap,MasterBundle) and
            // IsAttackableArea(ISceneUnit,IRestrictRule,ILocationMap,MasterBundle) --
            // compile to prologues differing by exactly ONE byte (which register, rsi
            // or rdi, gets saved first). A byte-scan across every same-shaped field on a
            // large validation class is not a safe way to tell those apart.
            //
            // Il2CppInterop names each method's native-info field with its FULL
            // parameter list baked in (confirmed by loading Assembly-CSharp.dll offline
            // and enumerating the real field names -- see
            // scratchpad/inspectmethods in this project's history), e.g.
            // "NativeMethodInfoPtr_IsAttackableArea_Public_Static_Boolean_ISceneUnit_
            // RangeType_ILocationMap_MasterBundle_0". Matching that exact fragment is as
            // unambiguous as the CT's own findMethodAddrBySignature (assembly + class +
            // method + parameter types), just expressed as a field-name match instead of
            // Cheat Engine's own metadata API. The old byte pattern is kept as a
            // fail-safe sanity check after resolution, not as the primary discriminator.
            var targetsSpec = new (string FieldFragment, byte[] ExpectedBytes, int Wildcard, byte CheckReg, bool FoundAllyMeansReturnTrue)[] {
                ("IsReachableRange_Public_Static_Boolean_ISceneUnit_ISceneUnit_RangeType_ILocationMap_ITargetableUnitMap_MasterBundle",
                 new byte[] {0x48,0x89,0x5C,0x24,0x18,0x48,0x89,0x6C,0x24,0x20,0x56,0x41,0x56,0x41,0x57,0x48,0x83,0xEC,0x00}, 18, RCX, true),
                ("IsAttackableArea_Public_Static_Boolean_ISceneUnit_RangeType_ILocationMap_MasterBundle",
                 new byte[] {0x48,0x89,0x5C,0x24,0x10,0x48,0x89,0x6C,0x24,0x18,0x48,0x89,0x74,0x24,0x20}, -1, RCX, true),
                ("IsAttackableArea_Public_Static_Boolean_ISceneUnit_IRestrictRule_ILocationMap_MasterBundle",
                 new byte[] {0x48,0x89,0x5C,0x24,0x10,0x48,0x89,0x6C,0x24,0x18,0x48,0x89,0x7C,0x24,0x20}, -1, RCX, true),
                ("IsReachableRange_Public_Static_Boolean_IReadOnlyList_1_ISceneUnit_ISceneUnit_RangeType_ILocationMap_ITargetableUnitMap_MasterBundle",
                 new byte[] {0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x10,0x48,0x89,0x74,0x24,0x18}, -1, RDX, false),
                ("IsReachableRange_Public_Static_Boolean_IUnitProvider_ISceneUnit_RangeType_ILocationMap_IVirtualLocationState_MasterBundle",
                 new byte[] {0x48,0x89,0x5C,0x24,0x20,0x55,0x57,0x41,0x54,0x41,0x55,0x41,0x57,0x48,0x83,0xEC,0x00}, 16, RDX, false),
            };
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(IntPtr) && f.Name.StartsWith("NativeMethodInfoPtr_"))
                .ToList();
            var targets = new List<(IntPtr Code, byte[] OriginalBytes, byte CheckReg, bool FoundAllyMeansReturnTrue)>();
            foreach (var spec in targetsSpec)
            {
                var field = fields.FirstOrDefault(f => f.Name.Contains(spec.FieldFragment));
                if (field == null)
                {
                    log.LogError($"PatchAnyRowAttacks: no field matching '{spec.FieldFragment}' "
                        + "-- NOT patching.");
                    break;
                }
                IntPtr info = (IntPtr)field.GetValue(null);
                if (info == IntPtr.Zero)
                {
                    log.LogError($"PatchAnyRowAttacks: {field.Name}'s native method info pointer "
                        + "is null -- NOT patching.");
                    break;
                }
                IntPtr code = Marshal.ReadIntPtr(info);
                if (code == IntPtr.Zero)
                {
                    log.LogError($"PatchAnyRowAttacks: {field.Name}'s methodPointer is null -- NOT patching.");
                    break;
                }
                var bytes = new byte[spec.ExpectedBytes.Length];
                Marshal.Copy(code, bytes, 0, bytes.Length);
                bool match = bytes.Where((b, i) => i != spec.Wildcard)
                    .SequenceEqual(spec.ExpectedBytes.Where((b, i) => i != spec.Wildcard));
                if (!match)
                {
                    log.LogError($"PatchAnyRowAttacks: {field.Name} resolved, but its compiled prologue "
                        + "didn't match the expected signature -- NOT patching (nothing was written). "
                        + "Bytes: " + BitConverter.ToString(bytes).Replace("-", " "));
                    break;
                }
                targets.Add((code, bytes, spec.CheckReg, spec.FoundAllyMeansReturnTrue));
            }
            if (targets.Count != targetsSpec.Length)
            {
                log.LogError($"PatchAnyRowAttacks: resolved {targets.Count}/{targetsSpec.Length} "
                    + "validation paths -- NOT patching.");
                return;
            }

            // Each target gets its own 40-byte R8-R12 save area (5 qword slots), matching
            // the CT's own per-target scratch rather than one shared area -- these five
            // checks could plausibly nest (a skill's range check triggering another
            // lookup), and separate scratch avoids that risk entirely. An extra 520
            // bytes at the end (8-byte write index + a 256-entry ring buffer, 2 bytes
            // each) is diagnostic scratch -- see EmitRowAttackDiagWrite.
            int diagOffset = targets.Count * 40;
            IntPtr scratch = VirtualAlloc(IntPtr.Zero, (UIntPtr)(diagOffset + 8 + 512),
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (scratch == IntPtr.Zero)
            {
                log.LogError("PatchAnyRowAttacks: VirtualAlloc for scratch failed, Win32 error "
                    + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return;
            }
            s_rowAttackDiagBase = IntPtr.Add(scratch, diagOffset);

            int installed = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                IntPtr baseAddr = IntPtr.Add(scratch, i * 40);
                IntPtr resumeAddr = IntPtr.Add(t.Code, t.OriginalBytes.Length);
                var tramp = BuildRowAttackTrampoline(i, t.CheckReg, t.FoundAllyMeansReturnTrue,
                    t.OriginalBytes, resumeAddr, baseAddr);
                if (InstallJumpTrampoline(log, $"PatchAnyRowAttacks[{i}]", t.Code, t.OriginalBytes.Length, tramp))
                    installed++;
            }

            if (installed == targets.Count)
                log.LogInfo($"PatchAnyRowAttacks: patched all {targets.Count} ally-conditional "
                    + "range-validation paths.");
            else
                log.LogError($"PatchAnyRowAttacks: only {installed}/{targets.Count} paths installed "
                    + "-- see errors above.");
        }

        // Diagnostic recorder for the row-attack trampolines -- a plain unmanaged ring
        // buffer (256 entries of siteIndex+foundAlly, 2 bytes each) plus an 8-byte write
        // index, both pure native memory with no managed involvement at all. Added to
        // chase a softlock reported on the enemy's turn, after the crash-causing
        // overwrite-footprint bug (see PatchBattleUnitListReset) was already fixed and
        // confirmed working for the player's side.
        //
        // A first attempt at this logged via a managed UnmanagedCallersOnly callback
        // from inside each trampoline -- the same technique used successfully elsewhere
        // in this file (PatchTryRandomStart, PatchRohanHeal's earlier design). It
        // crashed inside coreclr.dll itself on a LATER invocation, after at least one
        // earlier call from the same trampoline had already logged successfully. That
        // rules out a byte-level encoding mistake (the call-collision bug fixed
        // elsewhere in this file was real and is fixed, but distinct from this) and
        // points at something structural: calling into managed code from arbitrary
        // native call sites scattered through the game's own hot execution path isn't
        // GC-safe or thread-safe the way Harmony's own IL2CPP patches are (Harmony
        // handles the transition properly; this file's hand-built calls do not). Rather
        // than fight that, this records diagnostics with zero managed calls from the
        // hot path at all, and reads them out later via an ordinary Harmony postfix
        // (see RowAttackDiag_Dump_Patch below) -- the same safe, already-proven
        // mechanism every OTHER managed touchpoint in this plugin uses.
        private static IntPtr s_rowAttackDiagBase = IntPtr.Zero;
        private static IntPtr RowAttackDiagIndexAddr => s_rowAttackDiagBase;
        private static IntPtr RowAttackDiagBufferAddr => IntPtr.Add(s_rowAttackDiagBase, 8);

        /// Emits: read the write index, wrap it to 0-255, write (siteIndex, foundAlly)
        /// at that ring-buffer slot, then increment the index. Only ever touches RAX and
        /// R11 -- both confirmed safe at every call site this is used from (RAX is never
        /// read by any of the five targets' replayed prologue bytes; R11 is never a
        /// parameter register under the Windows x64 convention).
        private static void EmitRowAttackDiagWrite(List<byte> t, int siteIndex, int foundAlly)
        {
            EmitMovAbs(t, R11, (ulong)RowAttackDiagIndexAddr.ToInt64());
            EmitMovLoadIndirect(t, RAX, R11);                               // rax = index
            t.AddRange(new byte[] { 0x48, 0x81, 0xE0, 0xFF, 0x00, 0x00, 0x00 }); // and rax, 0xFF
            EmitMovAbs(t, R11, (ulong)RowAttackDiagBufferAddr.ToInt64());
            t.AddRange(new byte[] { 0x41, 0xC6, 0x04, 0x43, (byte)siteIndex });      // mov byte [r11+rax*2], siteIndex
            t.AddRange(new byte[] { 0x41, 0xC6, 0x44, 0x43, 0x01, (byte)foundAlly }); // mov byte [r11+rax*2+1], foundAlly
            EmitMovAbs(t, R11, (ulong)RowAttackDiagIndexAddr.ToInt64());
            t.AddRange(new byte[] { 0x49, 0xFF, 0x03 });                    // inc qword [r11]
        }

        // Reads out whatever's new in the ring buffer since the last call and logs it.
        // Called from RowAttackDiag_Dump_Patch's ordinary Harmony postfix -- pure
        // managed code reading pure native memory, no native-to-managed call involved
        // at all, so none of the GC/thread-safety concerns above apply here.
        private static long s_rowAttackDiagLastDumped = 0;
        internal static void DumpRowAttackDiagIfAny(ManualLogSource log)
        {
            if (s_rowAttackDiagBase == IntPtr.Zero) return;
            long currentCount = Marshal.ReadInt64(RowAttackDiagIndexAddr);
            if (currentCount == s_rowAttackDiagLastDumped) return;
            long newEntries = currentCount - s_rowAttackDiagLastDumped;
            if (newEntries > 256) newEntries = 256;   // ring buffer only retains the most recent 256
            var sb = new System.Text.StringBuilder();
            for (long i = currentCount - newEntries; i < currentCount; i++)
            {
                int slot = (int)(((i % 256) + 256) % 256);
                byte site = Marshal.ReadByte(RowAttackDiagBufferAddr, slot * 2);
                byte found = Marshal.ReadByte(RowAttackDiagBufferAddr, slot * 2 + 1);
                sb.Append($"[{site}:{(found != 0 ? "Y" : "N")}] ");
            }
            log.LogInfo($"[diag] AnyRowAttacks ({newEntries} new, {currentCount} total): {sb}");
            s_rowAttackDiagLastDumped = currentCount;
        }

        /// Builds one row-attack trampoline: search CurrentBattleUnitList for checkReg
        /// (rcx for a caller-check target, rdx for a target-check one); on a match,
        /// either return true immediately (foundAllyMeansReturnTrue) or fall through to
        /// replay the original bytes and resume, with the NOT-found case doing whichever
        /// the found case didn't. Matches the CT's own AOB1-AOB5 shapes exactly,
        /// including that the immediate-return path does NOT restore R8-R12 first --
        /// verified against Iced to behave identically to the community-tested CT script
        /// this replicates (see the note on PatchAnyRowAttacks above).
        private static List<byte> BuildRowAttackTrampoline(int siteIndex, byte checkReg, bool foundAllyMeansReturnTrue,
            byte[] originalBytes, IntPtr resumeAddr, IntPtr scratchBase)
        {
            IntPtr r8s = scratchBase, r9s = IntPtr.Add(scratchBase, 8), r10s = IntPtr.Add(scratchBase, 16),
                   r11s = IntPtr.Add(scratchBase, 24), r12s = IntPtr.Add(scratchBase, 32);

            var t = new List<byte>();
            EmitMovAbs(t, RAX, (ulong)r8s.ToInt64());  EmitMovStoreIndirect(t, RAX, R8);
            EmitMovAbs(t, RAX, (ulong)r9s.ToInt64());  EmitMovStoreIndirect(t, RAX, R9);
            EmitMovAbs(t, RAX, (ulong)r10s.ToInt64()); EmitMovStoreIndirect(t, RAX, R10);
            EmitMovAbs(t, RAX, (ulong)r11s.ToInt64()); EmitMovStoreIndirect(t, RAX, R11);
            EmitMovAbs(t, RAX, (ulong)r12s.ToInt64()); EmitMovStoreIndirect(t, RAX, R12);

            EmitMovRegToReg(t, R11, checkReg);                              // mov r11, checkReg
            t.AddRange(new byte[] { 0x4D, 0x31, 0xC0 });                    // xor r8, r8
            EmitMovAbs(t, R9, (ulong)TotalBattleUnitsAddr.ToInt64());
            EmitMovLoadIndirect(t, R9, R9);                                 // mov r9, [r9]
            int searchLoopPos = t.Count;
            t.AddRange(new byte[] { 0x45, 0x39, 0xC8 });                    // cmp r8d, r9d
            int jgeAllyNotFoundPos = t.Count;
            t.AddRange(new byte[] { 0x0F, 0x8D, 0, 0, 0, 0 });              // jge AllyNotFound (placeholder)
            EmitMovAbs(t, R10, (ulong)CurrentBattleUnitListAddr.ToInt64());
            t.AddRange(new byte[] { 0x4F, 0x8B, 0x14, 0xC2 });              // mov r10, [r10+r8*8]
            t.AddRange(new byte[] { 0x4D, 0x85, 0xD2 });                    // test r10, r10
            int jeAllyNotFoundPos = t.Count;
            t.AddRange(new byte[] { 0x0F, 0x84, 0, 0, 0, 0 });              // je AllyNotFound (placeholder)
            t.AddRange(new byte[] { 0x4D, 0x39, 0xD3 });                    // cmp r11, r10
            int jeFoundAllyPos = t.Count;
            t.AddRange(new byte[] { 0x0F, 0x84, 0, 0, 0, 0 });              // je FoundAlly (placeholder)
            t.AddRange(new byte[] { 0x41, 0xFF, 0xC0 });                    // inc r8d
            int jmpSearchLoopDelta = searchLoopPos - (t.Count + 5);
            t.Add(0xE9); t.AddRange(BitConverter.GetBytes(jmpSearchLoopDelta));

            int foundAllyPos = t.Count;
            PatchRel32(t, jeFoundAllyPos + 2, foundAllyPos - (jeFoundAllyPos + 6));

            int jmpFoundAllyPlaceholder = -1, jmpAllyNotFoundPlaceholder = -1;
            if (foundAllyMeansReturnTrue)
            {
                // FoundAlly -> return true. Diagnostic: foundAlly=1. No r8-r12 restore
                // here (matches the CT exactly) -- irrelevant now anyway since the
                // diagnostic write only touches rax/r11.
                EmitRowAttackDiagWrite(t, siteIndex, 1);
                t.AddRange(new byte[] { 0x48, 0x33, 0xC0 });                // xor rax, rax
                t.AddRange(new byte[] { 0xB0, 0x01 });                      // mov al, 1
                t.Add(0xC3);                                                // ret
            }
            else
            {
                jmpFoundAllyPlaceholder = t.Count;
                t.Add(0xE9); t.AddRange(new byte[4]);                       // jmp toRestore (placeholder)
            }

            int allyNotFoundPos = t.Count;
            PatchRel32(t, jgeAllyNotFoundPos + 2, allyNotFoundPos - (jgeAllyNotFoundPos + 6));
            PatchRel32(t, jeAllyNotFoundPos + 2, allyNotFoundPos - (jeAllyNotFoundPos + 6));

            if (!foundAllyMeansReturnTrue)
            {
                // AllyNotFound -> return true (the inverted target-check shape). Same
                // no-restore-needed reasoning as the FoundAlly branch above.
                EmitRowAttackDiagWrite(t, siteIndex, 0);
                t.AddRange(new byte[] { 0x48, 0x33, 0xC0 });
                t.AddRange(new byte[] { 0xB0, 0x01 });
                t.Add(0xC3);
            }
            else
            {
                jmpAllyNotFoundPlaceholder = t.Count;
                t.Add(0xE9); t.AddRange(new byte[4]);                       // jmp toRestore (placeholder)
            }

            int toRestorePos = t.Count;
            if (jmpFoundAllyPlaceholder >= 0)
                PatchRel32(t, jmpFoundAllyPlaceholder + 1, toRestorePos - (jmpFoundAllyPlaceholder + 5));
            if (jmpAllyNotFoundPlaceholder >= 0)
                PatchRel32(t, jmpAllyNotFoundPlaceholder + 1, toRestorePos - (jmpAllyNotFoundPlaceholder + 5));

            // Diagnostic: this path means "not found" for a caller-check target
            // (foundAllyMeansReturnTrue -- the found case already returned above) or
            // "found" for a target-check one (the found case falls through here
            // instead, per its inverted logic -- see PatchAnyRowAttacks). Written
            // BEFORE the r8-r12 restore below, deliberately: EmitRowAttackDiagWrite
            // uses r11 as scratch, and r11 is one of the five saved registers -- had
            // this run after the restore, it would clobber r11 right back to garbage
            // moments after correctly restoring it. Nothing about the diagnostic write
            // depends on r8-r12 already being restored, so the ordering is free.
            int foundAllyAtRestore = foundAllyMeansReturnTrue ? 0 : 1;
            EmitRowAttackDiagWrite(t, siteIndex, foundAllyAtRestore);

            EmitMovAbs(t, RAX, (ulong)r8s.ToInt64());  EmitMovLoadIndirect(t, R8, RAX);
            EmitMovAbs(t, RAX, (ulong)r9s.ToInt64());  EmitMovLoadIndirect(t, R9, RAX);
            EmitMovAbs(t, RAX, (ulong)r10s.ToInt64()); EmitMovLoadIndirect(t, R10, RAX);
            EmitMovAbs(t, RAX, (ulong)r11s.ToInt64()); EmitMovLoadIndirect(t, R11, RAX);
            EmitMovAbs(t, RAX, (ulong)r12s.ToInt64()); EmitMovLoadIndirect(t, R12, RAX);

            t.AddRange(originalBytes);
            t.AddRange(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 });
            t.AddRange(BitConverter.GetBytes(resumeAddr.ToInt64()));
            return t;
        }

        private static void PatchRel32(List<byte> t, int pos, int rel32)
        {
            var b = BitConverter.GetBytes(rel32);
            for (int i = 0; i < 4; i++) t[pos + i] = b[i];
        }

        private static void EmitMovRegToReg(List<byte> t, byte destReg, byte srcReg)
        {
            t.Add((byte)(0x48 | (srcReg >= 8 ? 4 : 0) | (destReg >= 8 ? 1 : 0)));
            t.Add(0x89);
            t.Add((byte)(0xC0 | ((srcReg & 7) << 3) | (destReg & 7)));
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

        // Community CT: "Everyone Can Equip Anything (Including Shields)" -- two
        // full-function replacements, both "return true" unconditionally, matching the
        // CT's own newmem/newmem2 exactly (`xor rax,rax` / `mov al,1` / `ret`). Both
        // target methods have exactly one overload each (confirmed via offline
        // reflection against the game's own interop assembly before writing this --
        // see the note on PatchAnyRowAttacks for why that check matters and what goes
        // wrong when it's skipped), so the plain ResolveNativeCode name-prefix lookup
        // used everywhere else in this file is safe here, unlike CommandValidation's
        // heavily-overloaded validation methods.
        public static void PatchCanEquipAnything(ManualLogSource log)
        {
            bool ok1 = PatchCanEquipAnythingTarget(log, "Common.EquipCategoryExtension, Assembly-CSharp",
                "HasCategory", new byte[] { 0x83, 0xE2, 0x1F, 0x44, 0x8B, 0xC9, 0x0F, 0xB6, 0xCA, 0x41, 0xB8, 0x01, 0x00, 0x00, 0x00 }, -1);
            bool ok2 = PatchCanEquipAnythingTarget(log, "UnitEquipmentUtility, Assembly-CSharp",
                "IsEquippable", new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x00 }, 9);

            if (ok1 && ok2)
                log.LogInfo("PatchCanEquipAnything: patched both HasCategory() and IsEquippable().");
            else
                log.LogError($"PatchCanEquipAnything: only {(ok1 ? 1 : 0) + (ok2 ? 1 : 0)}/2 targets "
                    + "patched -- see errors above.");
        }

        private static bool PatchCanEquipAnythingTarget(ManualLogSource log, string typeName,
            string methodName, byte[] expected, int wildcard)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                log.LogError($"PatchCanEquipAnything: {typeName} not found -- NOT patching {methodName}().");
                return false;
            }
            IntPtr code = ResolveNativeCode(type, methodName, log);
            if (code == IntPtr.Zero) return false;

            const int scanRange = 64;   // CT scans +30; this is the function's own prologue
            var window = new byte[scanRange + expected.Length];
            Marshal.Copy(code, window, 0, window.Length);

            int foundAt = -1;
            for (int i = 0; i <= scanRange; i++)
            {
                bool match = true;
                for (int j = 0; j < expected.Length; j++)
                {
                    if (j == wildcard) continue;
                    if (window[i + j] != expected[j]) { match = false; break; }
                }
                if (match) { foundAt = i; break; }
            }

            if (foundAt < 0)
            {
                log.LogError($"PatchCanEquipAnything: expected instruction sequence not found near "
                    + $"{methodName}() -- NOT patching (nothing was written).");
                log.LogError($"  resolved code address: 0x{code.ToInt64():x}");
                log.LogError("  first 64 bytes there: "
                    + BitConverter.ToString(window, 0, Math.Min(64, window.Length)).Replace("-", " "));
                return false;
            }

            IntPtr patchAddr = IntPtr.Add(code, foundAt);
            // xor eax,eax ; mov al,1 ; ret -- always "true", padded with NOPs to the
            // matched region's length. Same shape as PatchLargeUnitsTakeOneSlot, just
            // returning true instead of false.
            var replacement = new List<byte> { 0x33, 0xC0, 0xB0, 0x01, 0xC3 };
            while (replacement.Count < expected.Length) replacement.Add(0x90);

            if (!VirtualProtect(patchAddr, (UIntPtr)replacement.Count,
                                PAGE_EXECUTE_READWRITE, out uint oldProtect))
            {
                log.LogError($"PatchCanEquipAnything: VirtualProtect failed for {methodName}(), "
                    + "Win32 error " + Marshal.GetLastWin32Error() + " -- NOT patching.");
                return false;
            }
            try
            {
                Marshal.Copy(replacement.ToArray(), 0, patchAddr, replacement.Count);
            }
            finally
            {
                VirtualProtect(patchAddr, (UIntPtr)replacement.Count, oldProtect, out _);
            }

            log.LogInfo($"PatchCanEquipAnything: patched {replacement.Count} bytes at "
                + $"{methodName}()+0x{foundAt:x2} (0x{patchAddr.ToInt64():x})");
            return true;
        }
    }
}
