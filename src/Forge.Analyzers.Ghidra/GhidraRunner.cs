using System.Diagnostics;
using System.Text;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Analyzers.Ghidra;

public enum GhidraRunStatus
{
    /// <summary>进程退出码 0。</summary>
    Succeeded = 0,

    /// <summary>进程根本没能启动（缺少 cmd.exe/bat、权限不足等）。</summary>
    StartFailed = 1,

    /// <summary>超过 TimeoutSeconds，已杀进程树。</summary>
    TimedOut = 2,

    /// <summary>调用方取消，已杀进程树。</summary>
    Canceled = 3,

    /// <summary>进程正常结束但退出码非 0。</summary>
    NonZeroExit = 4,
}

/// <summary>一次 analyzeHeadless 子进程调用的输入。所有路径都必须是绝对路径且落在工作区内。</summary>
public sealed class GhidraRunRequest
{
    public required string AnalyzeHeadlessPath { get; init; }
    public required string ProjectDirectory { get; init; }
    public required string ProjectName { get; init; }
    public required string ImportPath { get; init; }
    public required string ScriptDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public string PostScript { get; init; } = GhidraScriptResources.CandidateHookDumpFileName;
    public int TimeoutSeconds { get; init; } = 600;
    public int AnalysisTimeoutSeconds { get; init; } = 900;
    public int MaxCpu { get; init; } = 4;
    public string? MaxMemory { get; init; }
    public bool DeleteProject { get; init; } = true;

    /// <summary>stdout 落盘路径；默认 &lt;ProjectDirectory&gt;/stdout.log。必须落在工作区内。</summary>
    public string? StandardOutputPath { get; init; }

    /// <summary>stderr 落盘路径；默认 &lt;ProjectDirectory&gt;/stderr.log。必须落在工作区内。</summary>
    public string? StandardErrorPath { get; init; }

    public IReadOnlyDictionary<string, string?>? Environment { get; init; }
}

public sealed class GhidraRunResult
{
    public required GhidraCommand Command { get; init; }
    public required string StandardOutputPath { get; init; }
    public required string StandardErrorPath { get; init; }
    public bool Started { get; init; }
    public int ExitCode { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string StandardOutput { get; init; } = "";
    public string StandardError { get; init; } = "";
    public string? StartError { get; init; }

    public bool TimedOut { get; init; }
    public bool Canceled { get; init; }
    public bool StartFailed { get; init; }

    public GhidraRunStatus Status =>
        StartFailed ? GhidraRunStatus.StartFailed
        : Canceled ? GhidraRunStatus.Canceled
        : TimedOut ? GhidraRunStatus.TimedOut
        : ExitCode != 0 ? GhidraRunStatus.NonZeroExit
        : GhidraRunStatus.Succeeded;

    /// <summary>stderr 优先、再 stdout 的最后若干行，用于错误消息。</summary>
    public string Tail(int maxChars = 800, int maxLines = 12)
    {
        var text = string.Join(
            "\n",
            (StandardError + "\n" + StandardOutput)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .TakeLast(maxLines));
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}

/// <summary>
/// 启动 Ghidra headless 子进程：流式收集 stdout/stderr、硬超时、取消时杀整棵进程树、
/// 把日志写进工作区。不做任何「分析成功」的判断——那是后端与产物解析的职责。
/// </summary>
public sealed class GhidraRunner
{
    private readonly string _allowedRoot;
    private readonly string? _gameDirectory;

    /// <param name="allowedRoot">允许写入的根（通常是工作区根或 work 目录）。</param>
    /// <param name="gameDirectory">游戏目录；任何写入落在这里都会被拒绝（Steam 只读红线）。</param>
    public GhidraRunner(string allowedRoot, string? gameDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);
        _allowedRoot = PathSafety.NormalizeRoot(allowedRoot);
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory)
            ? null
            : PathSafety.NormalizeRoot(gameDirectory);
    }

    public async Task<GhidraRunResult> RunAsync(GhidraRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AnalyzeHeadlessPath);

