using System.Text.RegularExpressions;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Workspaces;

/// <summary>
/// 纯路径校验工具。所有写盘入口都必须经过这里：
/// 1) 目标必须在工作区根目录内；2) 目标不得位于游戏目录；3) 相对路径不得逃逸。
/// </summary>
public static partial class PathSafety
{
    private static readonly char[] InvalidRelativeChars = ['\0', ':', '*', '?', '"', '<', '>', '|'];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    public static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ForgePathException(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "工作区根目录不能为空。",
                "使用 --workspace 指定一个可写目录，或去掉该参数使用默认目录。"));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>判断 path 是否位于 root 之内（含相等）。大小写不敏感，适合 Windows。</summary>
    public static bool IsUnder(string root, string path)
    {
        var normalizedRoot = NormalizeRoot(root);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>校验绝对路径可写：在工作区内，且不在游戏目录内。</summary>
    public static string EnsureWriteAllowed(string root, string path, string? gameDirectory = null)
    {
        var normalizedRoot = NormalizeRoot(root);
        var fullPath = Path.GetFullPath(path);

        // 游戏目录是更具体的禁令，先于一般的工作区逃逸检查报告。
        if (!string.IsNullOrWhiteSpace(gameDirectory) && IsUnder(gameDirectory, fullPath))
        {
            throw ForgePathException.GameDirectory(fullPath, Path.GetFullPath(gameDirectory));
        }

        if (!IsUnder(normalizedRoot, fullPath))
        {
            throw ForgePathException.Escape(fullPath, normalizedRoot);
        }

        return fullPath;
    }

    /// <summary>把安全相对路径解析到 root 下；绝对路径、盘符、.. 一律拒绝。</summary>
    public static string ResolveUnder(string root, string relativePath)
    {
        var normalizedRoot = NormalizeRoot(root);
        var relative = ValidateRelative(relativePath);
        var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!IsUnder(normalizedRoot, combined))
        {
            throw ForgePathException.Escape(combined, normalizedRoot);
        }

        return combined;
    }

    /// <summary>校验相对路径；返回统一使用 '/' 分隔的形式。</summary>
    public static string ValidateRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw ForgePathException.InvalidRelative(relativePath ?? "<null>", "不能为空");
        }

        var value = relativePath.Replace('\\', '/').Trim();
        if (value.StartsWith('/') || value.StartsWith('#'))
        {
            throw ForgePathException.InvalidRelative(relativePath, "不能是绝对路径");
        }

        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        {
            throw ForgePathException.InvalidRelative(relativePath, "不能包含盘符");
        }

        if (value.IndexOfAny(InvalidRelativeChars) >= 0)
        {
            throw ForgePathException.InvalidRelative(relativePath, "包含非法字符");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw ForgePathException.InvalidRelative(relativePath, "不能为空");
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw ForgePathException.InvalidRelative(relativePath, "不能包含 . 或 .. 段");
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw ForgePathException.InvalidRelative(relativePath, "路径段不能以空格或点结尾");
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>slug 只允许小写字母、数字、点、下划线、连字符。</summary>
    public static bool IsValidSlug(string? slug)
        => !string.IsNullOrEmpty(slug) && SlugPattern().IsMatch(slug) && !slug.Contains("..", StringComparison.Ordinal);

    public static string SanitizeSlug(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in (name ?? string.Empty).ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-');
        }

        var slug = builder.ToString().Trim('-', '.');
        while (slug.Contains("..", StringComparison.Ordinal))
        {
            slug = slug.Replace("..", ".", StringComparison.Ordinal);
        }

        if (slug.Length > 64)
        {
            slug = slug[..64].TrimEnd('-', '.');
        }

        return string.IsNullOrEmpty(slug) ? "game" : slug;
    }

    public static string SlugFromGameDirectory(string gameDirectory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        return SanitizeSlug(Path.GetFileName(trimmed));
    }
}
