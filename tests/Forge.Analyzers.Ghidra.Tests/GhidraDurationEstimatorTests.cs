using GalA11yForge.Analyzers.Ghidra;

namespace GalA11yForge.Analyzers.Ghidra.Tests;

public sealed class GhidraDurationEstimatorTests
{
    [Fact]
    public void UnknownSize_ReturnsStartupOnly()
    {
        Assert.Equal(GhidraDurationEstimator.StartupSeconds, GhidraDurationEstimator.EstimateSeconds(0));
        Assert.Equal(30, GhidraDurationEstimator.EstimateRoundedSeconds(-1));
        Assert.Contains("无法粗估", GhidraDurationEstimator.Describe(0));
    }

    [Fact]
    public void SmallExecutable_AddsPerMegabyteAllowance()
    {
        // 256 KB = 0.25 MB → 30 + 0.25 * 85 = 51.25 → 51 秒
        Assert.Equal(51, GhidraDurationEstimator.EstimateRoundedSeconds(256 * 1024));
        Assert.Equal("约 51 秒", GhidraDurationEstimator.Describe(256 * 1024));
    }

    [Fact]
    public void EightMegabyteExecutable_IsNearTwelveMinutes()
    {
        var seconds = GhidraDurationEstimator.EstimateRoundedSeconds(8L * 1024 * 1024);
        Assert.Equal(710, seconds);
        Assert.Equal("约 11 分钟", GhidraDurationEstimator.Describe(8L * 1024 * 1024));
    }

    [Fact]
    public void HugeExecutable_IsClamped()
    {
        Assert.Equal(
            GhidraDurationEstimator.MaximumSeconds,
            GhidraDurationEstimator.EstimateSeconds(100L * 1024 * 1024));
        Assert.Equal(3600, GhidraDurationEstimator.EstimateRoundedSeconds(100L * 1024 * 1024));
    }

    [Fact]
    public void SuggestTimeout_AddsMarginOverEstimateAndCurrentValue()
    {
        // 4.37 MB → 402 秒预估 → 402 + 60 = 462 秒
        var fourMegabytes = 4_583_168L;
        Assert.Equal(402, GhidraDurationEstimator.EstimateRoundedSeconds(fourMegabytes));
        Assert.Equal(462, GhidraDurationEstimator.SuggestTimeoutSeconds(fourMegabytes, currentTimeoutSeconds: 30));

        // 当前值已经很大时，至少再加 30 秒
        Assert.Equal(630, GhidraDurationEstimator.SuggestTimeoutSeconds(fourMegabytes, currentTimeoutSeconds: 600));
    }

    [Fact]
    public void SuggestTimeout_UnknownSizeFallsBackToStartupPlusMargin()
    {
        // 未知大小按“只有启动耗时 30 秒”算 → max(90, 60) = 90
        Assert.Equal(90, GhidraDurationEstimator.SuggestTimeoutSeconds(0, currentTimeoutSeconds: 30));
    }
}
