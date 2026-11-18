// GalA11y Forge — 加载器/构建兼容性只读探针（实验原型，task-33）
//
// 只读：只打开游戏目录里的 dll / 数据文件做元数据读取，不写游戏目录。
// 依赖 Mono.Cecil 读程序集元数据（比字符串搜索可靠：能区分类型/成员/重载）。
//
// 子命令：
//   scan   --root <游戏根目录> [--out <json>] [--csv <csv>]
//   game   --game <游戏目录>   [--json]
//   loader --core <BepInEx core 目录> --game <游戏目录> [--json]
//
// 退出码：0=成功执行；2=参数错误；3=致命错误。

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace GalA11yForge.Experiments.LoaderCompat;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Usage(); return 2; }
            var cmd = args[0].ToLowerInvariant();
            var opts = ParseOptions(args.Skip(1).ToArray());
            return cmd switch
            {
                "scan" => CmdScan(opts),
                "game" => CmdGame(opts),
                "loader" => CmdLoader(opts),
                "contract" => CmdContract(),
                _ => Unknown(cmd)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[fatal] " + ex);
            return 3;
        }
    }

    private static int Unknown(string cmd) { Console.Error.WriteLine("未知子命令: " + cmd); Usage(); return 2; }

    private static void Usage()
    {
        Console.WriteLine("""
        LoaderCompatProbe — 只读的 Unity Mono 构建/加载器兼容性探针

          scan   --root <dir> [--out a.json] [--csv a.csv]   扫描 root 下所有 Unity 游戏
          game   --game <dir> [--json]                      检查单个游戏
          loader --core <BepInEx core> --game <dir> [--prefix BepInEx,MelonLoader,0Harmony] [--json] 用游戏 mscorlib 校验加载器的成员引用
          contract                                          打印内置 API 契约（生成器依赖）
        """);
    }

    // ---------------------------------------------------------------- options

    private static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("多余参数: " + a[i]);
            var key = a[i].Substring(2);
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal)) { d[key] = a[++i]; }
            else d[key] = "true";
        }
        return d;
    }

    private static string? Opt(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : null;

    // ---------------------------------------------------------------- commands

    private static int CmdContract()
    {
        var json = JsonSerializer.Serialize(new { assemblies = ApiContract.RequiredAssemblies, members = ApiContract.Members },
            JsonOut.Indented);
        Console.WriteLine(json);
        return 0;
    }

    private static int CmdGame(Dictionary<string, string> o)
    {
        var dir = Opt(o, "game") ?? throw new ArgumentException("--game 必填");
        var info = GameProbe.Analyze(dir, deep: true);
        var json = JsonSerializer.Serialize(info, JsonOut.Indented);
        Console.WriteLine(json);
        return 0;
    }

    private static int CmdScan(Dictionary<string, string> o)
    {
        var root = Opt(o, "root") ?? throw new ArgumentException("--root 必填");
        if (!Directory.Exists(root)) throw new ArgumentException("目录不存在: " + root);

        var games = new List<GameInfo>();
        foreach (var d in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(d);
            if (name.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.EnumerateFiles(d, "*.exe", SearchOption.TopDirectoryOnly).Any()) continue;
            try
            {
                var info = GameProbe.Analyze(d, deep: true);
                games.Add(info);
                Console.WriteLine(FormatRow(info));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-32} [skip] {ex.GetType().Name}: {ex.Message}");
            }
        }

        var outJson = Opt(o, "out");
        if (outJson != null)
        {
            EnsureParent(outJson);
            File.WriteAllText(outJson, JsonSerializer.Serialize(new
            {
                generatedAt = DateTimeOffset.Now.ToString("o"),
                root,
                count = games.Count,
                contract = new { assemblies = ApiContract.RequiredAssemblies, members = ApiContract.Members },
                games
            }, JsonOut.Indented), new UTF8Encoding(false));
            Console.WriteLine("[out] " + outJson);
        }

        var csv = Opt(o, "csv");
        if (csv != null)
        {
            EnsureParent(csv);
            var sb = new StringBuilder();
            sb.AppendLine(GameInfo.CsvHeader);
            foreach (var g in games) sb.AppendLine(g.ToCsvRow());
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine("[csv] " + csv);
        }
        return 0;
    }

    private static string FormatRow(GameInfo g)
    {
        if (!g.Applicable)
            return string.Format("{0,-28} {1,-12} {2,-10} {3}", Trunc(g.Name, 28),
                Trunc(g.UnityVersion, 12), g.Kind, g.Conclusion);

        var input = g.InputLegacy;
        return string.Format(CultureInfo.InvariantCulture,
            "{0,-28} {1,-12} mscorlib={2,9} GetPEKind={3,-5} input={4,7} anyKeyDown={5,-5} api={6,-7} be5={7}  {8}",
            Trunc(g.Name, 28), Trunc(g.UnityVersion, 12), g.MscorlibSize, g.MscorlibHasGetPEKind,
            input?.Size ?? 0, input?.HasAnyKeyDown, g.ApiReady ? "ok" : "MISSING",
            g.BepInEx5Safe == true ? "SAFE" : "UNSAFE", g.Conclusion);
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "~";

    private static void EnsureParent(string path)
    {
        var p = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(p)) Directory.CreateDirectory(p);
    }

    // 用游戏 Managed 里的程序集校验某个 BepInEx core 的成员引用是否都能解析。
    // 目的：不启动游戏就能判断"这套加载器在这份裁剪 mscorlib 上会不会 MissingMethodException"。
    private static int CmdLoader(Dictionary<string, string> o)
    {
        var core = Opt(o, "core") ?? throw new ArgumentException("--core 必填");
        var game = Opt(o, "game") ?? throw new ArgumentException("--game 必填");
        var g = GameProbe.Analyze(game, deep: true);
        if (g.ManagedDir == null) throw new InvalidOperationException("游戏没有 Managed 目录: " + game);

        var asmIndex = AssemblyIndex.Load(g.ManagedDir);
        var loaderIndex = new LoaderScanResult { CoreDir = core, GameName = g.Name };

        var prefixes = (Opt(o, "prefix") ?? "BepInEx").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dll in Directory.GetFiles(core, "*.dll").OrderBy(x => x))
        {
            var fn = Path.GetFileName(dll);
            if (!prefixes.Any(p => fn.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            var one = LoaderScan.ScanAssembly(dll, asmIndex);
            loaderIndex.Assemblies.Add(one);
            foreach (var m in one.Missing) loaderIndex.Missing.Add(m);
        }

        loaderIndex.TotalChecked = loaderIndex.Assemblies.Sum(a => a.Checked);
        var json = JsonSerializer.Serialize(loaderIndex, JsonOut.Indented);
        if (Opt(o, "json") == "true") Console.WriteLine(json);
        else
        {
            Console.WriteLine($"loader core : {core}");
            Console.WriteLine($"game        : {g.Name}  (mscorlib {g.MscorlibSize:N0} B)");
            foreach (var a in loaderIndex.Assemblies)
                Console.WriteLine($"  {a.AssemblyName,-28} checked={a.Checked,5} missing={a.Missing.Count}");
            Console.WriteLine($"missing refs: {loaderIndex.Missing.Count}");
            foreach (var m in loaderIndex.Missing.Take(40))
                Console.WriteLine($"  - [{m.Assembly}] {m.Type}::{m.Member}  (used by {m.UsedBy})");
        }
        return 0;
    }
}

