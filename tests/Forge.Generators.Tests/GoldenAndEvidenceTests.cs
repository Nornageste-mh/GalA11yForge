using GalA11yForge.Core.Models;
using GalA11yForge.Generators;

namespace GalA11yForge.Generators.Tests;

/// <summary>
/// 生成物的快照测试：
/// 1) golden —— 固定样例生成结果必须与仓库快照逐字节一致；用 FORGE_UPDATE_GOLDEN=1 更新。
/// 2) evidence —— 同一份样例的样例生成物；用 FORGE_WRITE_EVIDENCE=1 更新。
/// 快照数据都在仓库内（tests/Forge.Generators.Tests/golden、.../evidence/forge-sample）。
/// </summary>
public sealed class GoldenAndEvidenceTests : GeneratorTestBase
{
    [Fact]
    public async Task Golden_matches_repository_snapshot()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), SamplePlans.Create(), OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var actual = ReadTree(OutDir);
        if (TestEnv.UpdateGolden)
        {
            CopyTree(actual, TestEnv.GoldenDir);
            return;
        }

        var expected = ReadTree(TestEnv.GoldenDir);
        AssertTreesEqual(expected, actual, "golden");
    }

    [Fact]
    public async Task Evidence_sample_matches_current_generator()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), SamplePlans.Create(), OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var actual = ReadTree(OutDir);
        if (TestEnv.WriteEvidence)
        {
            CopyTree(actual, TestEnv.EvidenceDir);
            return;
        }

        var expected = ReadTree(TestEnv.EvidenceDir);
        AssertTreesEqual(expected, actual, "evidence");
    }

    [Fact]
    public async Task Golden_plugin_embeds_current_tool_version()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), SamplePlans.Create(), OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var plugin = ReadText(Path.Combine(OutDir, "Plugin.cs"));
        Assert.Contains("internal const string Version = \"0.2.85.0\";", plugin, StringComparison.Ordinal);
    }
}
