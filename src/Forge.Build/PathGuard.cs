using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>游戏目录/工作区的复核。部署与回滚共用，确保不会把工作区写进游戏目录。</summary>
public static class PathGuard
{
    /// <summary>拒绝路径链上的 reparse point（junction/symlink），防止“看似在游戏目录内，实际指向别处”。</summary>
    public static void EnsureNoReparsePoints(string root, string target)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullTarget = Path.GetFullPath(target);

        if (!PathSafety.IsUnder(fullRoot, fullTarget))
        {
            throw ForgePathException.Escape(fullTarget, fullRoot);
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(fullTarget) ?? fullRoot);
        while (current is not null
               && !current.FullName.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
               && PathSafety.IsUnder(fullRoot, current.FullName))
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ForgePathException(ForgeError.Create(
                    ForgeErrorCodes.PathUnsafe,
                    $"路径中包含符号链接/联接点，拒绝部署：{current.FullName}",
                    "把游戏安装到普通目录，或移除路径中的 junction/symlink 后重试。"));
            }

            current = current.Parent;
        }

        if ((File.Exists(fullTarget) || Directory.Exists(fullTarget))
            && File.GetAttributes(fullTarget).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ForgePathException(ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"目标是符号链接/联接点，拒绝覆盖：{fullTarget}",
                "先由人工确认该链接指向哪里，再移除链接或改用普通文件。"));
        }
    }

    /// <summary>把游戏内相对路径解析为绝对路径，并做越界、reparse point 检查。</summary>
    public static string ResolveGameRelative(string gameDirectory, string relativePath)
    {
        var fullGame = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        var resolved = PathSafety.ResolveUnder(fullGame, relativePath);
        EnsureNoReparsePoints(fullGame, resolved);
        return resolved;
    }

    public static bool SamePath(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
               .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}
