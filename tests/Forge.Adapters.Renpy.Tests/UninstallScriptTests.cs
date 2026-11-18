using System.Diagnostics;
using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Renpy.Tests;

/// <summary>
/// 卸载脚本行为回归：不只做静态检查，而是在临时目录里真正跑一遍生成的 uninstall.ps1。
/// 覆盖：登记文件 + .rpyc + 空目录清理；被改动文件保留与 -Force；缺清单时的幂等重跑；
/// 以及“绝不碰 game/ 下其他 .rpy/.rpyc”的边界。
/// </summary>
public sealed class UninstallScriptTests
{
    private readonly RenpyAdapter _adapter = new();

    [Fact]
    public async Task Uninstall_RemovesRegisteredFilesRpycAndDirectory_AndIsIdempotent()
    {
        var powershell = FindWindowsPowerShell();
        if (powershell is null)
        {
            return;
        }

        using var fixture = new RenpyFixture("6.99");
        var installRoot = await InstallAsync(fixture);
        var platform = Path.Combine(installRoot, "game", "a11y_platform");
        File.WriteAllText(Path.Combine(installRoot, "a11y_speech.log"), "[A11y对话] test\n");
        File.WriteAllText(Path.Combine(installRoot, "deploy_plan.json"), "{ \"files\": [] }\n");

        var first = RunUninstall(powershell, installRoot);
        Assert.True(first.ExitCode == 0, Describe(first));
        Assert.False(Directory.Exists(platform), "game/a11y_platform 应被删除");
        Assert.False(File.Exists(Path.Combine(installRoot, "a11y_speech.log")), "运行日志应被删除");
        Assert.False(File.Exists(Path.Combine(installRoot, "deploy_plan.json")), "干净卸载后部署清单副本也应删除");
        Assert.False(File.Exists(Path.Combine(installRoot, "a11y_manifest.json")), "干净卸载后清单应自删");
        Assert.False(File.Exists(Path.Combine(installRoot, "uninstall.ps1")), "登记文件应删除");
        Assert.False(File.Exists(Path.Combine(installRoot, "README-A11Y.txt")), "登记文件应删除");
        Assert.False(File.Exists(Path.Combine(installRoot, "a11y_say.vbs")), "登记文件应删除");

        // game/ 下的游戏本体文件一个都不能少、也不能变。
        Assert.Equal("label start:\n    return\n", File.ReadAllText(Path.Combine(installRoot, "game", "script.rpy")));
        Assert.Equal("GAME-OWN-RPYC", File.ReadAllText(Path.Combine(installRoot, "game", "other.rpyc")));
    }

