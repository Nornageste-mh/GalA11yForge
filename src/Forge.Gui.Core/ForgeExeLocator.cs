namespace GalA11yForge.Gui.Core;

public sealed class ForgeExeSearchOptions
{
    /// <summary>用户在「forge.exe 路径」里填的绝对路径（可空）。</summary>
    public string? ExplicitPath { get; init; }

    /// <summary>环境变量 FORGE_EXE 的值。</summary>
    public string? EnvironmentValue { get; init; }

    /// <summary>GUI 自身所在目录，通常是 src\Forge.Gui\bin\Release\net8.0-windows\。</summary>
    public string? BaseDirectory { get; init; }

    public string? CurrentDirectory { get; init; }

    public string? PathVariable { get; init; }

    /// <summary>注入点：测试用假实现替换文件存在性。</summary>
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    public Func<string, bool> DirectoryExists { get; init; } = Directory.Exists;

    /// <summary>从哪一层开始向上找（默认 8 层，够从 bin\Release\net8.0-windows 回到仓库根）。</summary>
    public int MaxParentHops { get; init; } = 8;

    public static ForgeExeSearchOptions CreateDefault(string? explicitPath = null)
        => new()
        {
            ExplicitPath = explicitPath,
            EnvironmentValue = Environment.GetEnvironmentVariable("FORGE_EXE"),
            BaseDirectory = AppContext.BaseDirectory,
            CurrentDirectory = Environment.CurrentDirectory,
            PathVariable = Environment.GetEnvironmentVariable("PATH"),
        };
}

public sealed record ForgeExeResolution(string? Path, string? Source, IReadOnlyList<string> TriedPaths)
{
    public bool Found => !string.IsNullOrWhiteSpace(Path);

    public string Summary => Found ? $"{Path}（来源：{Source}）" : "没找到 forge.exe";
}

/// <summary>
/// 找 forge.exe。顺序固定且可解释（结果区会显示「来源」与「尝试过的路径」）：
/// 1) 界面显式指定 → 2) 环境变量 FORGE_EXE → 3) GUI 同目录
/// → 4) 从 GUI 目录向上逐层找 src\Forge.Cli\bin\{Release,Debug}\net8.0\forge.exe
/// → 5) 从当前目录向上同样找 → 6) PATH。
/// </summary>
public static class ForgeExeLocator
{
    public const string ExeFileName = "forge.exe";

    /// <summary>仓库内 Release 产物（任务书指定的默认值）。</summary>
    public const string ReleaseRelativePath = @"src\Forge.Cli\bin\Release\net8.0\forge.exe";

    /// <summary>仓库内 Debug 产物（Release 没构建时的兜底）。</summary>
    public const string DebugRelativePath = @"src\Forge.Cli\bin\Debug\net8.0\forge.exe";

    /// <summary>发布包布局：gui\ 与 app\ 同级，forge.exe 在 app\ 里。</summary>
    public const string ReleaseAppRelativePath = @"..\app\forge.exe";

    /// <summary>扁平布局兜底：forge.exe 就在 GUI 的上一级目录。</summary>
    public const string ParentFolderRelativePath = @"..\forge.exe";

    private static readonly string[] RelativeProbes = { ReleaseRelativePath, DebugRelativePath };

    public static ForgeExeResolution Resolve(ForgeExeSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tried = new List<string>();
        var fileExists = options.FileExists ?? File.Exists;

        if (Probe(options.ExplicitPath, "界面指定", fileExists, tried, out var explicitPath))
        {
            return new ForgeExeResolution(explicitPath, "界面指定", tried);
        }

        if (Probe(options.EnvironmentValue, "环境变量 FORGE_EXE", fileExists, tried, out var environmentPath))
        {
            return new ForgeExeResolution(environmentPath, "环境变量 FORGE_EXE", tried);
        }

        var baseDirectory = options.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            var sameFolder = System.IO.Path.Combine(baseDirectory, ExeFileName);
            if (Probe(sameFolder, "与 GUI 同目录", fileExists, tried, out var sameFolderPath))
            {
                return new ForgeExeResolution(sameFolderPath, "与 GUI 同目录", tried);
            }

            // 发布包布局：releases\v0.2.x\gui\Forge.Gui.exe 与 releases\v0.2.x\app\forge.exe 同级。
            var releaseApp = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, ReleaseAppRelativePath));
            if (Probe(releaseApp, "发布包 app 目录", fileExists, tried, out var releaseAppPath))
            {
                return new ForgeExeResolution(releaseAppPath, "发布包 app 目录", tried);
            }

            var parentFolder = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, ParentFolderRelativePath));
            if (Probe(parentFolder, "上一级目录", fileExists, tried, out var parentFolderPath))
            {
                return new ForgeExeResolution(parentFolderPath, "上一级目录", tried);
            }

            if (WalkUp(baseDirectory, "从 GUI 目录向上查找", options, fileExists, tried, out var walked))
            {
                return new ForgeExeResolution(walked, "从 GUI 目录向上查找", tried);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.CurrentDirectory))
        {
            if (WalkUp(options.CurrentDirectory, "从当前目录向上查找", options, fileExists, tried, out var fromCwd))
            {
                return new ForgeExeResolution(fromCwd, "从当前目录向上查找", tried);
            }
        }

        if (ProbeInPathVariable(options.PathVariable, fileExists, tried, out var fromPath))
        {
            return new ForgeExeResolution(fromPath, "PATH 环境变量", tried);
        }

        return new ForgeExeResolution(null, null, tried);
    }

    private static bool Probe(
        string? candidate,
        string source,
        Func<string, bool> fileExists,
        List<string> tried,
        out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var trimmed = candidate.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return false;
        }

        tried.Add($"{source}：{trimmed}");
        if (fileExists(trimmed))
        {
            try
            {
                resolved = System.IO.Path.GetFullPath(trimmed);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                resolved = trimmed;
            }

            return true;
        }

        return false;
    }

    private static bool WalkUp(
        string startDirectory,
        string source,
        ForgeExeSearchOptions options,
        Func<string, bool> fileExists,
        List<string> tried,
        out string? resolved)
    {
        resolved = null;
        var directory = startDirectory.TrimEnd('\\', '/');
        var hops = Math.Max(1, options.MaxParentHops);
        for (var hop = 0; hop <= hops && !string.IsNullOrWhiteSpace(directory); hop++)
        {
            foreach (var relative in RelativeProbes)
            {
                var candidate = System.IO.Path.Combine(directory, relative);
                if (Probe(candidate, source, fileExists, tried, out resolved))
                {
                    return true;
                }
            }

            var parent = System.IO.Path.GetDirectoryName(directory);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = parent;
        }

        return false;
    }

    private static bool ProbeInPathVariable(string? pathVariable, Func<string, bool> fileExists, List<string> tried, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        foreach (var entry in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = System.IO.Path.Combine(directory, ExeFileName);
            if (Probe(candidate, "PATH 环境变量", fileExists, tried, out resolved))
            {
                return true;
            }
        }

        return false;
    }
}
