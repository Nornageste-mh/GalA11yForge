using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UnityMonoAnalyzer.Discovery;

public sealed record ExeChoice(string? Path, string Reason, bool Excluded);

/// <summary>
/// 启动 exe 选择规则（教训来自 research/01-norna/REPORT.md:233,246 —— 旧工具把
/// UnityCrashHandler64.exe 当成入口）。
/// 规则优先级：
///   1. `--exe` 覆盖（必须存在且是 .exe）
///   2. 与 `*_Data` 目录同名的 exe（`Windose.exe` ↔ `Windose_Data`）
///   3. 与游戏根目录同名的 exe
///   4. 版本资源里 ProductName 匹配
///   5. 兜底：排除 crash handler / Burst / UnityPlayer 后取体积最大者
/// 永远排除：UnityCrashHandler*、*_BurstDebug*、UnityPlayer.dll 相关、*Crash*。
/// </summary>
public static class EntrySelector
{
    private static readonly string[] AlwaysExclude =
    {
        "unitycrashhandler", "crashhandler", "burstdebug", "unityplayer", "createdump",
        "unity.console", "unity.local", "dpinstaller", "vc_redist", "dxsetup", "uninstall",
    };

    public static bool IsAlwaysExcluded(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant() + Path.GetExtension(fileName).ToLowerInvariant();
        foreach (var e in AlwaysExclude)
            if (n.Contains(e, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>列出目录下全部候选 exe（已排除不可执行者），按文件名排序保证确定性。</summary>
    public static List<string> ListCandidates(string dir)
    {
        var result = new List<string>();
        if (!Directory.Exists(dir)) return result;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch (UnauthorizedAccessException) { return result; }
        catch (IOException) { return result; }
        foreach (var f in files)
            if (!IsAlwaysExcluded(f)) result.Add(f);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// 在数据目录的父目录里挑启动 exe。dataDirName 形如 `Windose_Data`。
    /// </summary>
    public static ExeChoice Pick(string gameDir, string dataDirName, IEnumerable<string>? filesHint = null)
    {
        var stem = dataDirName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
            ? dataDirName[..^5]
            : dataDirName;
        var candidates = filesHint?.ToList() ?? ListCandidates(gameDir);
        candidates = candidates.Where(c => !IsAlwaysExcluded(c)).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

        // 2. 与 <X>_Data 同名的 exe
        foreach (var c in candidates)
            if (string.Equals(Path.GetFileNameWithoutExtension(c), stem, StringComparison.OrdinalIgnoreCase))
                return new ExeChoice(c, $"与数据目录 {dataDirName} 同名", false);

        // 3. 与游戏目录同名的 exe
        var dirName = new DirectoryInfo(gameDir).Name;
        foreach (var c in candidates)
            if (string.Equals(Path.GetFileNameWithoutExtension(c), dirName, StringComparison.OrdinalIgnoreCase))
                return new ExeChoice(c, $"与游戏目录名 {dirName} 相同", false);

        // 4. 版本资源 ProductName
        foreach (var c in candidates)
        {
            var pn = TryGetProductName(c);
            if (!string.IsNullOrEmpty(pn) &&
                (string.Equals(pn, stem, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(pn, dirName, StringComparison.OrdinalIgnoreCase)))
                return new ExeChoice(c, $"版本资源 ProductName={pn}", false);
        }

        // 5. 兜底：体积最大
        if (candidates.Count > 0)
        {
            var biggest = candidates.OrderByDescending(SafeLength).ThenBy(c => c, StringComparer.OrdinalIgnoreCase).First();
            var reason = candidates.Count == 1 ? "目录内唯一可用 exe" : "兜底：体积最大的可用 exe";
            return new ExeChoice(biggest, reason, false);
        }

        return new ExeChoice(null, candidates.Count == 0 ? "目录内没有可用 exe" : "全部 exe 被排除", true);
    }

    private static long SafeLength(string p)
    {
        try { return new FileInfo(p).Length; } catch { return 0; }
    }

    public static string TryGetProductName(string exePath)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            return (vi.ProductName ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>由 exe 反查数据目录：`Windose.exe` → `Windose_Data`（可能不存在）。</summary>
    public static string DataDirForExe(string exePath)
    {
        var stem = Path.GetFileNameWithoutExtension(exePath);
        return Path.Combine(Path.GetDirectoryName(exePath) ?? ".", stem + "_Data");
    }
}

/// <summary>Unity 版本探测：优先 `*_Data/globalgamemanagers`，兜底 UnityPlayer.dll 版本资源。</summary>
public static class UnityVersionDetector
{
    private static readonly Regex FallbackRx = new(@"(\d{4}\.\d+\.\d+[abfp]\d+)", RegexOptions.Compiled);

    public sealed record Result(string Version, string Source, string EvidencePath);

    public static Result Detect(string dataDir, string gameDir)
    {
        var ggm = Path.Combine(dataDir, "globalgamemanagers");
        if (File.Exists(ggm))
        {
            try
            {
                using var fs = File.OpenRead(ggm);
                var buf = new byte[512];
                int n = fs.Read(buf, 0, buf.Length);
                var v = TextPatterns.FindUnityVersion(buf.AsSpan(0, n));
                if (!string.IsNullOrEmpty(v)) return new Result(v, "globalgamemanagers", ggm);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var dir in new[] { gameDir, dataDir })
        {
            var up = Path.Combine(dir, "UnityPlayer.dll");
            if (!File.Exists(up)) continue;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(up);
                var raw = vi.FileVersion ?? vi.ProductVersion ?? "";
                var m = FallbackRx.Match(raw);
                if (m.Success) return new Result(m.Groups[1].Value, "UnityPlayer.dll", up);
            }
            catch { }
        }

        // 最后兜底：与 exe 同目录的 globalgamemanagers（极少数布局）
        return new Result("", "unknown", "");
    }

    /// <summary>IL2CPP 探测：GameAssembly.dll 或 *_Data/il2cpp_data。</summary>
    public static (bool Present, string Evidence) DetectIl2Cpp(string gameDir, string dataDir)
    {
        var ga = Path.Combine(gameDir, "GameAssembly.dll");
        if (File.Exists(ga)) return (true, ga);
        var meta = Path.Combine(dataDir, "il2cpp_data");
        if (Directory.Exists(meta)) return (true, meta);
        var md = Path.Combine(dataDir, "Managed", "Metadata");
        if (Directory.Exists(md)) return (true, md);
        return (false, "");
    }
}
