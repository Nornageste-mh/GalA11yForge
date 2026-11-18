namespace UnityMonoAnalyzer.Discovery;

public sealed record DeployedState(bool Deployed, List<string> Evidence);

/// <summary>
/// 在给定目录（递归、限深）里找 Unity Mono 程序集：`&lt;X&gt;_Data\Managed\Assembly-CSharp.dll`。
/// 一个目录可能有多个（启动器 + 真游戏 + 多个 Minisode），全部返回，交给 CandidateRanker 排序。
/// </summary>
public static class GameDiscovery
{
    private static readonly string[] SkipDirs =
    {
        "bepinex", "monobleedingedge", "dotnet", "redist", "__macosx", "$recycle.bin",
        "burstdebuginformation", "_burstdebuginformation_donotship", "steamworks", "windows",
    };

    public const string AssemblyRelativePath = @"Managed\Assembly-CSharp.dll";

    public static List<DiscoveryCandidate> Find(string root, int maxDepth = 3)
    {
        var found = new List<DiscoveryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return found;

        // 输入本身就是 *_Data 目录
        var selfName = new DirectoryInfo(root).Name;
        if (selfName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            TryAdd(root, 0, root, found, seen);

        Walk(root, 0, maxDepth, found, seen);
        return found;
    }

    private static void Walk(string dir, int depth, int maxDepth, List<DiscoveryCandidate> found, HashSet<string> seen)
    {
        if (depth >= maxDepth) return;
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(dir); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        var list = subs.ToList();
        list.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in list)
        {
            var name = Path.GetFileName(sub);
            if (SkipDirs.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;

            if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                // _Data 是叶子：它的父目录即游戏目录
                TryAdd(sub, depth, Path.GetDirectoryName(sub)!, found, seen);
                continue; // 不再深入 _Data 内部
            }
            Walk(sub, depth + 1, maxDepth, found, seen);
        }
    }

    private static void TryAdd(string dataDir, int depth, string gameDir, List<DiscoveryCandidate> found, HashSet<string> seen)
    {
        var asm = Path.Combine(dataDir, AssemblyRelativePath);
        if (!File.Exists(asm)) return;
        var full = Path.GetFullPath(asm);
        if (!seen.Add(full)) return;

        var dataDirName = new DirectoryInfo(dataDir).Name;
        var exe = EntrySelector.Pick(gameDir, dataDirName);
        long size = 0;
        try { size = new FileInfo(asm).Length; } catch { }

        found.Add(new DiscoveryCandidate
        {
            DataDir = Path.GetFullPath(dataDir),
            DataDirName = dataDirName,
            GameDir = Path.GetFullPath(gameDir),
            AssemblyPath = full,
            AssemblySize = size,
            ExePath = exe.Path ?? "",
            ExeReason = exe.Reason,
            Depth = depth,
        });
    }

    public static DeployedState DetectDeployed(string gameDir)
    {
        var ev = new List<string>();
        if (!Directory.Exists(gameDir)) return new DeployedState(false, ev);

        foreach (var marker in new[] { "BepInEx", "BepInExPack", "doorstop_config.ini", ".doorstop_version", "winhttp.dll" })
        {
            var p = Path.Combine(gameDir, marker);
            if (File.Exists(p) || Directory.Exists(p)) ev.Add(marker);
        }
        try
        {
            foreach (var f in Directory.EnumerateFiles(gameDir, "nvdaControllerClient*.dll", SearchOption.TopDirectoryOnly))
                ev.Add(Path.GetFileName(f));
        }
        catch { }

        return new DeployedState(ev.Count > 0, ev);
    }
}
