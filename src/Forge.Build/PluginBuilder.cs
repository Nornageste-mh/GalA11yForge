using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Logging;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>
/// IBuilder 实现：调用 dotnet build 编译生成物，捕获 stdout/stderr、超时与退出码，
/// 只有输出目录里确实存在 DLL 才返回成功。产物与日志都必须在工作区内。
/// </summary>
public sealed class PluginBuilder : IBuilder
{
    private readonly ForgeWorkspace _workspace;
    private readonly BuildOptions _options;
    private readonly IProcessRunner _runner;
    private readonly IFlowLogger _logger;

    public PluginBuilder(
        ForgeWorkspace workspace,
        BuildOptions? options = null,
        IProcessRunner? runner = null,
        IFlowLogger? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = (options ?? new BuildOptions()).Validate();
        _runner = runner ?? new ProcessRunner();
        _logger = logger ?? NullFlowLogger.Instance;
    }

    public async Task<ForgeResult<BuildResult>> BuildAsync(
        GameContext context,
        GenerationResult generation,
        CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            return Failure(ForgeErrorCodes.ArgumentInvalid, "缺少游戏上下文。", "先识别游戏目录再构建。");
        }

        if (generation is null)
        {
            return Failure(ForgeErrorCodes.ArgumentInvalid, "缺少生成结果，无法构建。", "先运行生成步骤。");
        }

        try
        {
            return await BuildCoreAsync(context, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(ForgeErrorCodes.Io, "构建被取消。", "重新运行构建；已生成的临时文件可保留。");
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<BuildResult>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(ForgeErrorCodes.Io, $"构建过程读写失败：{ex.Message}", "检查磁盘空间、路径长度与目录权限。");
        }
        catch (Exception ex)
        {
            return Failure(
                ForgeErrorCodes.Internal,
                $"构建失败（未预期错误）：{ex.Message}",
                "保留日志并反馈给开发者；不要静默忽略。");
        }
    }

    private async Task<ForgeResult<BuildResult>> BuildCoreAsync(
        GameContext context,
        GenerationResult generation,
        CancellationToken cancellationToken)
    {
        var projectResult = ResolveProject(generation);
        if (!projectResult.Ok)
        {
            return ForgeResult<BuildResult>.Failure(projectResult.Error!);
        }

        var projectFile = projectResult.Value!;
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var timestamp = DateTimeOffset.UtcNow;
        var stamp = timestamp.ToString("yyyyMMdd-HHmmss");

        var outputDirectory = string.IsNullOrWhiteSpace(_options.OutputDirectory)
            ? Path.Combine(_workspace.WorkRoot, $"build-{stamp}-{Guid.NewGuid().ToString("N")[..8]}")
            : Path.GetFullPath(_options.OutputDirectory);
        outputDirectory = _workspace.EnsureWriteAllowed(outputDirectory);

        // 构建输出只允许放在 work/ 下的子目录：避免 CleanOutputDirectory 误删工作区根、
        // 备份、报告或其它构建目录。工作区根/游戏目录本身也永远不是合法输出目录。
        if (!PathSafety.IsUnder(_workspace.WorkRoot, outputDirectory)
            || PathGuard.SamePath(_workspace.WorkRoot, outputDirectory))
        {
            return Failure(
                ForgeErrorCodes.PathUnsafe,
                $"构建输出目录必须位于工作区 work/ 的子目录内：{outputDirectory}",
                $"把 OutputDirectory 改为 {Path.Combine(_workspace.WorkRoot, "build-<名称>")}，不要指向工作区根、work/ 本身或游戏目录。");
        }

        var logDirectory = string.IsNullOrWhiteSpace(_options.LogDirectory)
            ? _workspace.LogsRoot
            : Path.GetFullPath(_options.LogDirectory);
        logDirectory = _workspace.EnsureWriteAllowed(logDirectory);
        var logPath = _workspace.EnsureWriteAllowed(
            Path.Combine(logDirectory, $"build-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.log"));

        var arguments = BuildArguments(projectFile, outputDirectory);
        var commandText = FormatCommand(ResolveDotnet(_options.DotnetPath) ?? "dotnet", arguments);
        var plannedDll = Path.Combine(outputDirectory, $"{ReadAssemblyName(projectFile)}.dll");

        if (context.DryRun)
        {
            _logger.Log(FlowLogLevel.Info, "04-build", "dry-run：仅计算构建计划，不创建目录、不调用 dotnet。", new Dictionary<string, string>
            {
                ["project"] = projectFile,
                ["output"] = outputDirectory,
            });

            return ForgeResult<BuildResult>.Success(new BuildResult
            {
                OutputDirectory = outputDirectory,
                Artifacts = new List<string>(),
                LogPath = logPath,
                Metadata = new Dictionary<string, string>
                {
                    ["dryRun"] = "true",
                    ["command"] = commandText,
                    ["projectFile"] = projectFile,
                    ["outputDirectory"] = outputDirectory,
                    ["configuration"] = _options.Configuration,
                    ["plannedArtifacts"] = plannedDll,
                },
            });
        }

        var dotnet = ResolveDotnet(_options.DotnetPath);
        if (dotnet is null)
        {
            return ForgeResult<BuildResult>.Failure(ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "找不到 dotnet 可执行文件，无法构建补丁。",
                "安装 .NET SDK 8（https://dotnet.microsoft.com/download/dotnet/8.0），或设置环境变量 GALA11Y_DOTNET 指向 dotnet.exe。",
                new Dictionary<string, string>
                {
                    ["dotnet"] = _options.DotnetPath ?? "<auto>",
                    ["checked"] = @"GALA11Y_DOTNET, DOTNET_HOST_PATH, D:\dotnet\dotnet.exe, PATH",
                }));
        }

