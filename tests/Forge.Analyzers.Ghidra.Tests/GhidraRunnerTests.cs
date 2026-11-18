using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Analyzers.Ghidra.Tests;

public sealed class GhidraRunnerTests
{
    private static string WriteFakeHeadless(TempDir temp, string body)
    {
        var directory = Path.Combine(temp.Root, "fake-headless");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "analyzeHeadless.bat");
        File.WriteAllText(path, "@echo off\r\n" + body.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n");
        return path;
    }

    private static GhidraRunRequest CreateRequest(
        TempDir temp,
        string headlessPath,
        int timeoutSeconds = 600,
        string? outputDirectory = null,
        string? projectDirectory = null)
    {
        var import = Path.Combine(temp.Root, "game.exe");
        File.WriteAllText(import, "MZ");

        return new GhidraRunRequest
        {
            AnalyzeHeadlessPath = headlessPath,
            ProjectDirectory = projectDirectory ?? Path.Combine(temp.Root, "ws", "project"),
            ProjectName = "gala11y",
            ImportPath = import,
            ScriptDirectory = Path.Combine(temp.Root, "ws", "scripts"),
            OutputDirectory = outputDirectory ?? Path.Combine(temp.Root, "ws", "out"),
            TimeoutSeconds = timeoutSeconds,
        };
    }

    private static string Workspace(TempDir temp) => Path.Combine(temp.Root, "ws");

    [Fact]
    public async Task Run_Success_CapturesStdoutStderrAndWritesLogs()
    {
        using var temp = new TempDir();
        var headless = WriteFakeHeadless(temp, "echo hello-stdout\necho hello-stderr 1>&2\nexit /b 0");
        var request = CreateRequest(temp, headless);

        var result = await new GhidraRunner(Workspace(temp)).RunAsync(request);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(GhidraRunStatus.Succeeded, result.Status);
        Assert.Contains("hello-stdout", result.StandardOutput);
        Assert.Contains("hello-stderr", result.StandardError);
        Assert.True(File.Exists(result.StandardOutputPath));
        Assert.True(File.Exists(result.StandardErrorPath));
        Assert.Contains("hello-stdout", await File.ReadAllTextAsync(result.StandardOutputPath));
        Assert.Contains("hello-stderr", await File.ReadAllTextAsync(result.StandardErrorPath));
    }

    [Fact]
    public async Task Run_NonZeroExit_IsMappedNotThrown()
    {
        using var temp = new TempDir();
        var headless = WriteFakeHeadless(temp, "echo gh failed 1>&2\nexit /b 3");
        var request = CreateRequest(temp, headless);

        var result = await new GhidraRunner(Workspace(temp)).RunAsync(request);

        Assert.Equal(GhidraRunStatus.NonZeroExit, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task Run_Timeout_KillsProcessTreeAndReturnsTimedOut()
    {
        using var temp = new TempDir();
        var headless = WriteFakeHeadless(temp, "ping -n 30 127.0.0.1 >nul\nexit /b 0");
        var request = CreateRequest(temp, headless, timeoutSeconds: 1);

        var result = await new GhidraRunner(Workspace(temp)).RunAsync(request);

        Assert.True(result.TimedOut);
        Assert.False(result.Canceled);
        Assert.Equal(GhidraRunStatus.TimedOut, result.Status);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Run_Cancel_KillsProcessTreeAndReturnsCanceled()
    {
        using var temp = new TempDir();
        var headless = WriteFakeHeadless(temp, "ping -n 30 127.0.0.1 >nul\nexit /b 0");
        var request = CreateRequest(temp, headless);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(400));

        var result = await new GhidraRunner(Workspace(temp)).RunAsync(request, cts.Token);

        Assert.True(result.Canceled);
        Assert.False(result.TimedOut);
        Assert.Equal(GhidraRunStatus.Canceled, result.Status);
    }

    [Fact]
    public async Task Run_OutputOutsideWorkspace_IsRejectedBeforeStarting()
    {
        using var temp = new TempDir();
        var headless = WriteFakeHeadless(temp, "exit /b 0");
        var outside = temp.Sub("outside");
        var request = CreateRequest(temp, headless, outputDirectory: outside);

        await Assert.ThrowsAsync<ForgePathException>(
            () => new GhidraRunner(Workspace(temp)).RunAsync(request));
    }

    [Fact]
    public async Task Run_ProjectInsideGameDirectory_IsRejected()
    {
        using var temp = new TempDir();
        var headless = WriteFakeHeadless(temp, "exit /b 0");
        var root = temp.Sub("ws");
        var gameDirectory = Path.Combine(root, "game");
        Directory.CreateDirectory(gameDirectory);
        var request = CreateRequest(
            temp,
            headless,
            projectDirectory: Path.Combine(gameDirectory, "project"));

        await Assert.ThrowsAsync<ForgePathException>(
            () => new GhidraRunner(root, gameDirectory).RunAsync(request));
    }

    [Fact]
    public async Task Run_MissingImport_ThrowsFileNotFound()
    {
        using var temp = new TempDir();
        var headless = WriteFakeHeadless(temp, "exit /b 0");
        var request = CreateRequest(temp, headless);
        var broken = new GhidraRunRequest
        {
            AnalyzeHeadlessPath = request.AnalyzeHeadlessPath,
            ProjectDirectory = request.ProjectDirectory,
            ProjectName = request.ProjectName,
            ImportPath = Path.Combine(temp.Root, "missing.exe"),
            ScriptDirectory = request.ScriptDirectory,
            OutputDirectory = request.OutputDirectory,
        };

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => new GhidraRunner(Workspace(temp)).RunAsync(broken));
    }

    [Fact]
    public async Task Run_StartFailure_IsReportedAsStartFailed()
    {
        using var temp = new TempDir();
        var missingExe = Path.Combine(temp.Root, "no-such-headless.exe");
        var request = CreateRequest(temp, missingExe);

        var result = await new GhidraRunner(Workspace(temp)).RunAsync(request);

        Assert.True(result.StartFailed);
        Assert.False(result.Started);
        Assert.Equal(GhidraRunStatus.StartFailed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.StartError));
    }
}
