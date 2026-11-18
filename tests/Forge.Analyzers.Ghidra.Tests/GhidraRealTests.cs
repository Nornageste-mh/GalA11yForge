using GalA11yForge.Core.Models;

namespace GalA11yForge.Analyzers.Ghidra.Tests;

/// <summary>
/// 真实 Ghidra headless 冒烟测试。默认跳过；设置环境变量 GHIDRA_REAL=1 才执行。
/// 样本可用 GHIDRA_REAL_SAMPLE 覆盖，默认用 research\32-ghidra-integration\sample\SenrenBanka\SenrenBanka.exe。
/// </summary>
public sealed class GhidraRealTests
{
    [Fact]
    public async Task RealHeadless_OnSenrenBankaCopy_ProducesCandidatesArtifact()
    {
        if (Environment.GetEnvironmentVariable("GHIDRA_REAL") != "1")
        {
            return;
        }

        var sample = ResolveSample();
        Assert.True(File.Exists(sample), $"真实冒烟需要样本：{sample}；可用 GHIDRA_REAL_SAMPLE 指定。");

        using var temp = new TempDir();
        var backend = new GhidraAnalyzerBackend(new GhidraBackendOptions
        {
            WorkspaceRoot = temp.Sub("ws"),
            TimeoutSeconds = 600,
        });

        var outcome = await backend.AnalyzeDetailedAsync(new AnalyzerBackendRequest
        {
            GameDirectory = Path.GetDirectoryName(sample)!,
            ExecutablePath = sample,
            TimeoutSeconds = 600,
            Advanced = true,
        });

        Assert.Equal(GhidraAnalysisStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.CandidatesJsonPath);
        Assert.True(File.Exists(outcome.CandidatesJsonPath));
        Assert.NotNull(outcome.Document);
        Assert.True(outcome.Document!.Stats.Functions > 0);
        Assert.NotEmpty(outcome.Coverage.Covered);
        Assert.True(outcome.Elapsed.TotalSeconds > 0);
    }

    [Fact]
    public void RealLocator_FindsLocalGhidra()
    {
        if (Environment.GetEnvironmentVariable("GHIDRA_REAL") != "1")
        {
            return;
        }

        var location = GhidraLocator.Locate();
        Assert.True(location.Available, location.Error);
        Assert.NotNull(location.AnalyzeHeadlessPath);
        Assert.True(File.Exists(location.AnalyzeHeadlessPath));
    }

    private static string ResolveSample()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("GHIDRA_REAL_SAMPLE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        var relative = Path.Combine("research", "32-ghidra-integration", "sample", "SenrenBanka", "SenrenBanka.exe");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative));
    }
}
