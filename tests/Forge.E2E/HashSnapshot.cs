using System.Security.Cryptography;

namespace Forge.E2E;

internal sealed record HashChange(string RelativePath, string Kind, string? Before, string? After);

/// <summary>游戏目录的 sha256 快照；install 前后与 rollback 前后都用它做硬断言。</summary>
internal static class HashSnapshot
{
    public static Dictionary<string, string> Capture(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"快照目录不存在：{root}");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            result[relative] = hash;
        }

        return result;
    }

    public static IReadOnlyList<HashChange> Diff(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        var changes = new List<HashChange>();
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var hasBefore = before.TryGetValue(path, out var beforeHash);
            var hasAfter = after.TryGetValue(path, out var afterHash);

            if (!hasBefore && hasAfter)
            {
                changes.Add(new HashChange(path, "新增", null, afterHash));
            }
            else if (hasBefore && !hasAfter)
            {
                changes.Add(new HashChange(path, "删除", beforeHash, null));
            }
            else if (!string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new HashChange(path, "修改", beforeHash, afterHash));
            }
        }

        return changes;
    }

    public static string Describe(IReadOnlyList<HashChange> changes, int maxEntries = 60)
    {
        if (changes.Count == 0)
        {
            return "(无差异)";
        }

        var lines = changes.Take(maxEntries).Select(change =>
        {
            var before = change.Before is null ? "(无)" : change.Before[..12];
            var after = change.After is null ? "(无)" : change.After[..12];
            return $"  [{change.Kind}] {change.RelativePath}  {before} -> {after}";
        }).ToList();

        if (changes.Count > maxEntries)
        {
            lines.Add($"  ...（另有 {changes.Count - maxEntries} 项）");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
