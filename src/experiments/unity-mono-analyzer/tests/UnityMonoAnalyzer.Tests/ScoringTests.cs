using UnityMonoAnalyzer;
using UnityMonoAnalyzer.Analysis;
using Xunit;

namespace UnityMonoAnalyzer.Tests;

/// <summary>R4 打分（research/04-engines/REPORT.md §11）的规则级测试。</summary>
public class ScoringTests
{
    [Fact]
    public void FrameworkDialogueWriter_Scores105_AndIsTierA()
    {
        var s = Scoring.Score(Fixtures.DialogueWriter());

        // S1 25 + S2 20 + S3 20 + S4 10 + S5 5 + S6 10 + S7 10 + S8 5
        Assert.Equal(105, s.Total);
        Assert.Equal(25, s.Rules["S1"]);
        Assert.Equal(20, s.Rules["S2"]);
        Assert.Equal(20, s.Rules["S3"]);
        Assert.Equal(10, s.Rules["S4"]);
        Assert.Equal(5, s.Rules["S5"]);
        Assert.Equal(10, s.Rules["S6"]);
        Assert.Equal(10, s.Rules["S7"]);
        Assert.Equal(5, s.Rules["S8"]);
        Assert.Equal("A", s.Tier);
        Assert.Equal("high", s.Confidence);
        Assert.Equal("dialogue", s.Kind);
        Assert.NotEmpty(s.Evidence);
    }

    [Fact]
    public void WithoutFrameworkHit_IsNeverTierA_EvenIfTotalIsHigh()
    {
        var f = Fixtures.DialogueWriter();
        f.OnKnownFrameworkType = false;
        f.OnCustomDialogueType = false;
        f.FrameworkHitViaCaller = false;
        f.FrameworkId = "";

        var s = Scoring.Score(f);

        Assert.Equal(0, s.Rules["S1"]);
        Assert.Equal(80, s.Total);
        Assert.Equal("B", s.Tier); // 阈值 70 达到了，但 S1==0 → 降级
    }

    [Theory]
    [InlineData(70, 25, "A")]
    [InlineData(70, 0, "B")]
    [InlineData(69.99, 25, "B")]
    [InlineData(50, 0, "B")]
    [InlineData(49.99, 0, "C")]
    [InlineData(30, 0, "C")]
    [InlineData(29.99, 0, "excluded")]
    public void TierBoundaries_AreExact(double total, double s1, string expected)
    {
        Assert.Equal(expected, Scoring.TierFor(total, s1));
    }

    [Fact]
    public void DebugNaming_IsPenalizedAndKindIsDebug()
    {
        var f = new CandidateFeatures
        {
            TypeName = "DebugPanel",
            MethodName = "LogFps",
            TextSinkCalls = 1,
            IsSemanticNamed = false,
        };

        var s = Scoring.Score(f);

        Assert.Equal(-20, s.Rules["N1"]);
        Assert.Equal(5, s.Total); // S3 20 + S5 5（类型名含 Panel）− N1 20
        Assert.Equal("excluded", s.Tier);
        Assert.Equal("debug", s.Kind);
    }

    [Fact]
    public void MostlyNonDialogueLiterals_TriggerN2()
    {
        var f = new CandidateFeatures
        {
            MethodName = "Refresh",
            TextSinkCalls = 1,
            LiteralCount = 4,
            NonDialogueLiteralCount = 4,
            CjkLiteralCount = 0,
        };

        var s = Scoring.Score(f);

        Assert.Equal(-15, s.Rules["N2"]);
        Assert.Equal(5, s.Total);
    }

    [Fact]
    public void HalfNonDialogueLiterals_DoesNotTriggerN2()
    {
        var f = new CandidateFeatures
        {
            MethodName = "Refresh",
            TextSinkCalls = 1,
            LiteralCount = 4,
            NonDialogueLiteralCount = 1, // 25% < 50%
        };

        Assert.Equal(0, Scoring.Score(f).Rules["N2"]);
    }