// ==================================================================== contract

/// <summary>
/// 生成器（Framework/platform + contract）实际依赖的 Unity / BCL API 契约。
/// 每一条都对应生成工程里的一处源码引用；缺失即"原生 build 必然失败"。
/// </summary>
internal static class ApiContract
{
    internal static readonly string[] RequiredAssemblies =
    {
        // 与 src/Forge.Generators/Templates/CsprojTemplate.cs 的 &lt;Reference&gt; 一一对应
        "UnityEngine.dll",
        "UnityEngine.CoreModule.dll",
        "UnityEngine.UIModule.dll",
        "UnityEngine.UI.dll",
        "UnityEngine.InputLegacyModule.dll",
        "UnityEngine.TextRenderingModule.dll",
        "Unity.TextMeshPro.dll",
    };

    internal static readonly ContractMember[] Members =
    {
        new("UnityEngine.Input", "anyKeyDown", "prop", "Platform/KeyEdge.cs:282"),
        new("UnityEngine.Input", "anyKey", "prop", "Platform/KeyEdge.cs:185-190（回退路径注释）"),
        new("UnityEngine.Input", "GetKey", "method", "Platform/KeyEdge.cs:91,398"),
        new("UnityEngine.Input", "GetKeyDown", "method", "Platform/KeyEdge.cs:92, Platform/Hotkey.cs:173"),
        new("UnityEngine.Input", "GetMouseButtonDown", "method", "Platform/KeyEdge.cs:283-285"),
        new("UnityEngine.Time", "realtimeSinceStartup", "prop", "Platform/Nvda.cs:97, Platform/Speech.cs:313"),
        new("UnityEngine.Time", "unscaledTime", "prop", "Platform/KeyEdge.cs:266"),
        new("UnityEngine.Time", "frameCount", "prop", "Platform/KeyEdge.cs:267,278"),
        new("UnityEngine.CanvasGroup", "alpha", "prop", "Platform/UiNav.cs:661"),
        new("UnityEngine.UI.Selectable", "interactable", "prop", "Platform/UiNav.cs:1573,1999"),
        new("UnityEngine.UI.Text", "text", "prop", "Platform/UiNav.cs:1085"),
        new("UnityEngine.EventSystems.EventSystem", "current", "prop", "Platform/UiNav.cs:292,720"),
        new("UnityEngine.EventSystems.EventSystem", "SetSelectedGameObject", "method", "Platform/UiNav.cs:294,307"),
        new("UnityEngine.SceneManagement.SceneManager", "GetActiveScene", "method", "Platform/UiNav.cs:317,1862"),
        new("TMPro.TMP_Text", "text", "prop", "Platform/UiNav.cs:1083"),
        new("UnityEngine.GameObject", "GetComponent", "method", "Platform/UiNav.cs:660"),
    };

