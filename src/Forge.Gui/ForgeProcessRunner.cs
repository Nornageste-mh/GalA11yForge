using System.Diagnostics;
using System.Text;
using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui;

/// <summary>子进程的一行输出；<see cref="IsError"/> 表示来自 stderr。</summary>
public sealed record ProcessLine(bool IsError, string Text);

/// <summary>
/// 异步调用 forge.exe：不阻塞 UI 线程，stdout / stderr 同时读取（避免管道写满互相等待），
/// stderr 单独收集并在结果区显示。取消时整棵进程树一起杀（build 会拉起 dotnet）。
/// </summary>
public sealed class ForgeProcessRunner
{
    private static readonly Encoding OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<ForgeRunOutcome> RunAsync(
        string executablePath,
        string commandLine,
        IReadOnlyList<string> arguments,
        IProgress<ProcessLine>? liveProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return ForgeRunOutcome.LaunchFailed(commandLine, "没有指定 forge.exe 路径。", TimeSpan.Zero);
        }

        if (!File.Exists(executablePath))
        {
            return ForgeRunOutcome.LaunchFailed(commandLine, $"找不到可执行文件：{executablePath}", TimeSpan.Zero);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding,
            WorkingDirectory = WorkingDirectoryFor(executablePath),
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                return ForgeRunOutcome.LaunchFailed(commandLine, "进程没有启动（Process.Start 返回 false）。", stopwatch.Elapsed);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return ForgeRunOutcome.LaunchFailed(commandLine, $"启动失败：{ex.Message}", stopwatch.Elapsed);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        // 两个流必须同时读：只读 stdout 时 stderr 写满管道会把子进程卡住。
        var stdoutPump = PumpAsync(process.StandardOutput, stdout, isError: false, liveProgress, cancellationToken);
        var stderrPump = PumpAsync(process.StandardError, stderr, isError: true, liveProgress, cancellationToken);

        var canceled = false;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            TryKillTree(process);
        }

        // 进程结束后管道会关闭，两个 pump 自然收尾；这里等它把剩余内容读完。
        await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
        stopwatch.Stop();

        string stdoutText;
        string stderrText;
        lock (stdout)
        {
            stdoutText = stdout.ToString();
        }

        lock (stderr)
        {
            stderrText = stderr.ToString();
        }

        if (canceled)
        {
            return ForgeRunOutcome.Canceled(commandLine, stdoutText, stderrText, stopwatch.Elapsed);
        }

        return ForgeRunOutcome.Completed(
            process.ExitCode,
            commandLine,
            stdoutText,
            stderrText,
            stopwatch.Elapsed);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder sink,
        bool isError,
        IProgress<ProcessLine>? liveProgress,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lock (sink)
                {
                    sink.AppendLine(line);
                }

                liveProgress?.Report(new ProcessLine(isError, line));
            }
        }
        catch (OperationCanceledException)
        {
            // 用户点了「停止」：进程树已被杀，读管道自然结束，这里不再上抛。
        }
        catch (ObjectDisposedException)
        {
            // 进程对象提前释放，等价于流结束。
        }
        catch (IOException)
        {
            // 管道被外部关闭；已读到的内容仍然有效。
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程已经自己退出，或没有权限；忽略，结果会如实标记为「已取消」。
        }
    }

    /// <summary>
    /// 工作目录优先用 forge.exe 所在目录（相对路径参数都以它为基准），
    /// 目录不可用时退回当前目录，避免 Process.Start 直接抛异常。
    /// </summary>
    public static string WorkingDirectoryFor(string executablePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return directory;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 落到下面的兜底
        }

        var current = Environment.CurrentDirectory;
        return Directory.Exists(current) ? current : AppContext.BaseDirectory;
    }
}
