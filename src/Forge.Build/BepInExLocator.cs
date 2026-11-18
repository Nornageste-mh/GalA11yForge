using GalA11yForge.Core.Results;

namespace GalA11yForge.Build;

/// <summary>
/// 在若干搜索根里找 BepInEx_win_x64_5.4.23.5.zip。优先各 *_a11y 目录、bepinex/ 目录，其次浅层目录，
/// 再按修改时间新到旧；找不到时由调用方返回 E_DEPENDENCY_MISSING 和官方下载地址。
///
/// 搜索根除了工作区，通常还会包含 forge.exe 所在目录（发行包内置 bepinex/ 离线包），
/// 这样用户不用自己找 zip 就能跑 build/install。
/// </summary>
public static class BepInExLocator
{
    public const string ExactFileName = "BepInEx_win_x64_5.4.23.5.zip";

    /// <summary>单根搜索（兼容旧调用）。</summary>
    public static ForgeResult<IReadOnlyList<string>> FindCandidates(string searchRoot, string? explicitPath = null)
        => FindCandidates(new[] { searchRoot }, explicitPath);

    /// <summary>多根搜索：按给定顺序枚举，结果统一排序（显式路径优先级最高）。</summary>
    public static ForgeResult<IReadOnlyList<string>> FindCandidates(
        IReadOnlyList<string>? searchRoots,
        string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full;
            try
            {
                full = Path.GetFullPath(explicitPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ArgumentInvalid,
                    $"指定的 BepInEx 包路径非法：{ex.Message}",
                    "使用普通绝对路径。"));
            }

            if (!File.Exists(full))
            {
                return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.DependencyMissing,
                    $"指定的 BepInEx 包不存在：{full}；下载地址：{BepInExPackage.OfficialDownloadUrl}",
                    $"下载 BepInEx 5.4.23.5（win x64）：{BepInExPackage.OfficialDownloadUrl}",
                    new Dictionary<string, string> { ["packagePath"] = full, ["downloadUrl"] = BepInExPackage.OfficialDownloadUrl }));
            }

            return ForgeResult<IReadOnlyList<string>>.Success(new[] { full });
        }

        if (searchRoots is null || searchRoots.Count == 0)
        {
            return ForgeResult<IReadOnlyList<string>>.Success(Array.Empty<string>());
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var candidates = new List<string>();
        foreach (var searchRoot in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(searchRoot) || !Directory.Exists(searchRoot))
            {
                continue;
            }

            try
            {
                candidates.AddRange(Directory.EnumerateFiles(searchRoot, "BepInEx_win_x64*.zip", options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ForgeResult<IReadOnlyList<string>>.Failure(ForgeError.Create(
                    ForgeErrorCodes.Io,
                    $"搜索 BepInEx 缓存失败：{ex.Message}",
                    "用 --bepinex <zip> 显式指定路径。"));
            }
        }

        var sorted = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => Path.GetFileName(path).StartsWith("BepInEx_win_x64", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => string.Equals(
                Path.GetFileName(path), ExactFileName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => path.Contains("bepinex", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => path.Contains("_a11y", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => Depth(path))
            .ThenByDescending(path => SafeLastWrite(path))
            .ToList();

        return ForgeResult<IReadOnlyList<string>>.Success(sorted);
    }

    private static int Depth(string path)
        => path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