    internal sealed record ContractMember(string Type, string Member, string Kind, string UsedBy);
}

// ==================================================================== probe

internal sealed class GameInfo
{
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "";
    public string? MainExe { get; set; }
    public string? DataDir { get; set; }
    public string? ManagedDir { get; set; }
    public string Kind { get; set; } = "unknown"; // mono / il2cpp / unknown
    public string UnityVersion { get; set; } = "";
    public long MscorlibSize { get; set; }
    public string? MscorlibSha256 { get; set; }
    public bool MscorlibHasGetPEKind { get; set; }
    public InputInfo? InputLegacy { get; set; }
    public List<string> MissingAssemblies { get; set; } = new();
    public List<string> MissingApi { get; set; } = new();
    public bool ApiReady { get; set; }
    public bool Applicable => Kind == "mono";
    public bool? BepInEx5Safe { get; set; }
    public bool? BuildReady { get; set; }
    public string Conclusion { get; set; } = "";
    public List<string> Notes { get; set; } = new();

    public const string CsvHeader =
        "game,unity_version,kind,applicable,mscorlib_size,mscorlib_sha256_16,mscorlib_has_GetPEKind," +
        "inputlegacy_size,input_has_anyKeyDown,input_has_anyKey,input_has_GetKey,input_has_GetKeyDown," +
        "missing_assemblies,missing_api,build_ready,bepinex5_safe,conclusion,managed_dir";

    private static string B(bool? v) => v == null ? "n/a" : (v.Value ? "true" : "false");

