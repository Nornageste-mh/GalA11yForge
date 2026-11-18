using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GalA11yForge.Build;

/// <summary>
/// Process 实现：异步读取 stdout/stderr，超时或调用方取消时杀掉整棵进程树。
/// 不吞异常：启动失败（找不到 dotnet）原样抛给调用方转成 E_DEPENDENCY_MISSING。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("进程文件名不能为空。", nameof(fileName));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "超时必须大于 0。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var startedAt = Stopwatch.GetTimestamp();

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        var canceled = false;

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            canceled = cancellationToken.IsCancellationRequested;
            TryKillTree(process);

            // 杀掉后必须等进程真正退出，否则输出流可能永远读不完。
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 进程不响应 Kill：继续收集已有输出，让上层看到失败而不是挂死。
            }
        }

        string stdout;
        string stderr;
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            stdout = stdoutTask.Result;
            stderr = stderrTask.Result;
        }
        catch (TimeoutException)
        {
            stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
        }

        return new ProcessResult
        {
            ExitCode = process.HasExited ? process.ExitCode : -1,
            StandardOutput = stdout,
            StandardError = stderr,
            TimedOut = timedOut,
            Canceled = canceled,
            Duration = Stopwatch.GetElapsedTime(startedAt),
        };
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
        catch (InvalidOperationException)
        {
            // 已经退出或句柄失效，忽略。
        }
        catch (Win32Exception)
        {
            // 权限不足杀不掉：上层仍会按超时失败处理。
        }
    }
}
