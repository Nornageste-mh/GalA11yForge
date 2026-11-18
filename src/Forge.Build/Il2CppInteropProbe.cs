using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>一次 IL2CPP interop 前置检查的只读结果。</summary>
public sealed class Il2CppInteropState
{
    public string GameDirectory { get; init; } = string.Empty;

    public string BepInExDirectory { get; init; } = string.Empty;

    public string InteropDirectory { get; init; } = string.Empty;

    /// <summary>BepInEx/core/ 下是否存在 BepInEx 6 的 IL2CPP 核心。</summary>
    public bool HasBepInExCore { get; init; }

    /// <summary>interop 目录里的 DLL 数量。</summary>
    public int InteropAssemblyCount { get; init; }

    public long InteropTotalBytes { get; init; }

    /// <summary>interop 目录是否包含 Il2Cppmscorlib.dll 等关键程序集。</summary>
    public bool HasInteropAssemblies { get; init; }

    /// <summary>是否存在疑似 Cpp2IL / Il2CppInterop dump 产物（只用于提示，不是构建硬需求）。</summary>
    public bool HasDump { get; init; }

    public List<string> Notes { get; init; } = new();

    /// <summary>构建硬前置是否满足：BepInEx 6 核心 + 至少 MinimumInteropAssemblies 个 interop 程序集。</summary>
    public bool ReadyForBuild => HasBepInExCore && HasInteropAssemblies;

    public string Describe()
        => $"BepInEx6 核心={(HasBepInExCore ? "有" : "无")}，interop={InteropAssemblyCount} 个 DLL/{InteropTotalBytes} B，"
           + $"dump={(HasDump ? "有" : "无")}（{InteropDirectory}）";
}

/// <summary>
/// IL2CPP 构建的 interop 前置检查。forge build/install 在生成工程前调用；
/// 缺失时返回 E_DEPENDENCY_MISSING 并给出可执行步骤，绝不让构建以“缺 DLL 的编译错误”糊过去。
/// 依据：research/29-il2cpp-e2e/REPORT.md —— BepInEx 6 首次启动会生成 BepInEx/interop（97 文件/74,424,659 B，约 229 秒）。
/// </summary>
public static class Il2CppInteropProbe
{
    /// <summary>最低 interop DLL 数。实测正常首次启动 97 个；低于 10 视为没生成完或不是 IL2CPP 游戏。</summary>
    public const int MinimumInteropAssemblies = 10;

    public const string CoreAssemblyRelativePath = "BepInEx/core/BepInEx.Unity.IL2CPP.dll";

    public const string CoreMarkerRelativePath = "BepInEx/core/BepInEx.Core.dll";

    public const string InteropRelativePath = "BepInEx/interop";

    public static Il2CppInteropState Probe(string gameDirectory)
    {
        var full = string.IsNullOrWhiteSpace(gameDirectory)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));

        var bepInEx = string.IsNullOrEmpty(full) ? string.Empty : Path.Combine(full, "BepInEx");
        var interop = string.IsNullOrEmpty(full) ? string.Empty : Path.Combine(full, "BepInEx", "interop");
        var notes = new List<string>();

        var hasCore = File.Exists(Path.Combine(bepInEx, "core", "BepInEx.Unity.IL2CPP.dll"))
                      && File.Exists(Path.Combine(bepInEx, "core", "BepInEx.Core.dll"));
        if (!hasCore)
        {
            notes.Add($"缺少 {CoreAssemblyRelativePath} 或 {CoreMarkerRelativePath}：BepInEx 6 还没装进游戏目录。");
        }

        var count = 0;
        long bytes = 0;
        var hasMscorlib = false;
        if (Directory.Exists(interop))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(interop, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    count++;
                    bytes += new FileInfo(file).Length;
                    if (string.Equals(Path.GetFileName(file), "Il2Cppmscorlib.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        hasMscorlib = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notes.Add($"枚举 {InteropRelativePath} 失败：{ex.Message}");
            }
        }
        else
        {
            notes.Add($"缺少 {InteropRelativePath}/：BepInEx 6 还没完成首次启动的 interop 生成。");
        }

        var hasInterop = count >= MinimumInteropAssemblies && hasMscorlib;
        if (count > 0 && !hasInterop)
        {
            notes.Add(count < MinimumInteropAssemblies
                ? $"interop 只有 {count} 个 DLL（<{MinimumInteropAssemblies}），可能生成中断或尚未完成。"
                : "interop 缺少 Il2Cppmscorlib.dll，可能不是完整的 Il2CppInterop 输出。");
        }

        var hasDump = HasDumpArtifacts(full, notes);

        return new Il2CppInteropState
        {
            GameDirectory = full,
            BepInExDirectory = bepInEx,
            InteropDirectory = interop,
            HasBepInExCore = hasCore,
            InteropAssemblyCount = count,
            InteropTotalBytes = bytes,
            HasInteropAssemblies = hasInterop,
            HasDump = hasDump,
            Notes = notes,
        };
    }

    /// <summary>构建前调用：不满足则返回 E_DEPENDENCY_MISSING，并带“下一步做什么”的可复制指引。</summary>
    public static ForgeResult<Il2CppInteropState> RequireForBuild(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return ForgeResult<Il2CppInteropState>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{gameDirectory}",
                "用 --game 指向已安装游戏的目录。"));
        }

        var state = Probe(gameDirectory);
        if (state.ReadyForBuild)
        {
            return ForgeResult<Il2CppInteropState>.Success(state);
        }

        var detail = string.Join("；", state.Notes);
        return ForgeResult<Il2CppInteropState>.Failure(ForgeError.Create(
            ForgeErrorCodes.DependencyMissing,
            $"IL2CPP 构建缺少 interop 程序集，不能生成可编译的补丁工程（{state.Describe()}）。{detail}",
            "请先在游戏副本上完成一次 BepInEx 6 首启（只读原目录）——"
            + "1) 把游戏复制到可写目录；2) 把 BepInEx_win_x64_6.0.0-be.788.zip 解压进游戏目录；"
            + "3) 启动一次游戏，等 BepInEx\\interop 下出现约 97 个 DLL（首次约 4 分钟，日志见 BepInEx\\LogOutput.log）；"
            + "4) 关掉游戏后重跑 forge build。也可以用 --option il2cpp.dumpPath=<dump 文件> 走 dump 分析，但构建仍需要 interop。",
            new Dictionary<string, string>
            {
                ["gameDirectory"] = state.GameDirectory,
                ["interopDirectory"] = state.InteropDirectory,
                ["interopAssemblies"] = state.InteropAssemblyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["bepInExCore"] = state.HasBepInExCore ? "true" : "false",
                ["packageVersion"] = BepInEx6Package.Version,
            }));
    }

    private static bool HasDumpArtifacts(string gameDirectory, List<string> notes)
    {
        if (string.IsNullOrEmpty(gameDirectory))
        {
            return false;
        }

        var candidates = new[]
        {
            Path.Combine(gameDirectory, "BepInEx", "interop", "Il2CppDumper.dump"),
            Path.Combine(gameDirectory, "BepInEx", "interop", "dump.cs"),
            Path.Combine(gameDirectory, "il2cpp.dump"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        try
        {
            if (Directory.Exists(Path.Combine(gameDirectory, "BepInEx", "interop")))
            {
                return Directory.EnumerateFiles(
                    Path.Combine(gameDirectory, "BepInEx", "interop"),
                    "*.dump",
                    SearchOption.TopDirectoryOnly).Any();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"检查 dump 产物失败：{ex.Message}");
        }

        return false;
    }
}
