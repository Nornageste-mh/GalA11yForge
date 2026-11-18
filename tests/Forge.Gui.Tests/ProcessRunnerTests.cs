using GalA11yForge.Gui;
using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>
/// 真实子进程：异步、两路输出、退出码、取消。
/// 用 cmd.exe 跑临时脚本，不依赖 forge.exe 是否已构建（那是 E2E 测试的事）。
/// </summary>
public sealed class ProcessRunnerTests
{
    private static string ComSpec
        => Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";

    private sealed class Recorder : IProgress<ProcessLine>
    {
        public List<ProcessLine> Lines { get; } = new();

        public void Report(ProcessLine value)
        {
            lock (Lines)
            {
                Lines.Add(value);
            }
        }
    }

    private static async Task<string> CreateScriptAsync(string body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-gui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "probe.cmd");
        await File.WriteAllTextAsync(path, "@echo off\r\n" + body);
        return path;
    }

    private static void Cleanup(string scriptPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(scriptPath);
            if (directory is not null)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论。
        }
    }

    [Fact]
    public async Task MissingExecutable_IsLaunchFailure()
    {
        var runner = new ForgeProcessRunner();

        var outcome = await runner.RunAsync(
            @"C:\definitely-not-here\forge.exe", "forge detect", new[] { "detect" }, null, CancellationToken.None);

        Assert.Equal(ForgeRunStatus.LaunchFailed, outcome.Status);
        Assert.Contains("找不到可执行文件", outcome.Message);
        Assert.False(outcome.IsGood);
    }

    [Fact]
    public async Task BlankExecutable_IsLaunchFailure()
    {
        var runner = new ForgeProcessRunner();

        var outcome = await runner.RunAsync("  ", "forge detect", Array.Empty<string>(), null, CancellationToken.None);

        Assert.Equal(ForgeRunStatus.LaunchFailed, outcome.Status);
        Assert.Contains("没有指定", outcome.Message);
    }

    [Fact]
    public async Task CapturesBothStreamsAndEnvelope()
    {
        if (!File.Exists(ComSpec))
        {
            return;
        }

        var script = await CreateScriptAsync(
            "@echo {\"ok\":true,\"command\":\"detect\",\"message\":\"ok\"}\r\n@echo oops 1>&2\r\n");
        try
        {
            var runner = new ForgeProcessRunner();
            var progress = new Recorder();

            var outcome = await runner.RunAsync(
                ComSpec, "cmd /c probe.cmd", new[] { "/c", script }, progress, CancellationToken.None);

            Assert.Equal(ForgeRunStatus.Success, outcome.Status);
            Assert.NotNull(outcome.Envelope);
            Assert.Equal("detect", outcome.Envelope!.Command);
            Assert.True(outcome.HasStderr);
            Assert.Contains("oops", outcome.Stderr);

            lock (progress.Lines)
            {
                Assert.Contains(progress.Lines, line => !line.IsError && line.Text.Contains("ok"));
                Assert.Contains(progress.Lines, line => line.IsError && line.Text.Contains("oops"));
            }
        }
        finally
        {
            Cleanup(script);
        }
    }

    [Fact]
    public async Task NonZeroExitCodeMapsToFailure()
    {
        if (!File.Exists(ComSpec))
        {
            return;
        }

        var script = await CreateScriptAsync("exit /b 7\r\n");
        try
        {
            var runner = new ForgeProcessRunner();

            var outcome = await runner.RunAsync(ComSpec, "cmd /c probe.cmd", new[] { "/c", script }, null, CancellationToken.None);

            Assert.Equal(7, outcome.ExitCode);
            Assert.Equal(ForgeRunStatus.Failed, outcome.Status);
            Assert.False(outcome.IsGood);
        }
        finally
        {
            Cleanup(script);
        }
    }

    [Fact]
    public async Task CancellationKillsChildAndReportsCanceled()
    {
        if (!File.Exists(ComSpec))
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var runner = new ForgeProcessRunner();

        var outcome = await runner.RunAsync(
            ComSpec,
            "cmd /c ping -n 6 127.0.0.1",
            new[] { "/c", "ping", "-n", "6", "127.0.0.1" },
            null,
            cts.Token);

        Assert.Equal(ForgeRunStatus.Canceled, outcome.Status);
        Assert.Equal(-1, outcome.ExitCode);
    }

    [Fact]
    public void WorkingDirectoryIsExecutableFolder()
    {
        var expected = Path.GetDirectoryName(Path.GetFullPath(ComSpec));

        Assert.Equal(expected, ForgeProcessRunner.WorkingDirectoryFor(ComSpec));
    }

    [Fact]
    public void WorkingDirectoryFallsBackWhenExecutablePathIsUnusable()
    {
        var fallback = ForgeProcessRunner.WorkingDirectoryFor(@"Z:\no-such-folder\forge.exe");

        Assert.True(Directory.Exists(fallback));
        Assert.Equal(Environment.CurrentDirectory, fallback);
    }
}