    public string ToCsvRow()
    {
        static string E(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var i = InputLegacy;
        return string.Join(",",
            E(Name), E(UnityVersion), E(Kind), Applicable.ToString().ToLowerInvariant(),
            MscorlibSize.ToString(CultureInfo.InvariantCulture),
            E((MscorlibSha256 ?? "").Substring(0, Math.Min(16, (MscorlibSha256 ?? "").Length))),
            MscorlibHasGetPEKind.ToString().ToLowerInvariant(),
            (i?.Size ?? 0).ToString(CultureInfo.InvariantCulture),
            (i?.HasAnyKeyDown ?? false).ToString().ToLowerInvariant(),
            (i?.HasAnyKey ?? false).ToString().ToLowerInvariant(),
            (i?.HasGetKey ?? false).ToString().ToLowerInvariant(),
            (i?.HasGetKeyDown ?? false).ToString().ToLowerInvariant(),
            E(string.Join("|", MissingAssemblies)), E(string.Join("|", MissingApi)),
            B(BuildReady), B(BepInEx5Safe), E(Conclusion), E(ManagedDir ?? ""));
    }
}

internal sealed class InputInfo
{
    public long Size { get; set; }
    public bool HasAnyKeyDown { get; set; }
    public bool HasAnyKey { get; set; }
    public bool HasGetKey { get; set; }
    public bool HasGetKeyDown { get; set; }
    public bool HasGetMouseButtonDown { get; set; }
}

internal static class GameProbe
{
    private static readonly Regex UnityVerRe =
        new(@"\b(\d{4}\.\d+\.\d+[abfp]\d+)\b", RegexOptions.Compiled);

    private static readonly Regex UnityVerShortRe =
        new(@"\b(\d{4}\.\d+\.\d+)", RegexOptions.Compiled);

    internal static GameInfo Analyze(string gameDir, bool deep)
    {
        gameDir = Path.GetFullPath(gameDir);
        var info = new GameInfo { Name = Path.GetFileName(gameDir), Dir = gameDir };

        var exes = Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(p => !Path.GetFileName(p).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => new FileInfo(p).Length).ToList();
        info.MainExe = exes.FirstOrDefault();
        if (info.MainExe == null) { info.Kind = "no-exe"; return info; }

        var stem = Path.GetFileNameWithoutExtension(info.MainExe);
        var candidates = new List<string>();
        if (Directory.Exists(Path.Combine(gameDir, stem + "_Data"))) candidates.Add(Path.Combine(gameDir, stem + "_Data"));
        candidates.AddRange(Directory.GetDirectories(gameDir, "*_Data", SearchOption.TopDirectoryOnly));
        info.DataDir = candidates.FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Managed"))
                                                     || Directory.Exists(Path.Combine(d, "il2cpp_data")))
                       ?? candidates.FirstOrDefault();

        if (info.DataDir != null)
        {
            var managed = Path.Combine(info.DataDir, "Managed");
            if (Directory.Exists(managed)) { info.ManagedDir = managed; info.Kind = "mono"; }
            else if (Directory.Exists(Path.Combine(info.DataDir, "il2cpp_data"))) info.Kind = "il2cpp";
        }
        if (info.Kind == "unknown")
            info.Kind = File.Exists(Path.Combine(gameDir, "UnityPlayer.dll")) ? "unity-no-managed" : "non-unity";

        info.UnityVersion = ReadUnityVersion(gameDir, info.DataDir);

        if (info.ManagedDir == null)
        {
            info.Conclusion = info.Kind switch
            {
                "il2cpp" => "IL2CPP：BepInEx 5 不支持，需 BepInEx 6 IL2CPP（本探针不判 mono 兼容）",
                "unity-no-managed" => "Unity 但没有 Managed（异常构建），需人工确认",
                _ => "非 Unity 游戏，探针不适用"
            };
            info.Notes.Add("没有 Managed 目录");
            return info;
        }

        if (!deep) return info;

        var index = AssemblyIndex.Load(info.ManagedDir);

        // ---- mscorlib / BepInEx5 预加载器依赖 ----
        var mscorlib = Path.Combine(info.ManagedDir, "mscorlib.dll");
        if (File.Exists(mscorlib))
        {
            info.MscorlibSize = new FileInfo(mscorlib).Length;
            info.MscorlibSha256 = Sha256(mscorlib);
            var mod = TryRead(mscorlib);
            if (mod != null)
            {
                info.MscorlibHasGetPEKind = mod.GetType("System.Reflection.Module")?.Methods
                    .Any(m => m.Name == "GetPEKind") ?? false;
                mod.Dispose();
            }
        }
        else info.Notes.Add("Managed 下没有 mscorlib.dll");

