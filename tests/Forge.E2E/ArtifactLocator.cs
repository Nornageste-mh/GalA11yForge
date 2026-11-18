using Xunit.Sdk;

namespace Forge.E2E;

/// <summary>在 sandbox 的工作区 / 输出目录里递归找产物。找不到时把文件树带进失败信息。</summary>
internal static class ArtifactLocator
{
    public static IReadOnlyList<string> FindFiles(IEnumerable<string> roots, string fileName)
    {
        return FindFilesByName(roots, name => name.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> FindFilesContaining(IEnumerable<string> roots, string text, string extension = ".json")
    {
        return FindFilesByName(roots, name =>
            name.Contains(text, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    public static string? FindNewest(IEnumerable<string> roots, params string[] fileNames)
    {
        var wanted = fileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return FindFilesByName(roots, wanted.Contains)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string RequireNewest(string what, IEnumerable<string> roots, params string[] fileNames)
    {
        var found = FindNewest(roots, fileNames);
        if (found is null)
        {
            throw new XunitException(
                $"找不到{what}（期望文件名：{string.Join(" / ", fileNames)}）。"
                + $"{Environment.NewLine}搜索范围：{string.Join("；", roots)}{Environment.NewLine}目录快照：{Environment.NewLine}{Tree(roots)}");
        }

        return found;
    }

    public static string RequireGeneratedRoot(IEnumerable<string> roots)
    {
        var plugin = FindNewest(roots, "Plugin.cs");
        if (plugin is null)
        {
            throw new XunitException(
                $"找不到生成工程的 Plugin.cs，无法确定生成根。{Environment.NewLine}目录快照：{Environment.NewLine}{Tree(roots)}");
        }

        return Path.GetDirectoryName(plugin)!;
    }

    public static string Tree(IEnumerable<string> roots, int maxEntries = 80)
    {
        var lines = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                lines.Add($"[缺失] {root}");
                continue;
            }

            lines.Add($"[目录] {root}");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (lines.Count >= maxEntries)
                {
                    lines.Add("  ...（已截断）");
                    return string.Join(Environment.NewLine, lines);
                }

                var relative = Path.GetRelativePath(root, file);
                long size = 0;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // 文件可能正被占用；大小仅用于排障，失败不致命。
                }

                lines.Add($"  {relative} ({size} B)");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> FindFilesByName(IEnumerable<string> roots, Func<string, bool> predicate)
    {
        var results = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (predicate(Path.GetFileName(file)))
                    {
                        results.Add(file);
                    }
                }
            }
            catch (IOException)
            {
                // 目录在扫描过程中被改动的极端情况：跳过该根，失败信息仍会打印文件树。
            }
        }

        return results;
    }
}
