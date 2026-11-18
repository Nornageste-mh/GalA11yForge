using System.Diagnostics;

namespace GalA11yForge.Core.Tools;

/// <summary>一个工具的检查结果。Found=false 也是正常结果（可选能力），不抛错。</summary>
public sealed record ReToolStatus(
    string Id,
    string DisplayName,
    ReToolKind Kind,
    bool Found,
    string? Path,
    string License,
    string DownloadUrl,
    string PlacementHint,
    IReadOnlyList<string> SearchedPaths,
    string? Version,
    string Note);

/// <summary>
/// RE 工具定位器：按“显式路径 → 工作区 → forge 程序旁 → 工具目录”的固定顺序找，结果可解释。
/// 只做文件系统检查，不启动任何进程、不联网、不写盘。
/// </summary>
public static class ReToolLocator
{
    /// <summary>在给定根目录里找工具；显式路径优先，且允许指向文件或目录。</summary>
    public static IReadOnlyList<ReToolStatus> Locate(
        IEnumerable<string> searchRoots,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        var roots = searchRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(static root => root.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var results = new List<ReToolStatus>(ReToolCatalog.All.Count);
        foreach (var descriptor in ReToolCatalog.All)
        {
            results.Add(LocateOne(descriptor, roots, overrides));
        }

        return results;
    }

    private static ReToolStatus LocateOne(
        ReToolDescriptor descriptor,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var searched = new List<string>();
        if (overrides is not null
            && overrides.TryGetValue(ReToolCatalog.PathOptionKey(descriptor.Id), out var explicitPath)
            && !string.IsNullOrWhiteSpace(explicitPath))
        {
            var resolved = TryResolveExplicit(descriptor, explicitPath.Trim(), searched);
            if (resolved is not null)
            {
                return Found(descriptor, resolved, searched, "显式 --option 指定");
            }
        }

        foreach (var root in roots)
        {
            var resolved = TryResolveInRoot(descriptor, root, searched);
            if (resolved is not null)
            {
                return Found(descriptor, resolved, searched, $"在 {root} 找到");
            }
        }

        var note = descriptor.Kind == ReToolKind.Executable
            ? "未找到可执行文件。可把工具解压到工作区 tools 目录，或用 --option re." + descriptor.Id + ".path=<exe 或目录> 指定。"
            : "这类工具不是独立 exe，产品不自动探测：用 --option re." + descriptor.Id + ".path=<安装目录> 标注，或按提示自行确认。";
        return new ReToolStatus(
            descriptor.Id,
            descriptor.DisplayName,
            descriptor.Kind,
            Found: false,
            Path: null,
            descriptor.License,
            descriptor.DownloadUrl,
            descriptor.PlacementHint,
            searched,
            Version: null,
            note);
    }

    private static ReToolStatus Found(
        ReToolDescriptor descriptor,
        string path,
        IReadOnlyList<string> searched,
        string sourceNote)
    {
        return new ReToolStatus(
            descriptor.Id,
            descriptor.DisplayName,
            descriptor.Kind,
            Found: true,
            path,
            descriptor.License,
            descriptor.DownloadUrl,
            descriptor.PlacementHint,
            searched,
            ReadVersion(path),
            sourceNote);
    }

    private static string? TryResolveExplicit(
        ReToolDescriptor descriptor,
        string explicitPath,
        List<string> searched)
    {
        searched.Add($"显式：{explicitPath}");
        try
        {
            if (File.Exists(explicitPath))
            {
                return Path.GetFullPath(explicitPath);
            }

            if (!Directory.Exists(explicitPath))
            {
                return null;
            }

            var directory = Path.GetFullPath(explicitPath);
            if (descriptor.Kind != ReToolKind.Executable)
            {
                return directory;
            }

            foreach (var fileName in descriptor.FileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                searched.Add(candidate);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 允许指向“解压后带版本子目录”的工具目录（只下探一层，避免全盘扫描）。
            try
            {
                foreach (var subdirectory in Directory.EnumerateDirectories(directory))
                {
                    foreach (var fileName in descriptor.FileNames)
                    {
                        var candidate = Path.Combine(subdirectory, fileName);
                        searched.Add(candidate);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录不可枚举时按未找到处理，由报告提示用户换路径。
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryResolveInRoot(
        ReToolDescriptor descriptor,
        string root,
        List<string> searched)
    {
        if (descriptor.Kind != ReToolKind.Executable)
        {
            // Python/npm 包没有稳定的可执行入口，目录存在不等于“装好了”；
            // 只接受 --option 显式路径，避免报告假阳性。
            return null;
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var directory in CandidateDirectories(descriptor, root))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var fileName in descriptor.FileNames)
            {
                var candidate = Path.Combine(directory, fileName);
                searched.Add(candidate);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (descriptor.Kind == ReToolKind.Executable)
        {
            // 也接受“根目录下直接放 exe”。
            foreach (var fileName in descriptor.FileNames)
            {
                var candidate = Path.Combine(root, fileName);
                searched.Add(candidate);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories(ReToolDescriptor descriptor, string root)
    {
        yield return Path.Combine(root, descriptor.Id);
        yield return Path.Combine(root, "tools", "re", descriptor.Id);
        yield return Path.Combine(root, "re", descriptor.Id);
        yield return Path.Combine(root, "tools", descriptor.Id);

        // 一层版本目录：<root>/<id>/<version>/ 或 tools/re/<id>/<version>/。
        foreach (var parent in new[]
                 {
                     Path.Combine(root, descriptor.Id),
                     Path.Combine(root, "tools", "re", descriptor.Id),
                 })
        {
            if (!Directory.Exists(parent))
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                yield return child;
            }
        }
    }

    private static string? ReadVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = info.FileVersion ?? info.ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