        // ---- UnityEngine.Input 能力 ----
        var input = index.FindType("UnityEngine.Input");
        if (input == null) info.Notes.Add("找不到类型 UnityEngine.Input");
        else
        {
            var td = input.Value.Type;
            var asmPath = index.AssemblyPath(input.Value.Assembly);
            info.InputLegacy = new InputInfo
            {
                Size = asmPath != null ? new FileInfo(asmPath).Length : 0,
                HasAnyKeyDown = HasProp(td, "anyKeyDown"),
                HasAnyKey = HasProp(td, "anyKey"),
                HasGetKey = HasMethod(td, "GetKey"),
                HasGetKeyDown = HasMethod(td, "GetKeyDown"),
                HasGetMouseButtonDown = HasMethod(td, "GetMouseButtonDown"),
            };
        }

        // ---- 生成工程 csproj 引用的程序集是否都在 ----
        foreach (var asm in ApiContract.RequiredAssemblies)
            if (!File.Exists(Path.Combine(info.ManagedDir, asm))) info.MissingAssemblies.Add(asm);

        // ---- 生成器 API 契约 ----
        foreach (var m in ApiContract.Members)
        {
            var hit = index.FindType(m.Type);
            bool ok;
            if (hit == null) ok = false;
            else ok = m.Kind switch
            {
                "prop" => HasProp(hit.Value.Type, m.Member),
                _ => HasMethod(hit.Value.Type, m.Member),
            };
            if (!ok) info.MissingApi.Add($"{m.Type}.{m.Member} ({m.Kind}) @ {m.UsedBy}");
        }

        info.ApiReady = info.MissingApi.Count == 0 && info.InputLegacy != null;
        info.BepInEx5Safe = info.MscorlibHasGetPEKind;
        info.BuildReady = info.ApiReady && info.MissingAssemblies.Count == 0;
        if (!info.BepInEx5Safe.Value)
            info.Notes.Add("mscorlib 缺 System.Reflection.Module.GetPEKind：BepInEx 5 预加载器会 MissingMethodException");

        info.Conclusion = (info.BuildReady.Value, info.BepInEx5Safe.Value) switch
        {
            (true, true) => "OK：BepInEx5 可加载 + 构建 API 齐全",
            (true, false) => "构建可过，但 BepInEx5 会崩（mscorlib 裁剪）",
            (false, true) => "BepInEx5 可加载，但构建会因缺 API 失败",
            (false, false) => "双失：BepInEx5 会崩 + 构建缺 API",
        };
        return info;
    }

    internal static string ReadUnityVersion(string gameDir, string? dataDir)
    {
        // globalgamemanagers 里存着完整版本串（如 6000.0.49f1 / 2022.3.22f1）
        if (dataDir != null)
        {
            var gg = Path.Combine(dataDir, "globalgamemanagers");
            if (File.Exists(gg))
            {
                try
                {
                    using var fs = File.OpenRead(gg);
                    var len = (int)Math.Min(fs.Length, 4L << 20);
                    var bytes = new byte[len];
                    fs.ReadExactly(bytes, 0, len);
                    var text = Encoding.ASCII.GetString(bytes);
                    var m = UnityVerRe.Match(text);
                    if (m.Success) return m.Groups[1].Value;
                }
                catch { /* ignore */ }
            }
        }
        // 退路：UnityPlayer.dll 的文件版本（通常没有 f1 后缀）
        var up = Path.Combine(gameDir, "UnityPlayer.dll");
        if (File.Exists(up))
        {
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(up);
                var s = fv.FileVersion ?? fv.ProductVersion ?? "";
                var m = UnityVerShortRe.Match(s);
                if (m.Success) return m.Groups[1].Value;
            }
            catch { /* ignore */ }
        }
        return "";
    }

    // 只要求存在（public 实例/静态都算）；生成器用的是实例成员与静态成员混合，
    // 这里不按 static 过滤，否则会误报（2026-11-14 首版即因此误报 6 条）。
    internal static bool HasProp(TypeDefinition td, string name) =>
        td.Properties.Any(p => p.Name == name);

    internal static bool HasMethod(TypeDefinition td, string name) =>
        td.Methods.Any(m => m.Name == name);

    private static ModuleDefinition? TryRead(string path)
    {
        try { return ModuleDefinition.ReadModule(path); }
        catch { return null; }
    }

    internal static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}