    [Fact]
    public async Task Uninstall_PreservesModifiedFile_AndForceRemovesIt()
    {
        var powershell = FindWindowsPowerShell();
        if (powershell is null)
        {
            return;
        }

        using var fixture = new RenpyFixture("8.5");
        var installRoot = await InstallAsync(fixture);
        var platform = Path.Combine(installRoot, "game", "a11y_platform");
        var manifest = Path.Combine(installRoot, "a11y_manifest.json");
        var edited = Path.Combine(platform, "00_contract.rpy");
        File.AppendAllText(edited, "\n# 用户改过这一行\n", new UTF8Encoding(false));

        var first = RunUninstall(powershell, installRoot);
        Assert.True(first.ExitCode == 0, Describe(first));
        Assert.True(File.Exists(edited), "哈希不符的登记文件默认必须保留");
        Assert.True(File.Exists(manifest), "有跳过的文件时清单必须保留，便于再次处理");
        Assert.True(Directory.Exists(platform));
        Assert.False(File.Exists(Path.Combine(platform, "leftover.rpyc")), ".rpyc 即使对应 .rpy 被保留也应清掉");

        var forced = RunUninstall(powershell, installRoot, "-Force");
        Assert.True(forced.ExitCode == 0, Describe(forced));
        Assert.False(File.Exists(edited), "-Force 应删除被改动的登记文件");
        Assert.False(Directory.Exists(platform));
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task Uninstall_WithoutManifest_CleansRpycLeftByOldVersion_AndStaysRepeatable()
    {
        var powershell = FindWindowsPowerShell();
        if (powershell is null)
        {
            return;
        }

        using var fixture = new RenpyFixture("6.99");
        var installRoot = await InstallAsync(fixture);
        var platform = Path.Combine(installRoot, "game", "a11y_platform");

        // 模拟旧版卸载：登记 .rpy 已被删，只剩 .rpyc 和目录，清单也没了。
        foreach (var file in Directory.GetFiles(platform, "*.rpy"))
        {
            File.Delete(file);
        }

        File.Delete(Path.Combine(installRoot, "a11y_manifest.json"));

        var first = RunUninstall(powershell, installRoot);
        Assert.True(first.ExitCode == 0, Describe(first));
        Assert.False(Directory.Exists(platform), "缺清单也要能把旧版遗留的 .rpyc 清干净");

        var second = RunUninstall(powershell, installRoot);
        Assert.True(second.ExitCode == 0, Describe(second));
    }

    [Fact]
    public async Task Uninstall_ExplicitlyMissingManifest_ReturnsExitCodeTwo()
    {
        var powershell = FindWindowsPowerShell();
        if (powershell is null)
        {
            return;
        }

        using var fixture = new RenpyFixture("6.99");
        var installRoot = await InstallAsync(fixture);
        var missing = Path.Combine(installRoot, "does-not-exist.json");

        var result = RunUninstall(powershell, installRoot, "-Manifest", missing);
        Assert.True(result.ExitCode == 2, Describe(result));
    }

    private async Task<string> InstallAsync(RenpyFixture fixture)
    {
        var context = fixture.Context();
        var detection = await _adapter.DetectAsync(context);
        Assert.True(detection.Ok, detection.Error?.Message);
        var analysis = await _adapter.AnalyzeAsync(context, detection.Value!);
        Assert.True(analysis.Ok, analysis.Error?.Message);
        var plan = await _adapter.PlanAsync(context, analysis.Value!);
        Assert.True(plan.Ok, plan.Error?.Message);
        var generated = await _adapter.GenerateAsync(context, plan.Value!, fixture.GeneratedDirectory, CancellationToken.None);
        Assert.True(generated.Ok, generated.Error?.Message);

        var source = generated.Value!.ProjectDirectory;
        var installRoot = Path.Combine(fixture.Root, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installRoot);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(installRoot, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        File.WriteAllText(Path.Combine(installRoot, "game", "script.rpy"), "label start:\n    return\n");
        File.WriteAllText(Path.Combine(installRoot, "game", "other.rpyc"), "GAME-OWN-RPYC");

        var platform = Path.Combine(installRoot, "game", "a11y_platform");
        foreach (var file in Directory.GetFiles(platform, "*.rpy"))
        {
            File.WriteAllText(Path.ChangeExtension(file, ".rpyc"), "compiled");
        }

        File.WriteAllText(Path.Combine(platform, "leftover.rpyc"), "leftover");
        return installRoot;
    }

    private static string? FindWindowsPowerShell()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static (int ExitCode, string Output) RunUninstall(string powershell, string root, params string[] extra)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(root, "uninstall.ps1"));

        // 从 pwsh 里启动 Windows PowerShell 会继承 PS7 的 PSModulePath，5.1 可能因此
        // 找不到 Get-FileHash。这里还原成双击运行时的系统模块路径，贴近真实用户环境。
        startInfo.Environment["PSModulePath"] = string.Join(
            ';',
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell", "Modules"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules"));
        foreach (var argument in extra)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "uninstall.ps1 超时（60s）");
        }

        return (process.ExitCode, stdout + stderr);
    }

    private static string Describe((int ExitCode, string Output) result) =>
        $"uninstall.ps1 exit={result.ExitCode}\n{result.Output}";
}