        try
        {
            PathGuard.EnsureNoReparsePoints(_workspace.WorkRoot, outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            if (_options.CleanOutputDirectory)
            {
                CleanOutputDirectory(outputDirectory);
            }

            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(ForgeErrorCodes.Io, $"无法准备构建目录：{ex.Message}", "检查磁盘空间与目录权限。");
        }

        var header = new StringBuilder()
            .AppendLine($"[{timestamp:O}] build start")
            .AppendLine($"command: {commandText}")
            .AppendLine($"project: {projectFile}")
            .AppendLine($"output: {outputDirectory}")
            .AppendLine($"configuration: {_options.Configuration}")
            .AppendLine($"workspace: {_workspace.RootPath}")
            .AppendLine($"game: {_workspace.GameDirectory ?? context.GameDirectory}")
            .AppendLine("--- output ---")
            .ToString();

        if (!TryWriteLog(logPath, header))
        {
            return Failure(ForgeErrorCodes.Io, $"无法写入构建日志：{logPath}", "检查工作区 logs/ 目录权限。");
        }

        _logger.Log(FlowLogLevel.Info, "04-build", "开始构建补丁工程。", new Dictionary<string, string>
        {
            ["project"] = projectFile,
            ["output"] = outputDirectory,
        });

        ProcessResult process;
        try
        {
            process = await _runner.RunAsync(dotnet, arguments, projectDirectory, _options.Timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                $"无法启动 dotnet（{dotnet}）：{ex.Message}",
                "确认 .NET SDK 8 已安装，或设置 GALA11Y_DOTNET 指向 dotnet.exe。",
                new Dictionary<string, string> { ["dotnet"] = dotnet, ["logPath"] = logPath });
            TryWriteLog(logPath, $"启动失败：{ex}{Environment.NewLine}", append: true);
            return ForgeResult<BuildResult>.Failure(error);
        }

        var body = new StringBuilder()
            .AppendLine($"exitCode: {process.ExitCode}")
            .AppendLine($"timedOut: {process.TimedOut}")
            .AppendLine($"canceled: {process.Canceled}")
            .AppendLine($"durationMs: {(long)process.Duration.TotalMilliseconds}")
            .AppendLine("--- stdout ---")
            .AppendLine(process.StandardOutput)
            .AppendLine("--- stderr ---")
            .AppendLine(process.StandardError)
            .ToString();
        TryWriteLog(logPath, body, append: true);

        if (process.TimedOut)
        {
            return Failure(
                ForgeErrorCodes.Io,
                $"dotnet build 超时（{_options.Timeout.TotalSeconds:0} 秒），已终止进程树。",
                $"查看日志 {logPath}；可减少依赖或调大构建超时。",
                new Dictionary<string, string> { ["logPath"] = logPath });
        }

        if (process.Canceled)
        {
            return Failure(ForgeErrorCodes.Io, "dotnet build 已取消。", "重新运行构建。",
                new Dictionary<string, string> { ["logPath"] = logPath });
        }

        if (!process.Succeeded)
        {
            var firstError = FirstErrorLine(process.StandardError, process.StandardOutput);
            return Failure(
                ForgeErrorCodes.ArtifactInvalid,
                $"dotnet build 失败（退出码 {process.ExitCode}）：{firstError}",
                $"查看完整日志 {logPath}，修复编译错误后重试。",
                new Dictionary<string, string>
                {
                    ["exitCode"] = process.ExitCode.ToString(),
                    ["logPath"] = logPath,
                    ["stderr"] = Trim(process.StandardError, 2000),
                });
        }

        if (!Directory.Exists(outputDirectory))
        {
            return Failure(ForgeErrorCodes.ArtifactMissing, "dotnet build 返回成功，但输出目录不存在。", "检查 csproj 的 OutputPath 设置。",
                new Dictionary<string, string> { ["logPath"] = logPath });
        }

        var artifacts = Directory.EnumerateFiles(outputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artifacts.Count == 0)
        {
            return Failure(ForgeErrorCodes.ArtifactMissing, "dotnet build 返回成功，但输出目录没有 DLL。",
                "确认生成物是类库且已设置 OutputType=Library，或检查构建日志。",
                new Dictionary<string, string> { ["logPath"] = logPath, ["output"] = outputDirectory });
        }

        var metadata = new Dictionary<string, string>
        {
            ["dryRun"] = "false",
            ["command"] = commandText,
            ["projectFile"] = projectFile,
            ["outputDirectory"] = outputDirectory,
            ["configuration"] = _options.Configuration,
            ["exitCode"] = process.ExitCode.ToString(),
            ["durationMs"] = ((long)process.Duration.TotalMilliseconds).ToString(),
            ["logPath"] = logPath,
        };

        foreach (var artifact in artifacts)
        {
            _workspace.EnsureWriteAllowed(artifact);
            var name = Path.GetFileName(artifact);
            metadata[$"sha256:{name}"] = FileHashes.Sha256File(artifact);
            metadata[$"size:{name}"] = new FileInfo(artifact).Length.ToString();
        }

        _logger.Log(FlowLogLevel.Info, "04-build", "构建完成。", new Dictionary<string, string>
        {
            ["artifacts"] = string.Join(';', artifacts.Select(Path.GetFileName)),
            ["logPath"] = logPath,
        });

        return ForgeResult<BuildResult>.Success(new BuildResult
        {
            OutputDirectory = outputDirectory,
            Artifacts = artifacts,
            LogPath = logPath,
            Metadata = metadata,
        });
    }