// ==================================================================== index

internal sealed class AssemblyIndex
{
    private readonly Dictionary<string, List<(string Asm, TypeDefinition Type)>> _types =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    // (facade 程序集, 类型全名) -> 被转发到的程序集。Unity 的 System.Core.dll / System.dll 是
    // 只有 TypeForwardedTo 的门面，真实类型在 mscorlib；不追转发会把一切泛型/LINQ 引用误报成"缺失"。
    private readonly Dictionary<(string Asm, string Type), string> _forwarders = new();

    internal static AssemblyIndex Load(string managedDir)
    {
        var idx = new AssemblyIndex();
        foreach (var dll in Directory.GetFiles(managedDir, "*.dll"))
        {
            idx._paths[Path.GetFileName(dll)] = dll;
            ModuleDefinition mod;
            try { mod = ModuleDefinition.ReadModule(dll); }
            catch { continue; }
            var asmName = Path.GetFileName(dll);
            foreach (var td in SafeTypes(mod))
            {
                if (!idx._types.TryGetValue(td.FullName, out var list))
                    idx._types[td.FullName] = list = new List<(string, TypeDefinition)>();
                list.Add((asmName, td));
            }
            try
            {
                foreach (var et in mod.ExportedTypes)
                {
                    if (et.Scope is AssemblyNameReference ar && et.FullName != null)
                        idx._forwarders[(asmName, et.FullName)] = ar.Name + ".dll";
                }
            }
            catch { /* 损坏的转发表：忽略 */ }
        }
        return idx;
    }

    private static IEnumerable<TypeDefinition> SafeTypes(ModuleDefinition mod)
    {
        TypeDefinition[] arr;
        try { arr = mod.Types.ToArray(); } catch { yield break; }
        foreach (var t in arr)
        {
            yield return t;
            foreach (var n in Nested(t)) yield return n;
        }
        static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
        {
            foreach (var n in t.NestedTypes)
            {
                yield return n;
                foreach (var x in Nested(n)) yield return x;
            }
        }
    }

    internal (string Assembly, TypeDefinition Type)? FindType(string fullName) =>
        _types.TryGetValue(fullName, out var l) && l.Count > 0 ? l[0] : null;

    internal string? AssemblyPath(string asmFileName) =>
        _paths.TryGetValue(asmFileName, out var p) ? p : null;

    internal TypeDefinition? FindTypeInAssembly(string asmFileName, string fullName) =>
        _types.TryGetValue(fullName, out var l)
            ? l.FirstOrDefault(x => string.Equals(x.Asm, asmFileName, StringComparison.OrdinalIgnoreCase)).Type
            : null;

    // 跟随 TypeForwardedTo：System.Core.dll(门面) -> mscorlib.dll。找不到真实定义才算缺失。
    internal bool TypeExists(string asmFileName, string fullName, out string actualAsm)
        => TypeExists(asmFileName, fullName, out actualAsm, 0);

