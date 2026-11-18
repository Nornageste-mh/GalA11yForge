using UnityMonoAnalyzer;
using UnityMonoAnalyzer.Analysis;
using Xunit;

namespace UnityMonoAnalyzer.Tests;

/// <summary>多 `*_Data` 候选排序：启动器不一定输给真游戏，但"启动器痕迹"必须被压下去。</summary>
public class RankerTests
{
    [Fact]
    public void Rank_PrefersDialogueGame_OverLauncherWithMoreTextWrites()
    {
        // 真实教训（research/01-norna）：七日启动器 37 处 set_text > 真游戏 26 处
        var launcher = Fixtures.Candidate(
            "SevenDaysLauncher_Data",
            Fixtures.Metrics(sinks: 37, frameworks: 0, dialogueTypes: 0, cjkMethods: 21, launcherTypes: 4),
            @"C:\fake\SevenDaysLauncher.exe", depth: 0);
        var game = Fixtures.Candidate(
            "The seven days i spent with you_Data",
            Fixtures.Metrics(sinks: 26, frameworks: 20, dialogueTypes: 5, cjkMethods: 23, launcherTypes: 0),
            @"C:\fake\game\The seven days i spent with you.exe", depth: 1);

        var ranked = CandidateRanker.Rank(new[] { launcher, game }, "我与你共度的七日");

        Assert.Equal(2, ranked.Count);
        Assert.Equal("The seven days i spent with you_Data", ranked[0].Candidate.DataDirName);
        Assert.True(ranked[0].Score > ranked[1].Score);
        Assert.False(CandidateRanker.IsAmbiguous(ranked));
    }

    [Fact]
    public void Rank_PrefersDirectoryNameMatch_WhenMetricsAreIdentical()
    {
        // To the Moon 顶层无 exe：三个嵌套 Unity 作指标几乎相同，靠目录名匹配选中主作
        var metrics = Fixtures.Metrics(sinks: 245, frameworks: 446, dialogueTypes: 22, cjkMethods: 0, types: 1318, methods: 7477);
        var main = Fixtures.Candidate("To the Moon_Data", metrics, depth: 1);
        var minisode = Fixtures.Candidate("Minisode_1 (SerenityForge)_Data", Fixtures.Metrics(sinks: 243, frameworks: 446, dialogueTypes: 22, cjkMethods: 0, types: 1313, methods: 7462), depth: 1);

        var ranked = CandidateRanker.Rank(new[] { minisode, main }, "To the Moon");

        Assert.Equal("To the Moon_Data", ranked[0].Candidate.DataDirName);
        Assert.True(ranked[0].NameMatched);
        Assert.False(ranked[1].NameMatched);
    }

    [Fact]
    public void Rank_IsDeterministic_AndDetectsAmbiguity()
    {
        var a = Fixtures.Candidate("A_Data", Fixtures.Metrics(), depth: 1);
        var b = Fixtures.Candidate("B_Data", Fixtures.Metrics(), depth: 1);

        var first = CandidateRanker.Rank(new[] { a, b }, "Root");
        var second = CandidateRanker.Rank(new[] { b, a }, "Root");

        Assert.Equal(first[0].Candidate.DataDirName, second[0].Candidate.DataDirName);
        Assert.True(CandidateRanker.IsAmbiguous(first), "两个指标完全相同的候选必须报并列");
    }

    [Fact]
    public void Rank_PutsScanFailureLast()
    {
        var broken = Fixtures.Candidate("Broken_Data", Fixtures.Metrics(), depth: 1);
        broken.ScanError = "BadImageFormatException";
        broken.QuickMetrics.Scanned = false; // 扫描失败用 Scanned=false 表达（-5000）
        var ok = Fixtures.Candidate("Good_Data", Fixtures.Metrics(sinks: 1), depth: 1);

        var ranked = CandidateRanker.Rank(new[] { broken, ok }, "Root");

        Assert.Equal("Good_Data", ranked[0].Candidate.DataDirName);
        Assert.True(ranked[1].Score < ranked[0].Score, "扫描失败的候选必须排在最后");
    }

    [Fact]
    public void Rank_LauncherDataDirLosesEvenWithHugeCjkCount()
    {
        var launcher = Fixtures.Candidate(
            "GameLauncher_Data",
            Fixtures.Metrics(sinks: 200, frameworks: 0, dialogueTypes: 0, cjkMethods: 200, launcherTypes: 0),
            @"C:\fake\GameLauncher.exe", depth: 0);
        var game = Fixtures.Candidate(
            "Game_Data",
            Fixtures.Metrics(sinks: 10, frameworks: 0, dialogueTypes: 1, cjkMethods: 10, launcherTypes: 0),
            @"C:\fake\Game.exe", depth: 0);

        var ranked = CandidateRanker.Rank(new[] { launcher, game }, "Game");

        Assert.Equal("Game_Data", ranked[0].Candidate.DataDirName);
    }
}
