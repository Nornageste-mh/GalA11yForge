using UnityMonoAnalyzer;
using UnityMonoAnalyzer.Reporting;
using Xunit;

namespace UnityMonoAnalyzer.Tests;

/// <summary>报告层：Markdown 结构/转义、JSON schema 与中文不转义（用假数据，不碰游戏）。</summary>
public class ReportTests
{
    [Fact]
    public void Markdown_HasExpectedSectionsAndTopCandidate()
    {
        var md = MarkdownReport.Render(Fixtures.Result(), topN: 5);

        Assert.Contains("Unity Mono", md);
        Assert.Contains("结论摘要", md);
        Assert.Contains("覆盖声明", md);
        Assert.Contains("人工确认", md);
        Assert.Contains("ShowDialogue", md);
        Assert.Contains("GameProject.StoryWindow", md);
        Assert.Contains("2021.3.31f1", md);
    }

    [Fact]
    public void Markdown_EscapesTablePipes()
    {
        var r = Fixtures.Result();
        r.Candidates[0].TypeFullName = "Odd|Type";
        r.Candidates[0].MethodFullName = "System.Void Odd|Type::Show()";

        var md = MarkdownReport.Render(r, topN: 5);

        Assert.Contains("Odd\\|Type", md);
    }

    [Fact]
    public void Markdown_SurvivesEmptyResult()
    {
        var r = new AnalysisResult
        {
            Coverage = Coverage.Build(new ScanStats(), cjkGame: false, upstreamCount: 0),
        };

        var md = MarkdownReport.Render(r, topN: 5);

        Assert.False(string.IsNullOrWhiteSpace(md));
        Assert.Contains("覆盖声明", md);
    }

    [Fact]
    public void Markdown_ShortlistSkipsSystemUiAndSampleCode()
    {
        var r = Fixtures.Result();
        r.Candidates.Insert(0, new TextCandidate
        {
            Rank = 0,
            Score = 99,
            Tier = "A",
            Kind = "dialogue",
            MethodFullName = "System.Void NodeCanvas.Examples.DemoDialogue::Show()",
            TypeFullName = "NodeCanvas.Examples.DemoDialogue",
            MethodName = "Show",
            Flags = new List<string> { "sample-code", "system-ui" },
        });

        var md = MarkdownReport.Render(r, topN: 5);

        // 短名单里不能出现被标记为示例/系统 UI 的方法（避免"照着 Top1 下 Hook"）
        var start = md.IndexOf("建议优先复核", StringComparison.Ordinal);
        Assert.True(start >= 0, "报告里必须有「建议优先复核」小节");
        var end = md.IndexOf("\n## ", start, StringComparison.Ordinal);
        var region = end < 0 ? md[start..] : md[start..end];
        Assert.DoesNotContain("DemoDialogue", region);
        Assert.Contains("StoryWindow", region);
    }

    [Fact]
    public void Json_UsesCamelCase_AndKeepsChineseReadable()
    {
        var json = JsonReport.Serialize(Fixtures.Result());

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"textSinkCalls\"", json);
        Assert.Contains("\"methodFullName\"", json);
        Assert.Contains("测试游戏", json);
        Assert.DoesNotContain("\\u6d4b", json); // 未转义的中文
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var original = Fixtures.Result();

        var restored = JsonReport.Deserialize(JsonReport.Serialize(original));

        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(original.Game.Name, restored.Game.Name);
        Assert.Equal(original.Stats.Types, restored.Stats.Types);
        Assert.Equal(original.Candidates.Count, restored.Candidates.Count);
        Assert.Equal(original.Candidates[0].MethodFullName, restored.Candidates[0].MethodFullName);
        Assert.Equal(original.Candidates[0].Score, restored.Candidates[0].Score);
        Assert.Equal(original.Coverage.Can.Count, restored.Coverage.Can.Count);
    }

    [Fact]
    public void Coverage_DeclaresBothCapabilitiesAndLimits()
    {
        var coverage = Coverage.Build(new ScanStats { TextSinkCallerMethods = 12, UpstreamCandidateMethods = 3 }, cjkGame: true, upstreamCount: 3);

        Assert.NotEmpty(coverage.Can);
        Assert.NotEmpty(coverage.Cannot);
        Assert.Equal(8, coverage.RuntimeConfirmations.Count);
        Assert.Contains(coverage.Dimensions, d => d.Id == "S6" && d.Applicable);

        var english = Coverage.Build(new ScanStats { CjkGame = false }, cjkGame: false, upstreamCount: 0);
        Assert.Contains(english.Dimensions, d => d.Id == "S6" && !d.Applicable);
    }
}
