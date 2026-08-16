using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
                }
                sb.AppendLine();
                sb.AppendLine("}");

                var path = Path.Combine(DumpDir, "ec_unit_names.json");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Log.LogWarning($"[names] wrote {found} unit names -> {path}");
            }
            catch (Exception e) { Log.LogError("DumpUnitNames: " + e); }
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
