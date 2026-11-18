using System.Text.RegularExpressions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>一次只读定位得到的游戏布局。</summary>
internal sealed class UnityMonoGameLayout
{
    public required string GameDirectory { get; init; }

    public required string DataDirectory { get; init; }

    public required string PrimaryAssemblyPath { get; init; }

    public required string PrimaryAssemblyFile { get; init; }

    public List<string> AdditionalAssemblyPaths { get; init; } = new();

    public List<string> Notes { get; init; } = new();
}

/// <summary>
/// 只读发现 Unity Mono 程序集。
/// 规则来源：<c>src/experiments/unity-mono-analyzer/src/Discovery/GameDiscovery.cs</c>，
/// 适配器扩展为“主程序集优先 + Plugins/ 多程序集”，并保持确定性排序。
/// </summary>
internal static class UnityMonoDiscovery
{
    private const int MaxSearchDepth = 3;
    private const int MaxAdditionalAssemblies = 16;
    private const long MaxAdditionalAssemblyBytes = 64L * 1024 * 1024;

    private static readonly string[] SkipDirectoryNames =
    {
        "bepinex", "doorstop", "dotnet", "mono", "obj", "bin", ".git", ".vs", "node_modules",
        "backup", "cache", "logs", "crashdumps",
    };

    private static readonly string[] ExcludedAssemblyPrefixes =
    {
        "UnityEngine", "Unity.", "UnityEditor", "System", "mscorlib", "netstandard", "Mono.",
        "Microsoft.", "Newtonsoft.", "0Harmony", "BepInEx", "Il2Cpp", "JetBrains.", "nunit.",
        "xunit.", "coverlet.", "testhost", "Accessibility", "WindowsBase", "PresentationCore",
    };

    public static ForgeResult<UnityMonoGameLayout> Locate(GameContext context)
    {
        if (context is null)
        {
            return ForgeResult<UnityMonoGameLayout>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "GameContext 不能为空。",
                "由 CLI 创建 GameContext 后再调用 analyze。");
        }