    private bool TypeExists(string asmFileName, string fullName, out string actualAsm, int depth)
    {
        actualAsm = asmFileName;
        if (_types.TryGetValue(fullName, out var l) &&
            l.Any(x => string.Equals(x.Asm, asmFileName, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (depth < 8 && _forwarders.TryGetValue((asmFileName, fullName), out var next) &&
            _paths.ContainsKey(next))
        {
            if (TypeExists(next, fullName, out actualAsm, depth + 1)) return true;
            actualAsm = asmFileName;
        }
        return false;
    }
}

// ==================================================================== loader scan

internal sealed class LoaderScanResult
{
    public string CoreDir { get; set; } = "";
    public string GameName { get; set; } = "";
    public int TotalChecked { get; set; }
    public List<LoaderAssemblyResult> Assemblies { get; set; } = new();
    public List<MissingRef> Missing { get; set; } = new();
}

internal sealed class LoaderAssemblyResult
{
    public string AssemblyName { get; set; } = "";
    public int Checked { get; set; }
    public List<MissingRef> Missing { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
}

internal sealed class MissingRef
{
    public string Assembly { get; set; } = "";      // 目标程序集（mscorlib.dll 等）
    public string Type { get; set; } = "";
    public string Member { get; set; } = "";
    public string UsedBy { get; set; } = "";
}

internal static class LoaderScan
{
    // 只检查"运行时框架"程序集：加载器自己的依赖（BepInEx.*, MonoMod.*, 0Harmony）不在游戏 Managed 里。
    private static readonly HashSet<string> FrameworkAsm = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "System", "System.Core", "System.Xml", "System.Configuration",
        "System.Drawing", "System.Runtime.Serialization", "Mono.Security", "Mono.Posix",
        "System.Windows.Forms", "System.Data", "System.Numerics",
    };

    internal static LoaderAssemblyResult ScanAssembly(string dll, AssemblyIndex game)
    {
        var res = new LoaderAssemblyResult { AssemblyName = Path.GetFileName(dll) };
        ModuleDefinition mod;
        try { mod = ModuleDefinition.ReadModule(dll); }
        catch (Exception ex)
        {
            res.Missing.Add(new MissingRef { Assembly = "?", Type = "?", Member = "<read failed: " + ex.GetType().Name + ">" });
            return res;
        }

        using (mod)
        {
            var asmRefCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var mr in AllMemberRefs(mod))
            {
                var declType = mr.DeclaringType;
                if (declType == null) continue;
                if (declType is GenericInstanceType git) declType = git.ElementType;
                if (declType is ArrayType || declType is ByReferenceType) continue;

                var scope = declType.Scope;
                var asmName = scope switch
                {
                    AssemblyNameReference a => a.Name,
                    ModuleDefinition m => Path.GetFileNameWithoutExtension(m.Name),
                    _ => null
                };
                if (asmName == null || !FrameworkAsm.Contains(asmName)) continue;

                var file = asmName + ".dll";
                if (!asmRefCache.TryGetValue(file, out var resolvedAsm))
                {
                    resolvedAsm = game.AssemblyPath(file) != null ? file : null;
                    asmRefCache[file] = resolvedAsm;
                }
                if (resolvedAsm == null) continue; // 游戏没带这个框架程序集：不算"裁剪"，见报告说明

                res.Checked++;
                var found = game.TypeExists(resolvedAsm, declType.FullName, out var actualAsm);
                if (found && mr is MethodReference)
                {
                    var td = game.FindTypeInAssembly(actualAsm, declType.FullName);
                    // 门面/转发目标里找不到同名方法才算缺；td 为空说明类型本身在（只是索引没带出来）
                    found = td == null || td.Methods.Any(m => m.Name == mr.Name) ||
                            td.Properties.Any(p => p.Name == mr.Name);
                }
                if (!found)
                {
                    var key = file + "|" + declType.FullName + "|" + mr.Name + "|" + res.AssemblyName;
                    if (res.Seen.Add(key))
                        res.Missing.Add(new MissingRef
                        {
                            Assembly = file,
                            Type = declType.FullName,
                            Member = mr.Name,
                            UsedBy = res.AssemblyName
                        });
                }
            }
        }
        return res;
    }

    private static IEnumerable<MemberReference> AllMemberRefs(ModuleDefinition mod)
    {
        foreach (var t in mod.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (m.HasBody)
                    foreach (var ins in m.Body.Instructions)
                        if (ins.Operand is MemberReference mr) yield return mr;
                foreach (var ov in m.Overrides) yield return ov;
            }
            foreach (var f in t.Fields) yield return f;
            foreach (var p in t.Properties) yield return p;
        }
        foreach (var mr in mod.GetMemberReferences()) yield return mr;
    }
}

// ==================================================================== helpers

internal sealed class JsonOut
{
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
