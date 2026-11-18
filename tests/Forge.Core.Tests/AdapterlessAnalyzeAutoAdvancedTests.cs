using GalA11yForge.Cli;

namespace GalA11yForge.Core.Tests;

/// <summary>
/// 没有内置适配器时，analyze 不必让用户先知道 --advanced：
/// 只要游戏根目录确实有 exe，就自动改走 Ghidra 高级分析，并在 stderr 明确说明改道。
/// 没有 exe 的目录仍诚实失败（E_ENGINE_UNSUPPORTED）。
/// </summary>
public sealed class AdapterlessAnalyzeAutoAdvancedTests
{
    private static string NewRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Game"));
        Directory.CreateDirectory(Path.Combine(root, "ws"));
        return root;
    }

    [Fact]
    public async Task Analyze_OnKnownEngineWithoutAdapter_AutoEnablesGhidra_AndExplainsInStderr()
    {
        var root = NewRoot("forge-autoadv-");
        var game = Path.Combine(root, "Game");
        var workspace = Path.Combine(root, "ws");
        File.WriteAllBytes(Path.Combine(game, "data.xp3"), new byte[64]);
        File.WriteAllBytes(Path.Combine(game, "game.exe"), new byte[64]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var exitCode = await CliApplication.RunAsync(
                ["analyze", "--game", game, "--workspace", workspace, "--dry-run", "--json"],
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            var output = stdout.ToString();
            Assert.Contains("\"autoAdvanced\":true", output);
            Assert.Contains("\"status\":\"dry_run\"", output);
            Assert.Contains("该引擎没有内置适配器，已自动改用 Ghidra 高级分析", stderr.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_UnknownDirectoryWithExe_AlsoAutoEnablesGhidra()
    {
        var root = NewRoot("forge-autoadv-unknown-exe-");
        var game = Path.Combine(root, "Game");
        var workspace = Path.Combine(root, "ws");
        File.WriteAllBytes(Path.Combine(game, "mystery.exe"), new byte[64]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var exitCode = await CliApplication.RunAsync(
                ["analyze", "--game", game, "--workspace", workspace, "--dry-run", "--json"],
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("\"autoAdvanced\":true", stdout.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Report_WithoutCachedAnalysis_AutoRunsGhidraAnalysis()
    {
        var root = NewRoot("forge-report-auto-");
        var game = Path.Combine(root, "Game");
        var workspace = Path.Combine(root, "ws");
        File.WriteAllBytes(Path.Combine(game, "data.xp3"), new byte[64]);
        File.WriteAllBytes(Path.Combine(game, "game.exe"), new byte[64]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var exitCode = await CliApplication.RunAsync(
                ["report", "--game", game, "--workspace", workspace, "--dry-run", "--json"],
                stdout,
                stderr);

            Assert.Equal(0, exitCode);
            Assert.Contains("dry-run", stdout.ToString());
            Assert.Contains("该引擎没有内置适配器，已自动改用 Ghidra 高级分析", stderr.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Analyze_DirectoryWithoutExe_StillFailsHonestly()
    {
        var root = NewRoot("forge-autoadv-noexe-");
        var game = Path.Combine(root, "Game");
        var workspace = Path.Combine(root, "ws");
        File.WriteAllText(Path.Combine(game, "readme.txt"), "not a game");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var exitCode = await CliApplication.RunAsync(
                ["analyze", "--game", game, "--workspace", workspace, "--json"],
                stdout,
                stderr);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("E_ENGINE_UNSUPPORTED", stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
