using GalA11yForge.Gui.Core;

namespace Forge.Gui.Tests;

public sealed class AnalysisDurationHintTests
{
    [Fact]
    public void Estimate_MatchesGhidraFormula_ForKnownSample()
    {
        // 与 src/Forge.Analyzers.Ghidra/GhidraDurationEstimator.cs 的样例保持一致：
        // 4,583,168 B → 402 秒。
        var size = 4_583_168L;

        Assert.Equal(402, AnalysisDurationHint.EstimateRoundedSeconds(size));
        Assert.Equal("约 402 秒", AnalysisDurationHint.Describe(size));
    }

    [Fact]
    public void Describe_UsesMinutes_ForLongRuns()
    {
        // 8 MB：30 + 85×8 = 710 秒 → 约 11 分钟
        var size = 8L * 1024 * 1024;

        Assert.Equal(710, AnalysisDurationHint.EstimateRoundedSeconds(size));
        Assert.Equal("约 11 分钟", AnalysisDurationHint.Describe(size));
    }

    [Fact]
    public void HugeExecutable_IsClamped()
    {
        var size = 100L * 1024 * 1024;

        Assert.Equal(3600, AnalysisDurationHint.EstimateRoundedSeconds(size));
        Assert.Equal("约 60 分钟", AnalysisDurationHint.Describe(size));
    }

    [Fact]
    public void UnknownSize_IsHonest()
    {
        Assert.Equal("无法粗估（不知道可执行文件大小）", AnalysisDurationHint.Describe(0));
    }
}