    private ForgeResult<string> ResolveProject(GenerationResult generation)
    {
        if (string.IsNullOrWhiteSpace(generation.ProjectDirectory))
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "生成结果没有 ProjectDirectory。",
                "重新运行代码生成步骤。"));
        }

        string projectDirectory;
        try
        {
            projectDirectory = Path.GetFullPath(generation.ProjectDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"生成目录路径非法：{ex.Message}",
                "使用普通绝对路径，不要包含非法字符。"));
        }

        if (!PathSafety.IsUnder(_workspace.RootPath, projectDirectory))
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.PathUnsafe,
                $"生成目录不在工作区内：{projectDirectory}",
                $"把生成物放到工作区 {_workspace.RootPath} 内再构建。"));
        }

        if (!Directory.Exists(projectDirectory))
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactMissing,
                $"生成目录不存在：{projectDirectory}",
                "重新运行代码生成步骤。"));
        }

        if (generation.Metadata.TryGetValue("projectFile", out var explicitProject)
            && !string.IsNullOrWhiteSpace(explicitProject))
        {
            try
            {
                var resolved = PathSafety.ResolveUnder(projectDirectory, explicitProject);
                if (!File.Exists(resolved))
                {
                    return ForgeResult<string>.Failure(ForgeError.Create(
                        ForgeErrorCodes.ArtifactMissing,
                        $"指定的工程文件不存在：{resolved}",
                        "检查 generation.metadata.projectFile。"));
                }

                return ForgeResult<string>.Success(resolved);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<string>.Failure(ex.Error);
            }
        }

        var projects = Directory.EnumerateFiles(projectDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return projects.Count switch
        {
            1 => ForgeResult<string>.Success(projects[0]),
            0 => ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactMissing,
                $"生成目录里没有 .csproj：{projectDirectory}",
                "确认代码生成器产出了可构建的补丁工程。")),
            _ => ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid,
                $"生成目录里有多个 .csproj（{projects.Count} 个），无法确定构建目标。",
                "在 generation.metadata 里设置 projectFile 指定一个工程。")),
        };
    }

    private List<string> BuildArguments(string projectFile, string outputDirectory)
    {
        var arguments = new List<string>
        {
            "build",
            projectFile,
            "-c",
            _options.Configuration,
            "-o",
            outputDirectory,
            "--nologo",
            "-v:minimal",
        };

        if (_options.NoRestore)
        {
            arguments.Add("--no-restore");
        }

        if (!string.IsNullOrWhiteSpace(_options.Framework))
        {
            arguments.Add("-f");
            arguments.Add(_options.Framework);
        }

        if (!string.IsNullOrWhiteSpace(_options.Version))
        {
            arguments.Add($"-p:Version={_options.Version}");
        }

        if (_options.Properties is not null)
        {
            foreach (var pair in _options.Properties)
            {
                arguments.Add($"-p:{pair.Key}={pair.Value}");
            }
        }

        return arguments;
    }

    private static void CleanOutputDirectory(string outputDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(outputDirectory))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(outputDirectory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>依次尝试显式值、GALA11Y_DOTNET、DOTNET_HOST_PATH、D:\dotnet\dotnet.exe、PATH。</summary>
    public static string? ResolveDotnet(string? configured)
    {
        foreach (var candidate in new[]
                 {
                     configured,
                     Environment.GetEnvironmentVariable("GALA11Y_DOTNET"),
                     Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
                     @"D:\dotnet\dotnet.exe",
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (Path.IsPathFullyQualified(candidate))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            var onPath = FindOnPath(candidate);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        return FindOnPath("dotnet.exe") ?? FindOnPath("dotnet");
    }

    private static string? FindOnPath(string executable)
    {
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable) ? executable : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                if (!Path.HasExtension(executable))
                {
                    var withExe = candidate + ".exe";
                    if (File.Exists(withExe))
                    {
                        return withExe;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // PATH 里的坏路径不影响继续找。
            }
        }

        return null;
    }

    private static string ReadAssemblyName(string projectFile)
    {
        try
        {
            var document = XDocument.Load(projectFile);
            var assemblyName = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value.Trim();
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                return assemblyName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // 读不了就退回工程文件名；真正构建时 dotnet 会给出明确错误。
        }

        return Path.GetFileNameWithoutExtension(projectFile);
    }

    private static string FormatCommand(string fileName, IEnumerable<string> arguments)
        => string.Join(' ', new[] { Quote(fileName) }.Concat(arguments.Select(Quote)));

    private static string Quote(string value)
        => value.Contains(' ') || value.Contains('"') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static string FirstErrorLine(params string[] outputs)
    {
        foreach (var output in outputs)
        {
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("error CS", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("error MSB", StringComparison.OrdinalIgnoreCase))
                {
                    return Trim(line.Trim(), 300);
                }
            }
        }

        return "编译器未给出可解析的错误行";
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static bool TryWriteLog(string path, string text, bool append = false)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ForgeResult<BuildResult> Failure(
        string code,
        string message,
        string? suggestion = null,
        IReadOnlyDictionary<string, string>? details = null)
        => ForgeResult<BuildResult>.Failure(ForgeError.Create(code, message, suggestion, details));
}
