using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Verify.Baseline;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Build;

/// <summary>
/// 安装前基线：安装流程在第一次写游戏目录之前，把「本次部署目标 + BepInEx 运行期白名单文件」
/// 的路径/存在性/大小/SHA-256 记录到工作区 <c>games/&lt;slug&gt;/05-deploy/baseline.json</c>。
/// 只读游戏目录；回滚后 Verify(Rollback) 会逐文件核对“当前状态 == 安装前状态”。
/// </summary>
public static class DeploymentBaseline
{
    public const string FileName = "baseline.json";

    /// <summary>BepInEx 运行期会自行读写的目录；安装前已有内容必须纳入基线。</summary>
    public static readonly IReadOnlyList<string> BepInExRuntimeDirectoryRoots = new[]
    {
        "BepInEx/cache",
        "BepInEx/config",
        "BepInEx/patchers",
    };

    private static readonly EnumerationOptions BepInExScanOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static string ResolvePath(ForgeWorkspace workspace, string slug)
        => Path.Combine(workspace.StageDirectory(slug, PipelineStage.Deploy), FileName);

    /// <summary>收集需要写入基线的游戏内相对路径（'/' 分隔、去重、排序）。</summary>
    public static ForgeResult<IReadOnlyList<string>> CollectPaths(
        string gameDirectory,
        IEnumerable<string> deploymentPaths)
    {
        string root;
        try
        {
            root = PathSafety.NormalizeRoot(gameDirectory);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<IReadOnlyList<string>>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不合法：{ex.Message}",
                "检查路径中是否含有非法字符。"));
        }

        if (!Directory.Exists(root))
        {
            return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{root}",
                "确认游戏已安装，并传入正确的目录。"));
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var raw in deploymentPaths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArgumentInvalid,
                        "部署计划包含空的文件路径。",
                        "重新生成部署计划。"));
                }

                paths.Add(PathSafety.ValidateRelative(raw));
            }

            var bepInEx = Path.Combine(root, "BepInEx");
            if (Directory.Exists(bepInEx))
            {
                // BepInEx 自身是指向别处的链接时，枚举会读到游戏目录之外，直接拒绝。
                PathGuard.EnsureNoReparsePoints(root, bepInEx);

                foreach (var log in Directory.EnumerateFiles(bepInEx, "*.log", SearchOption.TopDirectoryOnly))
                {
                    paths.Add(RelativeOf(root, log));
                }

                foreach (var relativeDirectory in BepInExRuntimeDirectoryRoots)
                {
                    var directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    PathGuard.EnsureNoReparsePoints(root, directory);
                    foreach (var file in Directory.EnumerateFiles(directory, "*", BepInExScanOptions))
                    {
                        paths.Add(RelativeOf(root, file));
                    }
                }
            }
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<IReadOnlyList<string>>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"枚举 BepInEx 运行期文件失败：{ex.Message}",
                "关闭占用文件的程序，或检查游戏目录权限后重试。"));
        }

        return ForgeResult<IReadOnlyList<string>>.Success(
            paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>只读采集；必须在第一次写游戏目录之前调用。</summary>
    public static ForgeResult<GameBaseline> Capture(string gameDirectory, DeploymentPlan plan)
    {
        if (plan?.Operations is null)
        {
            return ForgeResult<GameBaseline>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "部署计划没有文件操作，无法采集安装前基线。",
                "重新生成部署计划。"));
        }

        var paths = CollectPaths(gameDirectory, plan.Operations.Select(operation => operation.RelativePath));
        if (!paths.Ok)
        {
            return ForgeResult<GameBaseline>.Failure(paths.Error!);
        }

        return GameBaselineBuilder.CapturePaths(gameDirectory, paths.Value!);
    }

    /// <summary>只写工作区；目标路径必须通过工作区与 reparse 检查。</summary>
    public static ForgeResult<string> Save(ForgeWorkspace workspace, string slug, GameBaseline baseline)
    {
        try
        {
            var path = ResolvePath(workspace, slug);
            workspace.EnsureWriteAllowed(path);
            PathGuard.EnsureNoReparsePoints(workspace.RootPath, path);
            return GameBaselineStore.Save(baseline, path);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<string>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"保存安装前基线失败：{ex.Message}",
                "检查工作区权限与磁盘空间后重试。"));
        }
    }

    public static void TryDelete(ForgeWorkspace workspace, string slug)
    {
        try
        {
            var path = ResolvePath(workspace, slug);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ForgePathException)
        {
            // 清理失败不改变部署失败的主结论。
        }
    }

    /// <summary>
    /// 从安装前基线中剔除 BepInEx 运行期会自行改写的文件（interop、cache、patchers、BepInEx 根日志；
    /// 可选 config）。这些文件在游戏启动后必然变化（interop 生成、chainloader 缓存、日志增长），
    /// 留在基线里会让 Verify 的 m-07「账本外原文件未变」把正常运行行为误判成篡改（R21/R22 真机踩过）。
    /// 用户文件和本次安装目标仍然保留。返回剔除条数。
    /// </summary>
    public static int TrimRuntimeChurn(GameBaseline baseline, bool includeConfig = true)
    {
        if (baseline?.Files is null || baseline.Files.Count == 0)
        {
            return 0;
        }

        return baseline.Files.RemoveAll(file => file is not null && IsRuntimeChurnPath(file.RelativePath, includeConfig));
    }

    private static readonly string[] RuntimeChurnRoots =
    [
        "BepInEx/interop/",
        "BepInEx/cache/",
        "BepInEx/patchers/",
    ];

    private static bool IsRuntimeChurnPath(string relativePath, bool includeConfig)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var path = relativePath.Replace('\\', '/').Trim();
        foreach (var root in RuntimeChurnRoots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (includeConfig && path.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && path.IndexOf('/', "BepInEx/".Length) < 0;
    }

    private static string RelativeOf(string root, string fullPath)
        => PathSafety.ValidateRelative(Path.GetRelativePath(root, fullPath).Replace('\\', '/'));
}