        // 一切写入都先过路径检查：工程目录、Candidates 输出、日志。
        var projectDirectory = PathSafety.EnsureWriteAllowed(_allowedRoot, request.ProjectDirectory, _gameDirectory);
        var outputDirectory = PathSafety.EnsureWriteAllowed(_allowedRoot, request.OutputDirectory, _gameDirectory);
        var stdoutPath = PathSafety.EnsureWriteAllowed(
            _allowedRoot,
            request.StandardOutputPath ?? Path.Combine(projectDirectory, "stdout.log"),
            _gameDirectory);
        var stderrPath = PathSafety.EnsureWriteAllowed(
            _allowedRoot,
            request.StandardErrorPath ?? Path.Combine(projectDirectory, "stderr.log"),
            _gameDirectory);

        var importPath = Path.GetFullPath(request.ImportPath);
        if (!File.Exists(importPath))
        {
            throw new FileNotFoundException($"待分析的可执行文件不存在：{importPath}", importPath);
        }

        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stderrPath)!);

        var command = GhidraCommandBuilder.Build(new GhidraCommandRequest
        {
            AnalyzeHeadlessPath = request.AnalyzeHeadlessPath,
            ProjectDirectory = projectDirectory,
            ProjectName = request.ProjectName,
            ImportPath = importPath,
            ScriptDirectory = request.ScriptDirectory,
            OutputDirectory = outputDirectory,
            PostScript = request.PostScript,
            AnalysisTimeoutSeconds = request.AnalysisTimeoutSeconds,
            MaxCpu = request.MaxCpu,
            DeleteProject = request.DeleteProject,
        });

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutLock = new object();
        var stderrLock = new object();

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(request.MaxMemory))
        {
            startInfo.Environment["GHIDRA_HEADLESS_MAXMEM"] = request.MaxMemory;
        }

        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdoutLock)
                {
                    stdout.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrLock)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        try
        {
            if (!process.Start())
            {
                return StartFailedResult(command, stdoutPath, stderrPath, "Process.Start 返回 false。");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return StartFailedResult(command, stdoutPath, stderrPath, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        var canceled = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds));
        linked.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = cancellationToken.IsCancellationRequested;
            timedOut = !canceled;
            KillTree(process);
            // 杀完之后必须收敛，否则 read 线程与 Job 句柄可能泄漏。
            await Task.Run(() =>
            {
                try
                {
                    process.WaitForExit(10_000);
                }
                catch (InvalidOperationException)
                {
                    // 进程已释放/未启动，忽略。
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }

        stopwatch.Stop();

        var exitCode = -1;
        try
        {
            if (process.HasExited)
            {
                // WaitForExit() 无参版本保证异步输出事件全部派发完；有超时版本不保证。
                // 前面已经确认退出（或被我们杀掉），这里只等管道排空，最多 10s。
                process.WaitForExit(10_000);
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        string stdoutText;
        string stderrText;
        lock (stdoutLock)
        {
            stdoutText = stdout.ToString();
        }

        lock (stderrLock)
        {
            stderrText = stderr.ToString();
        }

        TryWrite(stdoutPath, stdoutText);
        TryWrite(stderrPath, stderrText);

        return new GhidraRunResult
        {
            Command = command,
            StandardOutputPath = stdoutPath,
            StandardErrorPath = stderrPath,
            Started = true,
            ExitCode = exitCode,
            Elapsed = stopwatch.Elapsed,
            StandardOutput = stdoutText,
            StandardError = stderrText,
            TimedOut = timedOut,
            Canceled = canceled,
        };
    }

    private static GhidraRunResult StartFailedResult(
        GhidraCommand command,
        string stdoutPath,
        string stderrPath,
        string error)
    {
        TryWrite(stdoutPath, "");
        TryWrite(stderrPath, error);
        return new GhidraRunResult
        {
            Command = command,
            StandardOutputPath = stdoutPath,
            StandardErrorPath = stderrPath,
            Started = false,
            StartFailed = true,
            StartError = error,
            ExitCode = -1,
            StandardError = error,
        };
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程可能刚好自行退出；清理失败不掩盖上游的超时/取消语义。
        }
    }

    private static void TryWrite(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // 日志落盘失败不应把「分析结果」变成异常；调用方仍能拿到内存中的输出。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
