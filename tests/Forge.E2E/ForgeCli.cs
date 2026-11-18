using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Forge.E2E;

/// <summary>一次 CLI（或脚本）进程调用的结果。</summary>
internal sealed class CommandResult
{
    public required string Invocation { get; init; }
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public bool TimedOut { get; init; }
    public CliEnvelope? Envelope { get; init; }

    public string? Code => Envelope?.Code;

    public string Combined => string.Join(Environment.NewLine, new[] { StdOut, StdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string Tail(int lines = 40)
    {
        return ForgeCli.TailText(Combined, lines);
    }

    public string Describe(int tailLines = 40)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"命令：{Invocation}");
        builder.AppendLine($"退出码：{ExitCode}{(TimedOut ? "（超时被杀）" : string.Empty)}，耗时：{Elapsed.TotalSeconds:F1}s");

        if (Envelope is not null)
        {
            builder.AppendLine($"ok={Envelope.Ok?.ToString() ?? "?"} code={Envelope.Code ?? "(无)"}");
            if (!string.IsNullOrWhiteSpace(Envelope.Message))
            {
                builder.AppendLine($"message：{Envelope.Message}");
            }

            if (!string.IsNullOrWhiteSpace(Envelope.Suggestion))
            {
                builder.AppendLine($"suggestion：{Envelope.Suggestion}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(StdOut))
        {
            builder.AppendLine("stdout 不是可解析的 CLI JSON 信封。");
        }

        if (!string.IsNullOrWhiteSpace(StdOut))
        {
            builder.AppendLine("stdout（末尾）：");
            builder.AppendLine(ForgeCli.TailText(StdOut, tailLines));
        }

        if (!string.IsNullOrWhiteSpace(StdErr))
        {
            builder.AppendLine("stderr（末尾）：");
            builder.AppendLine(ForgeCli.TailText(StdErr, tailLines));
        }

        return builder.ToString().TrimEnd();
    }
}

/// <summary>CLI 的 --json 信封：{ ok, command, code, message, suggestion, data }。</summary>
internal sealed class CliEnvelope
{
    public bool? Ok { get; init; }
    public string? Command { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Suggestion { get; init; }
    public JsonElement? Data { get; init; }

    public static CliEnvelope? TryParse(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        // 契约要求 --json 时 stdout 只有一行 JSON；这里仍容忍前面混入日志，从后往前找。
        var lines = stdout.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('{'))
            .Reverse();

        foreach (var line in lines)
        {
            var envelope = TryParseJson(line);
            if (envelope is not null)
            {
                return envelope;
            }
        }

        return TryParseJson(stdout.Trim());
    }

    private static CliEnvelope? TryParseJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var looksLikeEnvelope = JsonHelpers.TryGetProperty(root, "ok", out _)
                || JsonHelpers.TryGetProperty(root, "code", out _)
                || JsonHelpers.TryGetProperty(root, "command", out _)
                || JsonHelpers.TryGetProperty(root, "message", out _);
            if (!looksLikeEnvelope)
            {
                return null;
            }

            return new CliEnvelope
            {
                Ok = JsonHelpers.TryGetBool(root, out var ok, "ok") ? ok : null,
                Command = JsonHelpers.TryGetString(root, out var command, "command") ? command : null,
                Code = JsonHelpers.TryGetString(root, out var code, "code") ? code : null,
                Message = JsonHelpers.TryGetString(root, out var message, "message") ? message : null,
                Suggestion = JsonHelpers.TryGetString(root, out var suggestion, "suggestion") ? suggestion : null,
                Data = JsonHelpers.CloneProperty(root, "data"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 以子进程方式调用 Forge CLI。默认自动构建 src/Forge.Cli 一次，然后直接运行 forge.dll；
/// 可通过 FORGE_CLI_DLL / FORGE_EXE 指定现成产物，或用 FORGE_SKIP_BUILD=1 跳过构建。
/// </summary>
internal static class ForgeCli
{
    public const string NotImplementedCode = "E_NOT_IMPLEMENTED";

    private static readonly Lazy<CliTarget> Target = new(ResolveTarget);
    private static readonly Lazy<string> PowerShell = new(ResolvePowerShell);
    private static readonly TimeSpan CommandTimeout = ResolveTimeout();

    public static CommandResult Run(
        string verb,
        string gameDir,
        string workspace,
        string outDir,
        IEnumerable<string>? extraArgs = null,
        TimeSpan? timeout = null)
    {
        var target = Target.Value;
        var args = new List<string>
        {
            verb,
            "--game", gameDir,
            "--workspace", workspace,
            "--out", outDir,
            "--json",
        };

        if (extraArgs is not null)
        {
            args.AddRange(extraArgs);
        }

        return Execute(target, args, E2ESandbox.RepoRoot, timeout ?? CommandTimeout);
    }

    /// <summary>用 pwsh 运行生成的 uninstall.ps1 等脚本（回滚兜底路径）。</summary>
    public static CommandResult RunScript(string scriptPath, IEnumerable<string> scriptArgs, string workingDirectory)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-File", scriptPath };
        args.AddRange(scriptArgs);
        return Execute(new CliTarget(PowerShell.Value, IsDll: false), args, workingDirectory, CommandTimeout);
    }

    public static string TailText(string text, int lines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var all = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var tail = all.Skip(Math.Max(0, all.Length - lines))
            .Select(line => line.Length <= 1000 ? line : line[..1000] + "…");
        return string.Join(Environment.NewLine, tail).TrimEnd();
    }

    private static CommandResult Execute(CliTarget target, IReadOnlyList<string> args, string workingDirectory, TimeSpan timeout)
    {
        var fullArgs = new List<string>();
        string fileName;
        if (target.IsDll)
        {
            fileName = E2ESandbox.DotnetPath;
            fullArgs.Add(target.FileName);
            fullArgs.AddRange(args);
        }
        else
        {
            fileName = target.FileName;
            fullArgs.AddRange(args);
        }

        var invocation = Display(fileName, fullArgs);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in fullArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法启动进程：{invocation}{Environment.NewLine}{exception.Message}", exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var timedOut = false;
        var timeoutMs = (int)Math.Min(int.MaxValue, Math.Max(1_000, timeout.TotalMilliseconds));
        if (!process.WaitForExit(timeoutMs))
        {
            timedOut = true;
            TryKill(process);
            if (!process.WaitForExit(30_000))
            {
                throw new InvalidOperationException($"进程超时且无法终止：{invocation}");
            }
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        stopwatch.Stop();

        return new CommandResult
        {
            Invocation = invocation,
            ExitCode = process.ExitCode,
            StdOut = stdout,
            StdErr = stderr,
            Elapsed = stopwatch.Elapsed,
            TimedOut = timedOut,
            Envelope = CliEnvelope.TryParse(stdout),
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // 进程可能已经退出；交给上层用 HasExited 判断。
        }
    }

    private static CliTarget ResolveTarget()
    {
        var explicitDll = Environment.GetEnvironmentVariable("FORGE_CLI_DLL");
        if (!string.IsNullOrWhiteSpace(explicitDll))
        {
            return RequireFile(explicitDll, "FORGE_CLI_DLL", isDll: true);
        }

        var explicitExe = Environment.GetEnvironmentVariable("FORGE_EXE");
        if (!string.IsNullOrWhiteSpace(explicitExe))
        {
            return RequireFile(explicitExe, "FORGE_EXE", isDll: false);
        }

        var cliProject = Path.Combine(E2ESandbox.RepoRoot, "src", "Forge.Cli", "Forge.Cli.csproj");
        var preferred = Path.Combine(E2ESandbox.RepoRoot, "src", "Forge.Cli", "bin", "Debug", "net8.0", "forge.dll");
        var skipBuild = string.Equals(Environment.GetEnvironmentVariable("FORGE_SKIP_BUILD"), "1", StringComparison.Ordinal);

        if (!skipBuild || !File.Exists(preferred))
        {
            if (!File.Exists(cliProject))
            {
                throw new InvalidOperationException($"找不到 CLI 工程：{cliProject}");
            }

            var build = Execute(
                new CliTarget(E2ESandbox.DotnetPath, IsDll: false),
                new[] { "build", cliProject, "-c", "Debug", "--nologo", "-v", "quiet" },
                E2ESandbox.RepoRoot,
                TimeSpan.FromMinutes(5));

            if (build.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"构建 CLI 失败（退出码 {build.ExitCode}）。{Environment.NewLine}{build.Describe(60)}");
            }
        }

        if (File.Exists(preferred))
        {
            return new CliTarget(preferred, IsDll: true);
        }

        var binRoot = Path.Combine(E2ESandbox.RepoRoot, "src", "Forge.Cli", "bin");
        var found = Directory.Exists(binRoot)
            ? Directory.EnumerateFiles(binRoot, "*.dll", SearchOption.AllDirectories)
                .Where(path =>
                    Path.GetFileName(path).Equals("forge.dll", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Equals("Forge.Cli.dll", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (found is null)
        {
            throw new InvalidOperationException(
                "找不到 CLI 产物（forge.dll）。请先手动运行：dotnet build src/Forge.Cli -c Debug，"
                + "或设置 FORGE_CLI_DLL 指向已构建的 dll。");
        }

        return new CliTarget(found, IsDll: true);
    }

    private static CliTarget RequireFile(string path, string source, bool isDll)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"{source} 指向的文件不存在：{full}");
        }

        return new CliTarget(full, isDll);
    }

    private static string ResolvePowerShell()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("FORGE_PWSH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : "pwsh";
    }

    private static TimeSpan ResolveTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("FORGE_E2E_TIMEOUT_SEC");
        if (int.TryParse(raw, out var seconds) && seconds is >= 30 and <= 3600)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromMinutes(10);
    }

    private static string Display(string fileName, IEnumerable<string> args)
    {
        static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
        return string.Join(' ', new[] { Quote(fileName) }.Concat(args.Select(Quote)));
    }

    private sealed record CliTarget(string FileName, bool IsDll);
}