    [Fact]
    public void AllConstantTinyLiterals_TriggerN4_AndS7StaysZero()
    {
        var f = new CandidateFeatures
        {
            MethodName = "Refresh",
            TextSinkCalls = 2,
            ConstantSinkCalls = 2,
            DynamicSinkCalls = 0,
            MaxConstantLength = 1,
        };

        var s = Scoring.Score(f);

        Assert.Equal(-10, s.Rules["N4"]);
        Assert.Equal(0, s.Rules["S7"]);
        Assert.Equal(10, s.Total);
    }

    [Fact]
    public void InitOnlyCallers_TriggerN3()
    {
        var f = new CandidateFeatures { MethodName = "Refresh", TextSinkCalls = 1, CallerCount = 2, AllCallersInitLike = true };

        Assert.Equal(-10, Scoring.Score(f).Rules["N3"]);
    }

    [Fact]
    public void RulesDictionary_AlwaysHasTwelveFixedKeys()
    {
        var s = Scoring.Score(new CandidateFeatures());
        string[] expected = { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "N1", "N2", "N3", "N4" };

        Assert.Equal(expected.Length, s.Rules.Count);
        foreach (var key in expected)
        {
            Assert.True(s.Rules.ContainsKey(key), $"缺少规则键 {key}");
        }
    }

    [Fact]
    public void Kind_ChoosesChoiceBeforeDialogue_AndMenuWhenNoDialogueSignal()
    {
        Assert.Equal("choice", Scoring.KindFor(new CandidateFeatures
        {
            TypeName = "ChoiceBox",
            TypeFullName = "GameProject.ChoiceBox",
            MethodName = "Show",
            TextSinkCalls = 1,
            IsSemanticNamed = true,
        }));

        Assert.Equal("menu", Scoring.KindFor(new CandidateFeatures
        {
            TypeName = "SavePanel",
            TypeFullName = "GameProject.SavePanel",
            MethodName = "Refresh",
            TextSinkCalls = 0,
        }));

        Assert.Equal("dialogue", Scoring.KindFor(new CandidateFeatures
        {
            TypeName = "StoryView",
            MethodName = "ShowDialogue",
            TextSinkCalls = 1,
            IsSemanticNamed = true,
        }));
    }

    [Fact]
    public void Confidence_HighRequiresDynamicSourceOrCjk()
    {
        // 85 分但只有固定短串 → medium
        var constantOnly = new CandidateFeatures
        {
            TypeName = "StoryWindow",
            MethodName = "ShowDialogue",
            ParameterCount = 0,
            OnKnownFrameworkType = true,
            IsSemanticNamed = true,
            TextSinkCalls = 1,
            ConstantSinkCalls = 1,
            MaxConstantLength = 12,
            CjkLiteralCount = 0,
            TextFieldCount = 1,
            CallerCount = 2,
        };
        var s1 = Scoring.Score(constantOnly);
        Assert.Equal(85, s1.Total);
        Assert.Equal("A", s1.Tier);
        Assert.Equal("medium", s1.Confidence);

        // 60 分 B 级但有直接写入 → medium（降级但证据成立）
        var tierB = new CandidateFeatures
        {
            TypeName = "StoryWindow",
            MethodName = "ShowDialogue",
            OnKnownFrameworkType = false,
            IsSemanticNamed = true,
            TextSinkCalls = 1,
            DynamicSinkCalls = 1,
            TextFieldCount = 1,
            CallerCount = 2,
        };
        var s2 = Scoring.Score(tierB);
        Assert.Equal(70, s2.Total);
        Assert.Equal("B", s2.Tier); // 70 分但 S1==0，只能降级
        Assert.Equal("medium", s2.Confidence);
    }

    [Fact]
    public void UpstreamOnlyCandidate_StillGetsS1FromCaller()
    {
        var f = new CandidateFeatures
        {
            TypeName = "StoryController",
            MethodName = "Play",
            FrameworkHitViaCaller = true,
            FrameworkId = "scenarioforge",
            TextSinkCalls = 0,
            IsSemanticNamed = false,
            DiscoveredBySink = false,
        };

        var s = Scoring.Score(f);

        Assert.Equal(25, s.Rules["S1"]);
        Assert.Equal(0, s.Rules["S3"]); // 自己不直接写文本，靠下游
        Assert.Equal(25, s.Total);
        Assert.Equal("excluded", s.Tier);
    }
}