        if (string.IsNullOrWhiteSpace(context.GameDirectory))
        {
            return ForgeResult<UnityMonoGameLayout>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "游戏目录不能为空。",
                "使用 --game 指定游戏安装目录（只读）。");
        }

        string gameDirectory;
        try
        {
            gameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<UnityMonoGameLayout>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不合法：{ex.Message}",
                "检查 --game 是否是本机存在的绝对路径。");
        }

        if (!Directory.Exists(gameDirectory))
        {
            return ForgeResult<UnityMonoGameLayout>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{gameDirectory}",
                "确认游戏已安装，且 --game 指向安装目录而不是 exe。");
        }

        var dataDirectories = FindDataDirectories(gameDirectory);
        if (dataDirectories.Count == 0)
        {
            return NoMonoAssembly(gameDirectory);
        }

        var selected = ChooseDataDirectory(gameDirectory, dataDirectories);
        var managedDirectory = Path.Combine(selected, "Managed");
        var primary = Path.Combine(managedDirectory, "Assembly-CSharp.dll");
        if (!File.Exists(primary))
        {
            return NoMonoAssembly(gameDirectory);
        }

        var layout = new UnityMonoGameLayout
        {
            GameDirectory = gameDirectory,
            DataDirectory = selected,
            PrimaryAssemblyPath = primary,
            PrimaryAssemblyFile = Path.GetFileName(primary),
        };

        CollectAdditionalAssemblies(layout, managedDirectory, selected, gameDirectory);
        layout.Notes.Add($"主程序集：{Path.GetRelativePath(gameDirectory, primary)}");
        return ForgeResult<UnityMonoGameLayout>.Success(layout);
    }

    private static List<string> FindDataDirectories(string gameDirectory)
    {
        var results = new List<string>();

        // 允许用户直接传 <Game>_Data 目录。
        if (Path.GetFileName(gameDirectory).EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(gameDirectory, "Managed", "Assembly-CSharp.dll")))
        {
            results.Add(gameDirectory);
            return results;
        }

        // 0.2.61（F3）：不再“顶层找到就返回”，否则启动器目录会挡住 game/ 下的真游戏；
        // 统一用限深 BFS 收集所有候选，再交给 ChooseDataDirectory 打分。
        // 少数启动器把 <Game>_Data 放在下一层；限深 3，确定性排序。
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((gameDirectory, 0));
        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= MaxSearchDepth)
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SkipDirectoryNames.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(child, "Managed", "Assembly-CSharp.dll")))
                {
                    results.Add(child);
                    continue;
                }

                queue.Enqueue((child, depth + 1));
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    internal static string ChooseDataDirectory(string gameDirectory, List<string> candidates)
    {
        // 0.2.61（F3）：根目录可能同时有启动器（SevenDaysLauncher_Data）和真正的游戏
        // （game/SevenDays_Data）。不能只看“有没有根 exe 同名”，要按可运行性和程序集大小打分。
        return candidates
            .Select(c => (Dir: c, Score: ScoreDataDirectory(gameDirectory, c)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Path.GetRelativePath(gameDirectory, x.Dir).Count(ch => ch == Path.DirectorySeparatorChar))
            .ThenBy(x => x.Dir, StringComparer.OrdinalIgnoreCase)
            .First()
            .Dir;
    }

    private static readonly Regex LauncherStemRx = new(
        "(launcher|bootstrapper|setup|install|unins|update|crash|config|start)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>0.2.61：给候选 *_Data 打分——同目录有同名 exe + 有 UnityPlayer + 程序集大 + 层级浅。</summary>
    private static double ScoreDataDirectory(string gameDirectory, string dataDirectory)
    {
        var score = 0.0;
        try
        {
            var stem = Path.GetFileName(dataDirectory)[..^"_Data".Length];
            var parent = Path.GetDirectoryName(dataDirectory) ?? gameDirectory;

            if (File.Exists(Path.Combine(parent, stem + ".exe")))
            {
                score += 40;
            }

            if (File.Exists(Path.Combine(parent, "UnityPlayer.dll")))
            {
                score += 10;
            }

            var assembly = Path.Combine(dataDirectory, "Managed", "Assembly-CSharp.dll");
            if (File.Exists(assembly))
            {
                score += Math.Min(30.0, new FileInfo(assembly).Length / (1024.0 * 1024.0) * 6.0);
            }

            if (LauncherStemRx.IsMatch(stem))
            {
                score -= 60;
            }

            score -= Path.GetRelativePath(gameDirectory, dataDirectory)
                .Count(ch => ch == Path.DirectorySeparatorChar) * 3.0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return double.MinValue;
        }

        return score;
    }

    private static void CollectAdditionalAssemblies(
        UnityMonoGameLayout layout,
        string managedDirectory,
        string dataDirectory,
        string gameDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            layout.PrimaryAssemblyPath,
        };

        void Consider(string path)
        {
            if (layout.AdditionalAssemblyPaths.Count >= MaxAdditionalAssemblies)
            {
                return;
            }

            if (!File.Exists(path) || !seen.Add(path))
            {
                return;
            }

            var name = Path.GetFileName(path);
            if (ExcludedAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            if (length is <= 0 or > MaxAdditionalAssemblyBytes)
            {
                layout.Notes.Add($"跳过附加程序集（大小 {length} B）：{name}");
                return;
            }

            layout.AdditionalAssemblyPaths.Add(path);
        }

        // Unity 默认的 firstpass 程序集（仍是游戏代码）。
        Consider(Path.Combine(managedDirectory, "Assembly-CSharp-firstpass.dll"));

        foreach (var root in new[]
                 {
                     Path.Combine(managedDirectory, "Plugins"),
                     Path.Combine(dataDirectory, "Plugins"),
                     Path.Combine(gameDirectory, "Plugins"),
                 })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var dlls = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
                    .Take(MaxAdditionalAssemblies * 4)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var dll in dlls)
                {
                    Consider(dll);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                layout.Notes.Add($"Plugins 枚举失败（只读跳过）：{ex.Message}");
            }
        }

        layout.AdditionalAssemblyPaths.Sort(StringComparer.OrdinalIgnoreCase);
        layout.Notes.Add(
            layout.AdditionalAssemblyPaths.Count == 0
                ? "未发现 Plugins/ 下的附加游戏程序集。"
                : $"附加程序集 {layout.AdditionalAssemblyPaths.Count} 个（Plugins/ 与 firstpass，最多 {MaxAdditionalAssemblies} 个）。");
    }

    private static ForgeResult<UnityMonoGameLayout> NoMonoAssembly(string gameDirectory)
    {
        var il2cpp = File.Exists(Path.Combine(gameDirectory, "GameAssembly.dll")) ||
                     Directory.Exists(Path.Combine(gameDirectory, "il2cpp_data"));
        if (il2cpp)
        {
            return ForgeResult<UnityMonoGameLayout>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "检测到 Unity IL2CPP（GameAssembly.dll / il2cpp_data），没有可扫描的 Mono Assembly-CSharp.dll。",
                "本适配器只支持 Unity Mono；IL2CPP 需要另一条适配器路线。");
        }

        return ForgeResult<UnityMonoGameLayout>.Failure(
            ForgeErrorCodes.EngineUnsupported,
            $"未在 {gameDirectory} 下找到 *_Data/Managed/Assembly-CSharp.dll。",
            "确认这是 Unity Mono 游戏；如程序集被改名，需要人工模式指定程序集路径。");
    }
}
