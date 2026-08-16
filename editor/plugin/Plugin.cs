using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace EiyudenKeyDump
{
    [BepInPlugin("eiyuden.keydump", "Eiyuden Save Key Dumper", "1.0.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource Log;

        // Master switch. Everything this plugin does (all Harmony patches, the crypto
        // capture, the name/rune-hole/role dumps) has already served its purpose -- the
        // key, name tables, and role table are extracted and baked into the save editor.
        // Left at false so the plugin loads (BepInEx will log it) but does nothing: no
        // hooks installed, no reflection scans, no dump-folder writes, on every single
        // save load. Flip back to true if any of that data needs re-capturing (e.g. a
        // game update, or extending the role/stat tables further).
        private const bool Enabled = false;

        // Where the captured key / names / sample payloads land. Override with
        // EIYUDEN_DUMP_DIR; otherwise a "dump" folder beside the game executable.
        internal static string DumpDir =
            Environment.GetEnvironmentVariable("EIYUDEN_DUMP_DIR")
            ?? Path.Combine(Path.GetDirectoryName(
                   System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                   ?? Directory.GetCurrentDirectory()) ?? ".", "EiyudenSaveEditor_dump");

        public override void Load()
        {
            Log = base.Log;
            // Everything meaningful in this method can throw on a BadImageFormatException
            // from IL2CPP interop (confirmed once already, in DumpUnitRoles). One top-level
            // guard means a future failure anywhere in here logs something and lets the
            // rest of the game carry on, rather than the plugin just going silent.
            try { LoadInner(); }
            catch (Exception e) { Log?.LogError("Load() top-level failure: " + e); }
        }

        private void LoadInner()
        {
            if (!Enabled)
            {
                Log.LogInfo("=== Eiyuden key dumper: disabled (Plugin.Enabled = false), "
                    + "no hooks installed ===");
                return;
            }

            Directory.CreateDirectory(DumpDir);
            Log.LogInfo("=== Eiyuden key dumper loading ===");
            Log.LogInfo("dump dir: " + DumpDir);

            var cryptoType = FindType("CryptoHelper");
            if (cryptoType == null)
            {
                Log.LogError("CryptoHelper type NOT FOUND");
                DumpCandidateTypes();
                return;
            }

            Log.LogInfo($"CryptoHelper found: {cryptoType.FullName} (asm {cryptoType.Assembly.GetName().Name})");
            DescribeType(cryptoType);

            var harmony = new Harmony("eiyuden.keydump");

            // The money hook: key + IV arrive here as plain arguments.
            TryPatch(harmony, cryptoType, "CreateAesCryptoServiceProvider",
                     nameof(AesPrefix), prefix: true);

            // Capture plaintext <-> ciphertext pairs around the top-level entry point.
            TryPatch(harmony, cryptoType, "DATA_Encryption", nameof(DataEncPrefix), prefix: true);
            TryPatch(harmony, cryptoType, "DATA_Encryption", nameof(DataEncPostfix), prefix: false);

            // Encryption(data, key, iv) - a second chance at the key if the Aes hook misses.
            TryPatch(harmony, cryptoType, "Encryption", nameof(EncryptionPrefix), prefix: true);

            Log.LogInfo("=== Eiyuden key dumper ready ===");
        }

        private static Type FindType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                var hit = types.FirstOrDefault(t => t != null && t.Name == simpleName);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void DumpCandidateTypes()
        {
            var sb = new StringBuilder();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t?.Name == null) continue;
                    if (t.Name.Contains("Crypt") || t.Name.Contains("Encrypt") ||
                        t.Name.Contains("SaveData"))
                        sb.AppendLine($"{asm.GetName().Name} :: {t.FullName}");
                }
            }
            File.WriteAllText(Path.Combine(DumpDir, "candidate_types.txt"), sb.ToString());
            Log.LogInfo("wrote candidate_types.txt");
        }

        private static void DescribeType(Type t)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"TYPE {t.FullName}");
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance;
            foreach (var m in t.GetMethods(all))
            {
                var ps = string.Join(", ", m.GetParameters()
                          .Select(p => p.ParameterType.Name + " " + p.Name));
                sb.AppendLine($"  {(m.IsStatic ? "static " : "")}{m.ReturnType.Name} {m.Name}({ps})");
            }
            foreach (var f in t.GetFields(all))
                sb.AppendLine($"  FIELD {f.FieldType.Name} {f.Name}");

            var path = Path.Combine(DumpDir, "cryptohelper_members.txt");
            File.WriteAllText(path, sb.ToString());
            Log.LogInfo("wrote " + path);
            Log.LogInfo(sb.ToString());
        }

        private static void TryPatch(Harmony harmony, Type target, string methodName,
                                     string handlerName, bool prefix)
        {
            try
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance;
                var method = target.GetMethods(all).FirstOrDefault(m => m.Name == methodName);
                if (method == null)
                {
                    Log.LogWarning($"method {methodName} not found on {target.Name}");
                    return;
                }

                var handler = new HarmonyMethod(typeof(Plugin).GetMethod(
                    handlerName, BindingFlags.Static | BindingFlags.NonPublic));

                if (prefix) harmony.Patch(method, prefix: handler);
                else harmony.Patch(method, postfix: handler);

                Log.LogInfo($"patched {methodName} ({(prefix ? "prefix" : "postfix")})");
            }
            catch (Exception e)
            {
                Log.LogError($"failed to patch {methodName}: {e}");
            }
        }

        internal static byte[] ToManaged(Il2CppStructArray<byte> a)
        {
            if (a == null) return null;
            var r = new byte[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i];
            return r;
        }

        private static string Hex(byte[] b) =>
            b == null ? "<null>" : BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

        private static string Ascii(byte[] b) =>
            b == null ? "<null>" : new string(b.Select(c => c >= 32 && c < 127 ? (char)c : '.').ToArray());

        private static bool _keyLogged;

        // CreateAesCryptoServiceProvider(byte[] key, byte[] iv)
        private static void AesPrefix(Il2CppStructArray<byte> __0, Il2CppStructArray<byte> __1)
        {
            try
            {
                var key = ToManaged(__0);
                var iv = ToManaged(__1);
                Log.LogWarning($"[AES] key({key?.Length}) = {Hex(key)}   ascii='{Ascii(key)}'");
                Log.LogWarning($"[AES] iv ({iv?.Length}) = {Hex(iv)}   ascii='{Ascii(iv)}'");

                if (!_keyLogged)
                {
                    _keyLogged = true;
                    File.WriteAllText(Path.Combine(DumpDir, "aes_key.txt"),
                        $"key_hex={Hex(key)}\nkey_ascii={Ascii(key)}\n" +
                        $"iv_hex={Hex(iv)}\niv_ascii={Ascii(iv)}\n");
                    Log.LogWarning("wrote aes_key.txt");
                }
            }
            catch (Exception e) { Log.LogError("AesPrefix: " + e); }
        }

        // Encryption(byte[] data, byte[] key, byte[] iv)
        private static void EncryptionPrefix(Il2CppStructArray<byte> __0,
                                             Il2CppStructArray<byte> __1,
                                             Il2CppStructArray<byte> __2)
        {
            try
            {
                var key = ToManaged(__1);
                var iv = ToManaged(__2);
                Log.LogWarning($"[Encryption] key = {Hex(key)}");
                Log.LogWarning($"[Encryption] iv  = {Hex(iv)}");
                File.WriteAllText(Path.Combine(DumpDir, "encryption_key.txt"),
                    $"key_hex={Hex(key)}\nkey_ascii={Ascii(key)}\n" +
                    $"iv_hex={Hex(iv)}\niv_ascii={Ascii(iv)}\n");
            }
            catch (Exception e) { Log.LogError("EncryptionPrefix: " + e); }
        }

        // --- unit id -> character name -------------------------------------------------
        // Dumped from the game's own `GetCharacterName(int)`. Triggered from the save
        // hook rather than a timer: that fires on the main thread with master data and
        // localization already loaded, which is exactly when the lookup is valid.
        private static bool _namesDumped;

        private static void DumpUnitNames()
        {
            if (_namesDumped) return;
            _namesDumped = true;
            try
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance;
                MethodInfo getter = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        MethodInfo m;
                        try { m = t.GetMethods(all).FirstOrDefault(x =>
                                    x.Name == "GetCharacterName" && x.IsStatic &&
                                    x.ReturnType == typeof(string) &&
                                    x.GetParameters().Length == 1 &&
                                    x.GetParameters()[0].ParameterType == typeof(int)); }
                        catch { continue; }
                        if (m != null) { getter = m; break; }
                    }
                    if (getter != null) break;
                }

                if (getter == null)
                {
                    Log.LogWarning("[names] GetCharacterName(int) not found");
                    return;
                }
                Log.LogWarning($"[names] using {getter.DeclaringType?.FullName}.GetCharacterName");

                var sb = new StringBuilder();
                sb.AppendLine("{");
                int found = 0;
                for (int id = 0; id <= 3000; id++)
                {
                    string name;
                    try { name = getter.Invoke(null, new object[] { id }) as string; }
                    catch { continue; }
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (found++ > 0) sb.AppendLine(",");
                    sb.Append($"  \"{id}\": {Quote(name)}");
                    _lastIds.Add(id);
                }
                sb.AppendLine();
                sb.AppendLine("}");

                var path = Path.Combine(DumpDir, "ec_unit_names.json");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Log.LogWarning($"[names] wrote {found} unit names -> {path}");

                DumpUnitRoles(getter.DeclaringType, ids: found > 0 ? _lastIds : null);
                DumpUnitRolesViaMasterData(ids: found > 0 ? _lastIds : null);
                DumpUnitRolesFromTable(ids: found > 0 ? _lastIds : null);
            }
            catch (Exception e) { Log.LogError("DumpUnitNames: " + e); }
        }

        // ids actually seen to have a name, filled in by DumpUnitNames above
        private static readonly System.Collections.Generic.List<int> _lastIds = new();

        // Unit type / battle-support-castle role. Not stored in the save at all (the
        // per-unit record has no such field -- confirmed by inspecting every key in real
        // save records), so this can only come from the game's own master data. Strategy:
        // GetCharacterName(int) lives on some "character master" accessor class; check
        // that same class and its neighbours for a sibling int -> role lookup, log full
        // diagnostics on every candidate found (declaring type, signature, a sample
        // invocation) so the result can be verified rather than guessed at, and only
        // write the final table for whichever candidate's sample values look sane
        // (an enum-shaped, non-exception result for known combat leads like Nowa/id 10).
        // Candidates are int->X getters we're about to blind-invoke with a character id.
        // The role/type/category name pattern already leans read-only, but this is an
        // explicit belt-and-suspenders check: never invoke anything whose name suggests
        // it changes state, regardless of what else about its signature looked promising.
        private static readonly Regex MutatingName = new(
            "^(Set|Add|Remove|Delete|Clear|Reset|Update|Save|Write|Apply|Modify|Change|" +
            "Recruit|Unlock|Kill|Destroy|Create|Give|Grant)",
            RegexOptions.IgnoreCase);

        // The diagnostic run identified the real source:
        //   MasterDataExtension.UnitParamExtensions.CanBattle(IUnitParam)  -- static
        //   MasterDataExtension.UnitParamExtensions.CanSupport(IUnitParam) -- static
        //   GameData.Unit.get_CanBattle / get_CanSupport                  -- instance
        // Both are per-CHARACTER (an IUnitParam / Unit is one character's master data),
        // not per-party -- exactly the classification asked for. What's still missing is
        // the id -> IUnitParam lookup; this searches for it and, if found, uses it to
        // capture CanBattle/CanSupport for every known character in one pass.
        private static void DumpUnitRolesViaMasterData(System.Collections.Generic.List<int> ids)
        {
            try
            {
                if (ids == null || ids.Count == 0) return;
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance;
                var diag = new StringBuilder();
                var deadline = DateTime.UtcNow.AddSeconds(8);
                bool TimedOut() => DateTime.UtcNow > deadline;

                var unitParamExt = FindType("UnitParamExtensions");
                var iUnitParam = FindType("IUnitParam");
                diag.AppendLine($"UnitParamExtensions: {unitParamExt?.FullName}");
                diag.AppendLine($"IUnitParam: {iUnitParam?.FullName}");

                if (unitParamExt == null || iUnitParam == null)
                {
                    diag.AppendLine("required types not found -- aborting");
                    File.WriteAllText(Path.Combine(DumpDir, "unit_role_masterdata_diag.txt"),
                                      diag.ToString());
                    return;
                }

                MethodInfo canBattle = null, canSupport = null;
                foreach (var m in unitParamExt.GetMethods(all))
                {
                    if (!m.IsStatic) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 1 || !iUnitParam.IsAssignableFrom(ps[0].ParameterType)) continue;
                    if (m.Name == "CanBattle") canBattle = m;
                    if (m.Name == "CanSupport") canSupport = m;
                }
                diag.AppendLine($"CanBattle method: {canBattle}");
                diag.AppendLine($"CanSupport method: {canSupport}");

                // Find the id -> IUnitParam lookup: a static method taking a single int and
                // returning something assignable to IUnitParam, searched across the same
                // two assemblies GetCharacterName lives in (kept narrow for the same speed
                // reason as the main role scan).
                MethodInfo lookup = null;
                var scanAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => (a.GetName().Name ?? "").StartsWith("Assembly-CSharp"))
                    .ToArray();
                foreach (var asm in scanAssemblies)
                {
                    if (lookup != null || TimedOut()) break;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null || lookup != null) break;
                        if (TimedOut()) break;
                        MethodInfo[] methods;
                        try { methods = t.GetMethods(all); } catch { continue; }
                        foreach (var m in methods)
                        {
                            try
                            {
                                if (!m.IsStatic || MutatingName.IsMatch(m.Name)) continue;
                                var ps = m.GetParameters();
                                if (ps.Length != 1 || ps[0].ParameterType != typeof(int)) continue;
                                if (!iUnitParam.IsAssignableFrom(m.ReturnType)) continue;
                                lookup = m;
                                diag.AppendLine($"lookup candidate: {t.FullName}.{m.Name}"
                                    + $"(int) -> {m.ReturnType}");
                                break;
                            }
                            catch { /* one bad member shouldn't stop the rest */ }
                        }
                    }
                }

                if (lookup == null || canBattle == null || canSupport == null)
                {
                    diag.AppendLine("missing a required piece");
                    // The narrow "static int -> IUnitParam" search came up empty, so
                    // widen out: log the full member surface of every type whose name
                    // mentions UnitParam (any binding, any signature) so the real access
                    // pattern -- an instance method, a two-arg (id, MasterBundle) form,
                    // a table object obtained some other way -- is visible without
                    // guessing at it blind again.
                    diag.AppendLine("\n=== full UnitParam-related member surface ===");
                    foreach (var asm in scanAssemblies)
                    {
                        if (TimedOut()) { diag.AppendLine("(time budget exceeded)"); break; }
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray(); }
                        catch { continue; }

                        foreach (var t in types)
                        {
                            if (t?.FullName == null) continue;
                            if (!t.FullName.Contains("UnitParam")) continue;
                            diag.AppendLine($"\nTYPE {t.FullName}");
                            try
                            {
                                foreach (var m in t.GetMethods(all))
                                {
                                    try
                                    {
                                        var ps = string.Join(", ", m.GetParameters()
                                                  .Select(p => p.ParameterType.Name));
                                        diag.AppendLine($"  {(m.IsStatic ? "static " : "")}"
                                            + $"{m.ReturnType.Name} {m.Name}({ps})");
                                    }
                                    catch { }
                                }
                                foreach (var f in t.GetFields(all))
                                {
                                    try { diag.AppendLine($"  FIELD {f.FieldType.Name} {f.Name}"); }
                                    catch { }
                                }
                            }
                            catch (Exception e)
                            {
                                diag.AppendLine($"  (member enum failed: {e.GetType().Name})");
                            }
                        }
                    }
                    File.WriteAllText(Path.Combine(DumpDir, "unit_role_masterdata_diag.txt"),
                                      diag.ToString());
                    Log.LogWarning("[roles2] missing lookup/CanBattle/CanSupport -- see diag");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("{");
                int n = 0, ok = 0;
                foreach (var id in ids)
                {
                    try
                    {
                        var param = lookup.Invoke(null, new object[] { id });
                        if (param == null) continue;
                        var battle = (bool)canBattle.Invoke(null, new[] { param });
                        var support = (bool)canSupport.Invoke(null, new[] { param });
                        if (n++ > 0) sb.AppendLine(",");
                        sb.Append($"  \"{id}\": {{\"battle\": {battle.ToString().ToLowerInvariant()}, "
                                 + $"\"support\": {support.ToString().ToLowerInvariant()}}}");
                        ok++;
                    }
                    catch (Exception e)
                    {
                        diag.AppendLine($"id {id} failed: {e.InnerException?.Message ?? e.Message}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("}");

                var path = Path.Combine(DumpDir, "ec_unit_roles.json");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(DumpDir, "unit_role_masterdata_diag.txt"),
                                  diag.ToString());
                Log.LogWarning($"[roles2] wrote {ok}/{ids.Count} unit roles via "
                    + $"{lookup.DeclaringType?.FullName}.{lookup.Name} -> {path}");
            }
            catch (Exception e) { Log.LogError("DumpUnitRolesViaMasterData: " + e); }
        }

        // The widened diagnostic identified the real access path:
        //   MasterData.UnitParamTable : UnityEngine.Object  (get_Item(int), get_Dictionary())
        //   MasterData.IUnitParam.get_UnitType() -- likely the single source CanBattle/
        //     CanSupport are themselves derived from, so both are captured.
        // UnitParamTable is a UnityEngine.Object (it has hideFlags/name/OnEnable), so the
        // standard non-invasive way to reach a loaded master-data table -- rather than
        // hunting for whoever holds a reference to it -- is Resources.FindObjectsOfTypeAll,
        // which returns every loaded instance of a type directly from the engine.
        private static void DumpUnitRolesFromTable(System.Collections.Generic.List<int> ids)
        {
            // Direct, strongly-typed call against the game's own interop assembly --
            // reflection kept guessing wrong about IL2CPP-translated signatures (Type vs
            // Il2CppSystem.Type being the last miss), and this project already references
            // Assembly-CSharp.dll and UnityEngine.CoreModule.dll, so there's no need to
            // reflect at all for a type/method we now know by name.
            var diag = new StringBuilder();
            try
            {
                if (ids == null || ids.Count == 0) return;

                var tables = UnityEngine.Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<MasterData.UnitParamTable>());
                diag.AppendLine($"instances found: {tables?.Length ?? 0}");
                if (tables == null || tables.Length == 0)
                {
                    diag.AppendLine("no loaded UnitParamTable instance -- aborting");
                    return;
                }
                var table = tables[0].Cast<MasterData.UnitParamTable>();

                // table[id] is NOT a lookup by the character's Id field -- it's a
                // positional index into the table's underlying list (confirmed: it
                // succeeded for exactly the first 12 ids tried, 10..120, then failed for
                // every id above 120, matching a ~121-row table indexed 0..120 rather than
                // by real character id). Build the real id -> entry map from the list once.
                // table.List's interface chain (Count/foreach) has behaved inconsistently
                // between compile-time and runtime -- it's IL2CPP's own mirror type, not
                // .NET's, despite sharing a short name. table.Dictionary is a plain
                // Dictionary<int, UnitParam>-shaped object (per the diagnostic dump:
                // ReadOnlyDictionary`2, i.e. two type args = keyed by id already), which
                // interop bindings support far more predictably than list enumeration --
                // and it's already exactly the id -> entry map this needs, no building required.
                var dict = table.Dictionary;
                diag.AppendLine($"table.Dictionary: {dict}");

                var sb = new StringBuilder();
                sb.AppendLine("{");
                int n = 0, ok = 0;
                foreach (var id in ids)
                {
                    try
                    {
                        if (!dict.ContainsKey(id))
                        {
                            diag.AppendLine($"id {id}: not in table");
                            continue;
                        }
                        var param = dict[id];
                        if (param == null) { diag.AppendLine($"id {id}: null param"); continue; }
                        var iparam = param.Cast<MasterData.IUnitParam>();
                        bool battle = MasterDataExtension.UnitParamExtensions.CanBattle(iparam);
                        bool support = MasterDataExtension.UnitParamExtensions.CanSupport(iparam);
                        string unitType = param.UnitType.ToString();
                        if (n++ > 0) sb.AppendLine(",");
                        sb.Append($"  \"{id}\": {{\"battle\": {battle.ToString().ToLowerInvariant()}, "
                                 + $"\"support\": {support.ToString().ToLowerInvariant()}, "
                                 + $"\"unitType\": {Quote(unitType)}}}");
                        ok++;
                    }
                    catch (Exception e)
                    {
                        diag.AppendLine($"id {id} failed: {e.InnerException?.Message ?? e.Message}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("}");

                var path = Path.Combine(DumpDir, "ec_unit_roles.json");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Log.LogWarning($"[roles3] wrote {ok}/{ids.Count} unit roles from "
                    + $"UnitParamTable -> {path}");
            }
            catch (Exception e) { diag.AppendLine("EXCEPTION: " + e); Log.LogError("DumpUnitRolesFromTable: " + e); }
            finally
            {
                try
                {
                    File.WriteAllText(Path.Combine(DumpDir, "unit_role_table_diag.txt"), diag.ToString());
                }
                catch { }
            }
        }

        private static void DumpUnitRoles(Type nameOwner, System.Collections.Generic.List<int> ids)
        {
            try
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Static | BindingFlags.Instance;
                var diag = new StringBuilder();
                diag.AppendLine("=== unit-role candidate search ===");
                diag.AppendLine($"GetCharacterName lives on: {nameOwner?.FullName}");

                // Time budget: if the scan ever drifts back into slow territory (a new
                // assembly, a bigger type set), stop and write what's found so far rather
                // than risk another silent no-output run. 8s is generous for a
                // single-assembly scan that normally finishes in well under one.
                var deadline = DateTime.UtcNow.AddSeconds(8);
                bool TimedOut() => DateTime.UtcNow > deadline;

                var candidates = new System.Collections.Generic.List<(MethodInfo m, string why)>();

                // 1) sibling static int->X methods on the same class as GetCharacterName.
                //    IL2CPP interop types can throw BadImageFormatException out of plain
                //    reflection calls (confirmed: this exact call did, aborting the whole
                //    function before anything got written) -- guarded per-method like
                //    every other reflection call in this file, not just around the loop,
                //    since the throw can come from GetMethods() itself too.
                if (nameOwner != null)
                {
                    MethodInfo[] siblingMethods;
                    try { siblingMethods = nameOwner.GetMethods(all); }
                    catch (Exception e)
                    {
                        siblingMethods = Array.Empty<MethodInfo>();
                        diag.AppendLine($"  sibling scan of {nameOwner.FullName} failed: "
                            + $"{e.GetType().Name}: {e.Message}");
                    }
                    foreach (var m in siblingMethods)
                    {
                        try
                        {
                            if (!m.IsStatic) continue;
                            var ps = m.GetParameters();
                            if (ps.Length != 1 || ps[0].ParameterType != typeof(int)) continue;
                            if (m.ReturnType == typeof(string) || m.ReturnType == typeof(void)) continue;
                            if (!MutatingName.IsMatch(m.Name) &&
                                Regex.IsMatch(m.Name, "Type|Battle|Support|Role|Category|Combat",
                                              RegexOptions.IgnoreCase))
                                candidates.Add((m, "sibling of GetCharacterName"));
                        }
                        catch { /* one bad member shouldn't stop the rest */ }
                    }
                }

                // 2) game assemblies only: static int->X methods with a role-shaped name,
                //    OR the exact CanBattle/CanSupport properties found in metadata (any
                //    binding, any parameter count -- log what they are). Deliberately NOT
                //    every loaded assembly: scanning UnityEngine.*/Il2CppSystem.*/etc via
                //    IL2CPP reflection is what made the first attempt at this too slow to
                //    finish before the game closed. GetCharacterName itself lives in
                //    Assembly-CSharp (MiniGameCommon.MiniGameUseful), so that -- plus its
                //    firstpass counterpart -- is where a sibling role lookup would be too.
                var scanAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => (a.GetName().Name ?? "").StartsWith("Assembly-CSharp"))
                    .ToArray();
                diag.AppendLine($"scanning {scanAssemblies.Length} assemblies: "
                    + string.Join(", ", scanAssemblies.Select(a => a.GetName().Name)));

                foreach (var asm in scanAssemblies)
                {
                    if (TimedOut())
                    {
                        diag.AppendLine("  time budget exceeded, stopping scan early");
                        break;
                    }
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        if (TimedOut()) break;
                        MethodInfo[] methods;
                        try { methods = t.GetMethods(all); } catch { continue; }
                        foreach (var m in methods)
                        {
                            // every member access below can throw on an IL2CPP interop
                            // type (BadImageFormatException, confirmed elsewhere in this
                            // function) -- one bad method must not drop the rest of the type
                            try
                            {
                                if (m.Name == "CanBattle" || m.Name == "get_CanBattle" ||
                                    m.Name == "CanSupport" || m.Name == "get_CanSupport")
                                {
                                    var ps = m.GetParameters();
                                    diag.AppendLine($"  found {t.FullName}.{m.Name}"
                                        + $"  static={m.IsStatic}  returns={m.ReturnType}"
                                        + $"  params=({string.Join(",", ps.Select(p => p.ParameterType.Name))})");
                                }
                                if (m.IsStatic)
                                {
                                    var ps = m.GetParameters();
                                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int) &&
                                        m.ReturnType != typeof(string) && m.ReturnType != typeof(void) &&
                                        !MutatingName.IsMatch(m.Name) &&
                                        Regex.IsMatch(m.Name, "^(Get)?(Unit)?(Type|Role|Category)$",
                                                      RegexOptions.IgnoreCase) &&
                                        !candidates.Any(c => c.m == m))
                                        candidates.Add((m, $"pattern match in {t.FullName}"));
                                }
                            }
                            catch { /* one bad member shouldn't stop the rest */ }
                        }
                    }
                }

                diag.AppendLine($"candidates: {candidates.Count}");
                // flushed now, before any invocation -- so the assembly scan alone is
                // captured on disk even if something below misbehaves
                File.WriteAllText(Path.Combine(DumpDir, "unit_role_diagnostics.txt"),
                                  diag.ToString(), new UTF8Encoding(false));

                var sampleIds = (ids != null && ids.Count > 0)
                    ? ids.Take(6).ToArray() : new[] { 10, 40, 50, 60 };

                object bestResultForTable = null;
                MethodInfo bestMethod = null;

                foreach (var (m, why) in candidates)
                {
                    diag.AppendLine($"\n-- {m.DeclaringType?.FullName}.{m.Name}  ({why})");
                    diag.AppendLine($"   static={m.IsStatic} returns={m.ReturnType} "
                        + $"params=({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                    foreach (var sid in sampleIds)
                    {
                        try
                        {
                            var result = m.Invoke(null, new object[] { sid });
                            diag.AppendLine($"   id {sid}: {result} ({result?.GetType().FullName})");
                            if (result != null) { bestResultForTable = result; bestMethod = m; }
                        }
                        catch (Exception e)
                        {
                            diag.AppendLine($"   id {sid}: threw {e.InnerException?.GetType().Name ?? e.GetType().Name}");
                        }
                    }
                }

                File.WriteAllText(Path.Combine(DumpDir, "unit_role_diagnostics.txt"),
                                  diag.ToString(), new UTF8Encoding(false));
                Log.LogWarning($"[roles] wrote diagnostics ({candidates.Count} candidates) -> "
                               + Path.Combine(DumpDir, "unit_role_diagnostics.txt"));

                // Only write a final table if we found a method that returned real,
                // non-exception values for every sample id -- otherwise this stays
                // diagnostic-only rather than shipping a guessed mapping.
                if (bestMethod != null && ids != null)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("{");
                    int n = 0, ok = 0;
                    foreach (var id in ids)
                    {
                        object result;
                        try { result = bestMethod.Invoke(null, new object[] { id }); }
                        catch { continue; }
                        if (result == null) continue;
                        ok++;
                        if (n++ > 0) sb.AppendLine(",");
                        sb.Append($"  \"{id}\": {Quote(result.ToString())}");
                    }
                    sb.AppendLine();
                    sb.AppendLine("}");
                    var rolePath = Path.Combine(DumpDir, "ec_unit_roles.json");
                    File.WriteAllText(rolePath, sb.ToString(), new UTF8Encoding(false));
                    Log.LogWarning($"[roles] wrote {ok}/{ids.Count} roles via "
                        + $"{bestMethod.DeclaringType?.FullName}.{bestMethod.Name} -> {rolePath}");
                }
            }
            catch (Exception e) { Log.LogError("DumpUnitRoles: " + e); }
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static int _encCount;

        private static void DataEncPrefix(Il2CppStructArray<byte> __0)
        {
            try
            {
                DumpUnitNames();
                var data = ToManaged(__0);
                var n = _encCount++;
                Log.LogWarning($"[DATA_Encryption#{n}] input {data?.Length} bytes, " +
                               $"head={Hex(data?.Take(16).ToArray())}");
                if (data != null)
                    File.WriteAllBytes(Path.Combine(DumpDir, $"data_enc_{n}_in.bin"), data);
            }
            catch (Exception e) { Log.LogError("DataEncPrefix: " + e); }
        }

        private static void DataEncPostfix(Il2CppStructArray<byte> __result)
        {
            try
            {
                var data = ToManaged(__result);
                var n = _encCount - 1;
                Log.LogWarning($"[DATA_Encryption#{n}] output {data?.Length} bytes, " +
                               $"head={Hex(data?.Take(16).ToArray())}");
                if (data != null)
                    File.WriteAllBytes(Path.Combine(DumpDir, $"data_enc_{n}_out.bin"), data);
            }
            catch (Exception e) { Log.LogError("DataEncPostfix: " + e); }
        }
    }
}
