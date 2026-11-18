using System.Text;
using System.Text.RegularExpressions;

namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>一次 analyzeHeadless 调用的输入。路径必须已解析为绝对路径。</summary>
public sealed class GhidraCommandRequest
{
    public required string AnalyzeHeadlessPath { get; init; }
    public required string ProjectDirectory { get; init; }
    public required string ProjectName { get; init; }
    public required string ImportPath { get; init; }
    public required string ScriptDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public string PostScript { get; init; } = GhidraScriptResources.CandidateHookDumpFileName;
    public int AnalysisTimeoutSeconds { get; init; } = 900;
    public int MaxCpu { get; init; } = 4;
    public bool DeleteProject { get; init; } = true;
    public IReadOnlyList<string> ExtraArguments { get; init; } = Array.Empty<string>();
}

/// <summary>可直接交给 ProcessStartInfo 的命令；Argv 保留未拼接的参数序列，便于测试与日志。</summary>
public sealed class GhidraCommand
{
    public required string FileName { get; init; }
    public required string Arguments { get; init; }
    public required string Display { get; init; }
    public required IReadOnlyList<string> Argv { get; init; }
    public bool ViaCmd { get; init; }
}

/// <summary>
/// 纯函数式命令行构造：不启动进程、不碰文件系统。
/// .bat 无法被 .NET 以 UseShellExecute=false 直接启动，所以经 cmd.exe /d /s /c 包装。
/// </summary>
public static class GhidraCommandBuilder
{
    private static readonly Regex ProjectNamePattern = new(@"^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled);

    public static GhidraCommand Build(GhidraCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfBlank(request.AnalyzeHeadlessPath, nameof(request.AnalyzeHeadlessPath));
        ThrowIfBlank(request.ProjectDirectory, nameof(request.ProjectDirectory));
        ThrowIfBlank(request.ProjectName, nameof(request.ProjectName));
        ThrowIfBlank(request.ImportPath, nameof(request.ImportPath));
        ThrowIfBlank(request.ScriptDirectory, nameof(request.ScriptDirectory));
        ThrowIfBlank(request.OutputDirectory, nameof(request.OutputDirectory));
        ThrowIfBlank(request.PostScript, nameof(request.PostScript));

        if (!ProjectNamePattern.IsMatch(request.ProjectName))
        {
            throw new ArgumentException(
                $"Ghidra 工程名只能是字母、数字、下划线、点或连字符（1-64 字符）：{request.ProjectName}",
                nameof(request));
        }

        if (!Path.IsPathFullyQualified(request.ProjectDirectory) ||
            !Path.IsPathFullyQualified(request.ImportPath) ||
            !Path.IsPathFullyQualified(request.ScriptDirectory) ||
            !Path.IsPathFullyQualified(request.OutputDirectory))
        {
            throw new ArgumentException("Ghidra 的工程/导入/脚本/输出路径必须是绝对路径。", nameof(request));
        }

        var argv = new List<string>
        {
            Path.GetFullPath(request.ProjectDirectory),
            request.ProjectName,
            "-import",
            Path.GetFullPath(request.ImportPath),
            "-overwrite",
            "-analysisTimeoutPerFile",
            request.AnalysisTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-max-cpu",
            request.MaxCpu.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-scriptPath",
            Path.GetFullPath(request.ScriptDirectory),
            "-postScript",
            request.PostScript,
            Path.GetFullPath(request.OutputDirectory),
        };

        foreach (var extra in request.ExtraArguments)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                argv.Add(extra);
            }
        }

        if (request.DeleteProject)
        {
            argv.Add("-deleteProject");
        }

        var quoted = string.Join(" ", argv.Select(Quote));
        var isBatch = !request.AnalyzeHeadlessPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var headless = Path.GetFullPath(request.AnalyzeHeadlessPath);

        if (!isBatch)
        {
            return new GhidraCommand
            {
                FileName = headless,
                Arguments = quoted,
                Display = $"{Quote(headless)} {quoted}",
                Argv = argv,
                ViaCmd = false,
            };
        }

        // cmd /d /s /c ""<bat>" <args>" —— /s 时首尾引号会被剥离，内层引号必须完整包住 bat 路径。
        var arguments = $"/d /s /c \"{Quote(headless)} {quoted}\"";
        return new GhidraCommand
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            Display = $"cmd.exe {arguments}",
            Argv = argv,
            ViaCmd = true,
        };
    }

    /// <summary>Windows 命令行参数引用（CommandLineToArgvW 兼容；路径里不会出现引号，但仍按规则转义）。</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(ch);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void ThrowIfBlank(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("不能为空。", name);
        }
    }
}
